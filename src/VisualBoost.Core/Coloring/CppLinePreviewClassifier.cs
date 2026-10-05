using System;
using System.Collections.Generic;

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
/// 줄 하나만 보므로 앞 줄에서 시작한 블록 주석·raw 문자열은 알 수 없습니다. 식별자 종류는 문서 밖 자료를 읽지 않고
/// 편집기 빠른 색상 보조(<see cref="CppQuickColorScanner"/>)와 같은 형태 추정(호출 형태, 선언 형태, <c>#define</c>)만 씁니다.
/// 구간이 없는 글자는 기본색입니다.
/// </summary>
public static class CppLinePreviewClassifier
{
    /// <summary>이보다 긴 줄은 색을 입히지 않습니다. 미리보기는 앞뒤를 잘라 수백 자 안쪽이므로 비정상 입력만 걸립니다.</summary>
    public const int MaximumLength = 4096;

    private static readonly HashSet<string> Keywords = new(("alignas alignof and and_eq asm auto bitand bitor bool break case catch char char8_t char16_t char32_t " +
        "class compl concept const consteval constexpr constinit const_cast continue co_await co_return co_yield decltype default delete do double " +
        "dynamic_cast else enum explicit export extern false final float for friend goto if import inline int interface __interface long module mutable " +
        "namespace new noexcept not not_eq nullptr operator or or_eq override private protected public register reinterpret_cast requires return " +
        "short signed sizeof static static_assert static_cast struct switch template this thread_local throw true try typedef typeid typename union " +
        "unsigned using virtual void volatile wchar_t while xor xor_eq").Split(' '), StringComparer.Ordinal);

    public static IReadOnlyList<CodePreviewSpan> Classify(string? line)
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
                else if (hints.TryGetValue(start, out var kind) && ToPreviewKind(kind) is { } preview) spans.Add(new CodePreviewSpan(start, i - start, preview));
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
