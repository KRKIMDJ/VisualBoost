using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 소스의 조건부 컴파일(<c>#if</c>·<c>#elif</c>) 식에 쓴 매크로 이름을 글자로 모읍니다. 공유 PCH 없이 분석한 파일이 PCH가 정의하는 매크로에
/// 기대는지 알아볼 때 씁니다.
/// </summary>
/// <remarks>
/// Unreal 빌드는 조건식에 정의되지 않은 매크로를 오류로 다룹니다(C4668). 그래서 실제 빌드에서 조건식의 매크로는 늘 정의되어 있고, PCH 없이
/// 분석했을 때 정의되지 않았다면 PCH가 정의하던 매크로입니다. 이때 clang은 오류 없이 0으로 보아 그 구역 전체를 비활성으로 분석하므로
/// 분석 오류로는 드러나지 않습니다(2026-10-09 정확도 시험: <c>#if WITH_AUTOMATION_TESTS</c>로 감싼 파일의 정의·참조가 모두 빠짐).
/// <c>defined(X)</c>의 피연산자, 함수형 매크로의 인수, 예약 이름(<c>_X</c>·<c>__x</c>), 불리언·대체 연산자 키워드, 파일 안에서
/// <c>#define</c>·<c>#undef</c>하는 이름은 뺍니다.
/// </remarks>
public static class ConditionMacros
{
    private static readonly Regex Directive = new(@"^[ \t]*#[ \t]*(if|elif)\b(.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex DefinedOrUndefined = new(@"^[ \t]*#[ \t]*(?:define|undef)[ \t]+([A-Za-z_]\w*)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex Builtin = new(@"\b(?:defined|__has_include|__has_include_next|__has_feature|__has_extension|__has_builtin|__has_attribute|__has_cpp_attribute|__has_declspec_attribute|__has_warning|__is_identifier)\s*(\((?:[^()]|\([^()]*\))*\)|[A-Za-z_]\w*)",
        RegexOptions.CultureInvariant);
    // 앞 경계가 없으면 숫자 접미사·16진수의 글자(`201703L`의 L, `0x0600`의 x0600)를 이름으로 뽑았습니다(2026-10-09 검토 49).
    private static readonly Regex Identifier = new(@"\b[A-Za-z_]\w*", RegexOptions.CultureInvariant);
    private static readonly Regex Conditional = new(@"^[ \t]*#[ \t]*(?<kind>if|ifdef|ifndef|elif|else|endif)\b(?<rest>.*)$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "true", "false", "and", "or", "not", "bitand", "bitor", "xor", "compl", "and_eq", "or_eq", "xor_eq", "not_eq"
    };

    /// <summary>조건식에 쓴 매크로 이름입니다(처음 나온 순서, 중복 없음).</summary>
    public static IReadOnlyList<string> Used(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var local = new HashSet<string>(DefinedOrUndefined.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Directive.Matches(ActiveText(JoinContinuations(text))))
        {
            var expression = StripComments(match.Groups[2].Value);
            expression = Builtin.Replace(expression, " ");
            foreach (Match name in Identifier.Matches(expression))
            {
                var value = name.Value;
                if (IsArgument(expression, name.Index) || Keywords.Contains(value) || IsReserved(value) || local.Contains(value)) continue;
                if (seen.Add(value)) names.Add(value);
            }
        }

        return names;
    }

    /// <summary>함수형 매크로 호출의 괄호 안에 있는 이름인지 봅니다. 인수는 매크로가 아닐 수 있습니다.</summary>
    private static bool IsArgument(string expression, int index)
    {
        var depth = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            var c = expression[i];
            if (c == ')') depth++;
            else if (c == '(')
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                // 여는 괄호 바로 앞이 이름이면 함수형 매크로의 인수입니다.
                var before = i - 1;
                while (before >= 0 && char.IsWhiteSpace(expression[before])) before--;
                if (before >= 0 && (char.IsLetterOrDigit(expression[before]) || expression[before] == '_')) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 여러 줄 주석과 <c>#if 0</c> 구역을 지운 글자입니다. 그 안의 조건식은 컴파일되지 않으므로 PCH 의존 판단에 쓰지 않습니다(검토 49).
    /// 줄 번호가 바뀌지 않게 지운 자리의 줄바꿈은 남깁니다.
    /// </summary>
    private static string ActiveText(string text)
    {
        text = WithoutBlockComments(text);
        var lines = text.Split('\n');
        var skipDepth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var match = Conditional.Match(lines[i]);
            var kind = match.Success ? match.Groups["kind"].Value : null;
            if (skipDepth > 0)
            {
                if (kind is "if" or "ifdef" or "ifndef") skipDepth++;
                else if (kind == "endif" || skipDepth == 1 && kind is "else" or "elif") skipDepth--;
                // 건너뛰던 구역을 끝낸 #else·#elif 줄은 남겨 그 조건식을 봅니다.
                if (!(skipDepth == 0 && kind is "else" or "elif")) lines[i] = string.Empty;
                continue;
            }

            if (kind == "if" && WithoutLineComment(match.Groups["rest"].Value).Trim() is "0" or "false")
            {
                skipDepth = 1;
                lines[i] = string.Empty;
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 여러 줄 주석을 지우고 그 안의 줄바꿈만 남깁니다. 줄 주석·문자열·문자 상수 안의 <c>/*</c>는 주석 시작이 아닙니다. 정규식으로 먼저 지우면
    /// <c>// Plugin/*.ini</c> 같은 줄부터 다음 <c>*/</c>까지의 조건식이 빠졌습니다(UE 5.8 Core 등 87줄, 2026-10-09 검토 56).
    /// </summary>
    private static string WithoutBlockComments(string text)
    {
        if (text.IndexOf("/*", StringComparison.Ordinal) < 0) return text;
        var result = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                result.Append(text, i, end - i);
                i = end;
            }
            else if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + 2;
                for (var j = i; j < stop; j++)
                {
                    if (text[j] == '\n') result.Append('\n');
                }

                result.Append(' ');
                i = stop;
            }
            else if (c == '"' || c == '\'' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                // 문자열·문자 상수는 닫는 따옴표나 줄 끝까지 그대로 둡니다. 숫자 구분자(1'000)는 앞 글자가 숫자라 상수로 보지 않습니다.
                var j = i + 1;
                while (j < text.Length && text[j] != c && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
                var stop = Math.Min(text.Length, j < text.Length && text[j] == c ? j + 1 : j);
                result.Append(text, i, stop - i);
                i = stop;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    private static string WithoutLineComment(string text)
    {
        var comment = text.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? text : text.Substring(0, comment);
    }

    private static bool IsReserved(string name) => name.Length > 1 && name[0] == '_' && (name[1] == '_' || char.IsUpper(name[1]));

    private static string JoinContinuations(string text) => text.Replace("\\\r\n", " ").Replace("\\\n", " ");

    private static string StripComments(string expression)
    {
        var line = expression.IndexOf("//", StringComparison.Ordinal);
        if (line >= 0) expression = expression.Substring(0, line);
        return Regex.Replace(expression, @"/\*.*?\*/", " ");
    }
}
