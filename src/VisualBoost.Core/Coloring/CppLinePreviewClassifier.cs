using System;
using System.Collections.Generic;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.Coloring;

/// <summary>코드 미리보기 한 줄의 표시 구간 종류입니다. 앞의 다섯은 편집기 구문 색, 뒤의 여섯은 의미 기반 색상 그룹에 대응합니다.</summary>
public enum CodePreviewKind { Keyword, Comment, String, Number, Preprocessor, Type, Variable, Function, Macro, EnumMember, Namespace }

public readonly struct CodePreviewSpan
{
    public CodePreviewSpan(int start, int length, CodePreviewKind kind) { Start = start; Length = length; Kind = kind; }
    public int Start { get; }
    public int Length { get; }
    public CodePreviewKind Kind { get; }
}

/// <summary>
/// 결과 목록의 C++ 코드 미리보기 한 줄을 색 구간으로 나눕니다. 화면 표시 전용입니다.
/// 줄 하나만 보므로 앞 줄에서 시작한 블록 주석·raw 문자열은 알 수 없습니다. 식별자 종류는 편집기 빠른 색상 보조
/// (<see cref="CppQuickColorScanner"/>)와 같은 형태 추정(호출 형태, 선언 형태, <c>#define</c>)에, 호출자가 주는 이름 판정
/// (Solution 이름 인덱스 등)을 더해 정합니다. 이 클래스는 판정의 출처를 모르며, 판정이 없으면 형태만으로 분류합니다.
/// 구간이 없는 글자는 기본색입니다.
/// </summary>
public static class CppLinePreviewClassifier
{
    /// <summary>이보다 긴 줄은 색을 입히지 않습니다. 미리보기는 앞뒤를 잘라 수백 자 안쪽이므로 비정상 입력만 걸립니다.</summary>
    public const int MaximumLength = 4096;

    // 템플릿 인수 목록으로 볼 최대 길이입니다. 더 길면 비교식일 가능성이 커 호출 형태로 보지 않습니다.
    private const int MaximumTemplateArguments = 160;

    private static readonly HashSet<string> Keywords = new(("alignas alignof and and_eq asm auto bitand bitor bool break case catch char char8_t char16_t char32_t " +
        "class compl concept const consteval constexpr constinit const_cast continue co_await co_return co_yield decltype default delete do double " +
        "dynamic_cast else enum explicit export extern false final float for friend goto if import inline int interface __interface long module mutable " +
        "namespace new noexcept not not_eq nullptr operator or or_eq override private protected public register reinterpret_cast requires return " +
        "short signed sizeof static static_assert static_cast struct switch template this thread_local throw true try typedef typeid typename union " +
        "unsigned using virtual void volatile wchar_t while xor xor_eq").Split(' '), StringComparer.Ordinal);

    // 템플릿 인수 목록에는 올 수 없는 논리·비트 연산 대체 단어입니다.
    private static readonly HashSet<string> AlternativeOperators = new("and and_eq bitand bitor compl not not_eq or or_eq xor xor_eq".Split(' '), StringComparer.Ordinal);

    /// <param name="line">분류할 코드 한 줄입니다.</param>
    /// <param name="resolveName">
    /// 식별자 이름의 종류를 알려 주는 판정입니다. 확실하지 않으면 null을 돌려줘야 합니다. 키워드가 아닌 식별자마다 부르므로
    /// 호출자가 결과를 캐시합니다. null이면 줄 형태만으로 분류합니다.
    /// </param>
    public static IReadOnlyList<CodePreviewSpan> Classify(string? line, Func<string, CodePreviewKind?>? resolveName = null)
    {
        if (string.IsNullOrEmpty(line) || line!.Length > MaximumLength) return Array.Empty<CodePreviewSpan>();
        var text = line;
        var spans = new List<CodePreviewSpan>();
        var hints = new Dictionary<int, SemanticColorKind>();
        foreach (var hint in CppQuickColorScanner.Scan(text)) hints[hint.Start] = hint.Kind;

        var i = SkipSpaces(text, 0);
        if (i < text.Length && text[i] == '#')
        {
            // 전처리 지시문 이름까지를 한 구간으로 칠하고, include 대상은 문자열로 칠합니다. 나머지는 일반 코드처럼 이어서 나눕니다.
            var start = i;
            var word = SkipSpaces(text, i + 1);
            var end = word;
            while (end < text.Length && char.IsLetter(text[end])) end++;
            spans.Add(new CodePreviewSpan(start, end - start, CodePreviewKind.Preprocessor));
            i = end;
            var directive = text.Substring(word, end - word);
            if (directive is "include" or "include_next" or "import")
            {
                var open = SkipSpaces(text, i);
                if (open < text.Length && text[open] == '<')
                {
                    var close = text.IndexOf('>', open + 1);
                    var stop = close < 0 ? text.Length : close + 1;
                    spans.Add(new CodePreviewSpan(open, stop - open, CodePreviewKind.String));
                    i = stop;
                }
            }
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                spans.Add(new CodePreviewSpan(i, text.Length - i, CodePreviewKind.Comment));
                break;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = close < 0 ? text.Length : close + 2;
                spans.Add(new CodePreviewSpan(i, stop - i, CodePreviewKind.Comment));
                i = stop;
                continue;
            }

            if (TryLiteral(text, i, out var literalEnd))
            {
                spans.Add(new CodePreviewSpan(i, literalEnd - i, CodePreviewKind.String));
                i = literalEnd;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < text.Length && IsIdentifierPart(text[i])) i++;
                var word = text.Substring(start, i - start);
                if (Keywords.Contains(word)) spans.Add(new CodePreviewSpan(start, i - start, CodePreviewKind.Keyword));
                else if (IdentifierKind(text, start, i, word, hints, resolveName) is { } kind) spans.Add(new CodePreviewSpan(start, i - start, kind));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                var start = i++;
                while (i < text.Length)
                {
                    var next = text[i];
                    if (IsIdentifierPart(next) || next is '.' or '\'') i++;
                    // 지수 부호(1e+5, 0x1p-3)는 숫자에 붙입니다.
                    else if (next is '+' or '-' && text[i - 1] is 'e' or 'E' or 'p' or 'P' && !IsHex(text, start, i - 1)) i++;
                    else break;
                }

                spans.Add(new CodePreviewSpan(start, i - start, CodePreviewKind.Number));
                continue;
            }

            i++;
        }

        return spans.AsReadOnly();
    }

    /// <summary>
    /// 같은 이름으로 찾은 심볼 종류들을 구간 종류 하나로 줄입니다. 이름 판정을 만드는 쪽이 씁니다. 표시 보조라 확실한 경우만 답합니다.
    /// 타입은 생성자(함수)와 같은 이름이므로 타입+함수는 타입입니다. 변수·모르는 종류가 섞이거나 서로 다른 그룹이 섞이면 null입니다.
    /// </summary>
    public static CodePreviewKind? KindOfSymbols(IEnumerable<SourceSymbolKind> kinds)
    {
        if (kinds is null) throw new ArgumentNullException(nameof(kinds));
        var any = false;
        var type = false;
        var function = false;
        CodePreviewKind? other = null;
        foreach (var kind in kinds)
        {
            any = true;
            switch (kind)
            {
                case SourceSymbolKind.Type or SourceSymbolKind.Class or SourceSymbolKind.Struct or SourceSymbolKind.Union or SourceSymbolKind.Enum:
                    type = true;
                    break;
                case SourceSymbolKind.Function:
                    function = true;
                    break;
                case SourceSymbolKind.Macro when other is null or CodePreviewKind.Macro:
                    other = CodePreviewKind.Macro;
                    break;
                case SourceSymbolKind.Namespace when other is null or CodePreviewKind.Namespace:
                    other = CodePreviewKind.Namespace;
                    break;
                default:
                    return null;
            }
        }

        if (!any) return null;
        if (other is not null) return type || function ? null : other;
        return type ? CodePreviewKind.Type : CodePreviewKind.Function;
    }

    /// <summary>
    /// 키워드가 아닌 식별자의 종류입니다. 줄 형태로 확정한 종류(선언·<c>#define</c>·네임스페이스·열거자)가 가장 확실하고 이름 판정이 다음입니다.
    /// 호출 형태(<c>Name(</c>, <c>Name&lt;...&gt;(</c>)로 추정한 함수는 생성자 호출이나 함수형 매크로일 수 있어 이름 판정이 있으면 그것을 따릅니다.
    /// 이름 판정은 줄 위치를 모르므로 두 경우에는 쓰지 않습니다. 이름 색인에 지역 변수가 없어 같은 이름의 함수로 칠할 수 있으므로
    /// 판정이 함수이면 호출 형태에서만 받고, 멤버 접근(<c>.</c>, <c>-&gt;</c>) 뒤의 이름은 다른 곳의 같은 이름과 무관하므로 판정을 묻지 않습니다.
    /// </summary>
    private static CodePreviewKind? IdentifierKind(string text, int start, int end, string word, Dictionary<int, SemanticColorKind> hints,
        Func<string, CodePreviewKind?>? resolveName)
    {
        var shaped = hints.TryGetValue(start, out var hint) ? ToPreviewKind(hint) : null;
        if (shaped is not null and not CodePreviewKind.Function) return shaped;
        // 편집기 빠른 색상 보조와 같이 대문자만인 이름은 매크로일 수 있어 호출 형태여도 함수로 칠하지 않습니다.
        var call = shaped is not null || (!IsUpperName(word) && IsTemplateCall(text, end));
        var named = IsMemberAccess(text, start) ? null : resolveName?.Invoke(word);
        if (named is { } kind && (kind != CodePreviewKind.Function || call)) return kind;
        return call ? CodePreviewKind.Function : null;
    }

    /// <summary>이름 바로 앞이 멤버 접근 연산자(<c>.</c>, <c>-&gt;</c>)인지 봅니다. <c>::</c> 한정 이름은 멤버 접근이 아닙니다.</summary>
    private static bool IsMemberAccess(string text, int start)
    {
        var i = start - 1;
        while (i >= 0 && text[i] is ' ' or '\t') i--;
        if (i < 0) return false;
        return text[i] == '.' || (text[i] == '>' && i > 0 && text[i - 1] == '-');
    }

    /// <summary>
    /// 이름 바로 뒤가 템플릿 인수 목록과 여는 괄호(<c>Cast&lt;T&gt;(</c>)이면 호출 형태로 봅니다. 비교식(<c>a &lt; b &amp;&amp; c &gt; (d)</c>)을
    /// 거르도록 인수 목록에는 이름·숫자·<c>::</c>·포인터·참조·쉼표·공백만 허용하고, <c>&amp;&amp;</c>는 인수 끝(<c>T&amp;&amp;&gt;</c>)에서만 받습니다.
    /// 논리 연산 대체 단어(<c>and</c>, <c>or</c> 등)가 있어도 비교식으로 봅니다.
    /// </summary>
    private static bool IsTemplateCall(string text, int index)
    {
        index = SkipSpaces(text, index);
        if (index >= text.Length || text[index] != '<') return false;
        var depth = 0;
        for (var limit = Math.Min(text.Length, index + MaximumTemplateArguments); index < limit; index++)
        {
            var c = text[index];
            if (c == '<') depth++;
            else if (c == '>')
            {
                if (--depth > 0) continue;
                var open = SkipSpaces(text, index + 1);
                return open < text.Length && text[open] == '(';
            }
            else if (c == '&' && index + 1 < text.Length && text[index + 1] == '&')
            {
                var next = SkipSpaces(text, index + 2);
                if (next >= text.Length || text[next] is not ('>' or ',')) return false;
                index++;
            }
            else if ((char.IsLetter(c) || c == '_') && (index == 0 || !IsIdentifierPart(text[index - 1])))
            {
                var wordEnd = index + 1;
                while (wordEnd < text.Length && IsIdentifierPart(text[wordEnd])) wordEnd++;
                if (AlternativeOperators.Contains(text.Substring(index, wordEnd - index))) return false;
                index = wordEnd - 1;
            }
            else if (!IsIdentifierPart(c) && c is not (' ' or '\t' or ':' or '*' or '&' or ',')) return false;
        }

        return false;
    }

    private static bool IsUpperName(string word)
    {
        foreach (var c in word)
            if (!char.IsUpper(c) && !char.IsDigit(c) && c != '_') return false;
        return true;
    }

    /// <summary>색상 그룹에 대응하는 구간 종류입니다. 표시 전용이므로 나중에 늘어난 모르는 그룹은 예외 대신 색 없이 둡니다.</summary>
    private static CodePreviewKind? ToPreviewKind(SemanticColorKind kind) => kind switch
    {
        SemanticColorKind.Type => CodePreviewKind.Type,
        SemanticColorKind.Variable => CodePreviewKind.Variable,
        SemanticColorKind.Function => CodePreviewKind.Function,
        SemanticColorKind.Macro => CodePreviewKind.Macro,
        SemanticColorKind.EnumMember => CodePreviewKind.EnumMember,
        SemanticColorKind.Namespace => CodePreviewKind.Namespace,
        _ => null,
    };

    /// <summary>문자·문자열 리터럴(접두어 u8/u/U/L, raw 문자열 포함)이면 끝 위치를 돌려줍니다. 닫히지 않았으면 줄 끝까지입니다.</summary>
    private static bool TryLiteral(string text, int start, out int end)
    {
        end = start;
        // 식별자 중간 글자는 접두어로 보지 않습니다.
        if (start > 0 && IsIdentifierPart(text[start - 1])) return false;
        var quote = start;
        if (quote + 1 < text.Length && text[quote] == 'u' && text[quote + 1] == '8') quote += 2;
        else if (text[quote] is 'u' or 'U' or 'L') quote++;
        var raw = quote < text.Length && text[quote] == 'R';
        if (raw) quote++;
        if (quote >= text.Length) return false;
        var delimiter = text[quote];
        if (delimiter != '"' && (raw || delimiter != '\'')) return false;

        if (raw)
        {
            var open = text.IndexOf('(', quote + 1);
            if (open < 0 || open - quote > 17)
            {
                end = text.Length;
                return true;
            }

            var suffix = ")" + text.Substring(quote + 1, open - quote - 1) + "\"";
            var close = text.IndexOf(suffix, open + 1, StringComparison.Ordinal);
            end = close < 0 ? text.Length : close + suffix.Length;
        }
        else
        {
            var i = quote + 1;
            while (i < text.Length && text[i] != delimiter) i += text[i] == '\\' && i + 1 < text.Length ? 2 : 1;
            end = i < text.Length ? i + 1 : text.Length;
        }

        // 사용자 정의 리터럴 접미사("abc"sv)도 리터럴에 붙입니다.
        while (end < text.Length && IsIdentifierPart(text[end])) end++;
        return true;
    }

    private static bool IsHex(string text, int start, int exponent) =>
        exponent - start >= 2 && text[start] == '0' && text[start + 1] is 'x' or 'X' && text[exponent] is 'e' or 'E';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int SkipSpaces(string text, int index)
    {
        while (index < text.Length && text[index] is ' ' or '\t') index++;
        return index;
    }
}
