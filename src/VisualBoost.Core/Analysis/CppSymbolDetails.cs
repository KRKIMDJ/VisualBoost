using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VisualBoost.Core.Analysis;

// 표시용 정보만 보강합니다. 타입 추론이나 오버로드의 의미적 동일성을 판정하지 않습니다.
internal static class CppSymbolDetails
{
    private static readonly Regex ScopePattern = new(@"\b(?:namespace|class|struct|union)\s+(?:\w+_API\s+)?(?<name>[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)", RegexOptions.Compiled);
    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

    public static IReadOnlyList<SourceSymbolLocation> Enrich(string source, IReadOnlyList<SourceSymbolLocation> symbols, string maskedSource, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0) return symbols;
        var code = maskedSource;
        var result = new List<SourceSymbolLocation>(symbols.Count);
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
                    result.Add(new SourceSymbolLocation(location.Name, location.Path, location.Line,
                        nameStart - lineStart + 1, location.Kind, owner, signature));
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
                scopes.Add(lastScope is not null && text.IndexOf('(', lastScope.Index) < 0 && text.LastIndexOf(')') < lastScope.Index
                    ? lastScope.Groups["name"].Value : string.Empty);
                currentScope = string.Join("::", scopes.Where(s => s.Length != 0));
                statement.Clear();
            }
            else if (c == '}')
            {
                if (scopes.Count > 0) scopes.RemoveAt(scopes.Count - 1);
                currentScope = string.Join("::", scopes.Where(s => s.Length != 0));
                statement.Clear();
            }
            else if (c == ';') statement.Clear();
            else if (statement.Length < 4096) statement.Append(c);
            if (c == '\n') { line++; lineStart = offset + 1; }
        }
        return result;
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
