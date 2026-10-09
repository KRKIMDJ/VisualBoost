using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VisualBoost.Core.Analysis;

// 표시용 정보(소속·시그니처)를 보강하고 함수 본문 안의 변수·함수 위치를 뺍니다. 타입 추론이나 오버로드의 의미적 동일성을 판정하지 않습니다.
internal static class CppSymbolDetails
{
    private static readonly Regex ScopePattern = new(@"\b(?:namespace|class|struct|union)\s+(?:\w+_API\s+)?(?<name>[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)", RegexOptions.Compiled);
    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

    // 함수 본문 머리에서 마지막 ')' 뒤에 올 수 있는 것: 멤버 한정자와 뒤에 쓰는 반환 형식입니다. 생성자 초기화 목록은 마지막 초기화의 ')'로 끝납니다.
    private static readonly Regex FunctionHeadSuffix = new(@"^(?>\s*)(?:(?:const|volatile|mutable|noexcept|override|final|&&|&)(?>\s*))*(?:->[^{};]*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <remarks>
    /// 줄 단위 분석은 함수 본문 안의 지역 변수(<c>const int32 Score = …;</c>)와 지역 객체(<c>FScopeLock Lock(&amp;Mutex);</c>, 함수로 보임)까지
    /// 등록해, 엔진 규모에서 이름 인덱스 위치의 상당수가 이런 오등록이었습니다(2026-10-10 측정: 변수가 전체의 43%). 중괄호를 따라 함수 본문 안이면
    /// 변수·함수 위치를 뺍니다. 중괄호 짝이 파일 끝에서 맞지 않으면(<c>#if</c>/<c>#else</c> 갈래가 여는 중괄호를 하나씩 가진 경우 등) 본문 판정이
    /// 뒤로 밀려 함수 밖 심볼까지 잃을 수 있으므로 그 파일은 빼지 않습니다.
    /// </remarks>
    public static IReadOnlyList<SourceSymbolLocation> Enrich(string source, IReadOnlyList<SourceSymbolLocation> symbols, string maskedSource, CancellationToken cancellationToken,
        Func<string, string>? intern = null)
    {
        intern ??= static value => value;
        if (symbols.Count == 0) return symbols;
        var code = maskedSource;
        var result = new List<SourceSymbolLocation>(symbols.Count);
        // 함수 본문 안에서 나온 변수·함수 위치(result 안 번호)입니다. 파일 끝에서 중괄호가 맞으면 뺍니다.
        var local = new List<int>();
        // scopes와 같은 깊이로, 그 블록이 함수 본문(또는 그 안)인지입니다.
        var functionBlocks = new List<bool>();
        var functionDepth = 0;
        var unbalanced = false;
        var lineOffsets = new List<int> { 0 };
        for (var i = 0; i < code.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (code[i] == '\n') lineOffsets.Add(i + 1);
        }
        var byOffset = symbols.GroupBy(s => lineOffsets[Math.Min(lineOffsets.Count - 1, Math.Max(0, s.Line - 1))] + Math.Max(0, s.Column - 1))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var scopes = new List<string>();
        var currentScope = string.Empty;
        var statement = new StringBuilder();
        var line = 1;
        var lineStart = 0;
        var directive = false;
        for (var offset = 0; offset <= code.Length; offset++)
        {
            if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (offset == lineStart && offset < code.Length)
            {
                var first = offset;
                while (first < code.Length && code[first] is ' ' or '\t') first++;
                directive = directive || (first < code.Length && code[first] == '#');
            }
            if (byOffset.TryGetValue(offset, out var locations))
            {
                var scope = currentScope;
                foreach (var location in locations)
                {
                    var start = lineStart + Math.Max(0, location.Column - 1);
                    var nameStart = start;
                    var end = code.IndexOf('\n', lineStart);
                    if (end < 0) end = code.Length;
                    if (start >= end) { result.Add(location); continue; }
                    // 분석기가 한정 이름 시작을 반환하는 경우 마지막 이름까지 위치를 바로잡습니다.
                    var nameEnd = start;
                    while (nameEnd < end && (char.IsLetterOrDigit(code[nameEnd]) || code[nameEnd] is '_' or ':' or '~')) nameEnd++;
                    var qualified = code.Substring(start, nameEnd - start);
                    var separator = qualified.LastIndexOf("::", StringComparison.Ordinal);
                    var owner = scope;
                    if (separator >= 0)
                    {
                        var explicitOwner = qualified.Substring(0, separator);
                        owner = scope.Length == 0 || explicitOwner.StartsWith(scope + "::", StringComparison.Ordinal) || explicitOwner == scope
                            ? explicitOwner : scope + "::" + explicitOwner;
                        nameStart += separator + 2;
                    }
                    var signature = location.Kind == SourceSymbolKind.Function ? ReadSignature(source, code, nameEnd) : string.Empty;
                    if (functionDepth > 0 && location.Kind is SourceSymbolKind.Variable or SourceSymbolKind.Function) local.Add(result.Count);
                    result.Add(new SourceSymbolLocation(intern(location.Name), location.Path, location.Line,
                        nameStart - lineStart + 1, location.Kind, intern(owner), intern(signature)));
                }
            }
            if (offset == code.Length) break;
            var c = code[offset];
            if (directive)
            {
                if (c == '\n')
                {
                    var previous = offset - 1;
                    if (previous >= 0 && code[previous] == '\r') previous--;
                    directive = previous >= 0 && code[previous] == '\\';
                    line++; lineStart = offset + 1;
                }
                continue;
            }
            if (c == '{')
            {
                var text = statement.ToString();
                var matches = ScopePattern.Matches(text);
                // 함수 본문 안의 로컬 블록을 클래스/네임스페이스로 오인하지 않습니다.
                var lastScope = matches.Count > 0 ? matches[matches.Count - 1] : null;
                var named = lastScope is not null && text.IndexOf('(', lastScope.Index) < 0 && text.LastIndexOf(')') < lastScope.Index;
                scopes.Add(named ? lastScope!.Groups["name"].Value : string.Empty);
                var function = functionDepth > 0 || !named && IsFunctionHead(text);
                functionBlocks.Add(function);
                if (function) functionDepth++;
                currentScope = string.Join("::", scopes.Where(s => s.Length != 0));
                statement.Clear();
            }
            else if (c == '}')
            {
                if (scopes.Count > 0)
                {
                    scopes.RemoveAt(scopes.Count - 1);
                    if (functionBlocks[functionBlocks.Count - 1]) functionDepth--;
                    functionBlocks.RemoveAt(functionBlocks.Count - 1);
                }
                else
                {
                    unbalanced = true;
                }

                currentScope = string.Join("::", scopes.Where(s => s.Length != 0));
                statement.Clear();
            }
            else if (c == ';') statement.Clear();
            else if (statement.Length < 4096) statement.Append(c);
            if (c == '\n') { line++; lineStart = offset + 1; }
        }

        if (local.Count == 0 || unbalanced || scopes.Count != 0) return result;
        var kept = new List<SourceSymbolLocation>(result.Count - local.Count);
        var next = 0;
        for (var i = 0; i < result.Count; i++)
        {
            if (next < local.Count && local[next] == i) { next++; continue; }
            kept.Add(result[i]);
        }

        return kept;
    }

    /// <summary>
    /// 여는 중괄호 앞 문장이 함수 본문 머리인지 봅니다: 마지막 ')' 뒤에 멤버 한정자나 뒤에 쓰는 반환 형식만 있습니다(<c>void F() const</c>,
    /// <c>A::A() : X(1)</c>, <c>auto F() -&gt; int</c>, 람다 <c>[](int V)</c>). <c>struct alignas(16) FVec</c>처럼 괄호 뒤에 이름이 오면 아닙니다.
    /// </summary>
    private static bool IsFunctionHead(string text)
    {
        var close = text.LastIndexOf(')');
        return close >= 0 && FunctionHeadSuffix.IsMatch(text.Substring(close + 1));
    }

    private static string ReadSignature(string source, string code, int offset)
    {
        var whitespaceLimit = Math.Min(code.Length, offset + 4096);
        while (offset < whitespaceLimit && char.IsWhiteSpace(code[offset])) offset++;
        if (offset == code.Length || code[offset] != '(') return string.Empty;
        var start = offset;
        var depth = 0;
        var limit = Math.Min(code.Length, start + 4096);
        for (; offset < limit; offset++)
        {
            if (code[offset] == '(') depth++;
            else if (code[offset] == ')' && --depth == 0)
            {
                offset++;
                var suffix = offset;
                while (suffix < limit && code[suffix] != ';' && code[suffix] != '{' && code[suffix] != '\n') suffix++;
                return Space.Replace(source.Substring(start, suffix - start).Trim(), " ");
            }
        }
        return string.Empty;
    }

    // 원래 줄과 열을 보존하여 표시 위치가 이동하지 않도록 합니다.
    internal static string Mask(string source, CancellationToken cancellationToken)
    {
        var chars = source.ToCharArray();
        var block = false;
        var quote = '\0';
        string? rawEnd = null;
        for (var i = 0; i < chars.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var c = source[i];
            if (rawEnd is not null)
            {
                if (i + rawEnd.Length <= source.Length && string.CompareOrdinal(source, i, rawEnd, 0, rawEnd.Length) == 0)
                {
                    for (var j = 0; j < rawEnd.Length; j++) chars[i + j] = ' ';
                    i += rawEnd.Length - 1; rawEnd = null;
                }
                else if (c != '\n' && c != '\r') chars[i] = ' ';
                continue;
            }
            if (block)
            {
                if (c == '*' && i + 1 < chars.Length && source[i + 1] == '/') { chars[i] = chars[++i] = ' '; block = false; }
                else if (c != '\n' && c != '\r') chars[i] = ' ';
                continue;
            }
            if (quote != '\0')
            {
                if (c != '\n' && c != '\r') chars[i] = ' ';
                if (c == '\\' && i + 1 < chars.Length) { i++; if (source[i] != '\n' && source[i] != '\r') chars[i] = ' '; }
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c == '/' && i + 1 < chars.Length && source[i + 1] == '/')
            {
                while (i < chars.Length && source[i] != '\n')
                {
                    if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    chars[i++] = ' ';
                }
                i--; continue;
            }
            if (c == '/' && i + 1 < chars.Length && source[i + 1] == '*') { chars[i] = chars[++i] = ' '; block = true; continue; }
            if (c == '"' && i > 0 && source[i - 1] == 'R')
            {
                var opening = source.IndexOf('(', i + 1, Math.Min(17, source.Length - i - 1));
                if (opening >= 0 && opening - i <= 17) { rawEnd = ")" + source.Substring(i + 1, opening - i - 1) + "\""; chars[i] = ' '; continue; }
            }
            if (c is '"' or '\'') { quote = c; chars[i] = ' '; }
        }
        return new string(chars);
    }
}
