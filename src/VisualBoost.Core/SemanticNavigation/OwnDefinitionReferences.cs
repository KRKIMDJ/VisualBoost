using System;
using System.Collections.Generic;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 클래스 참조 중 그 클래스 자신의 정의에 속하는 이름 위치를 가려냅니다. 참조 목록은 "쓰는 곳"을 보이므로 이런 위치는 뺍니다
/// (2026-10-07 사용자 결정: 뺀 개수 표시·다시 보기 없음).
/// </summary>
/// <remarks>
/// clangd는 다음 위치를 클래스 참조로 돌려줍니다(clangd 22 확인). 참조의 소속 이름(<see cref="NavigationLocation.Container"/>)과 그 줄의 글자로만
/// 판정하고, 소속 이름이 없으면 빼지 않습니다.
/// <list type="bullet">
/// <item>멤버 정의 머리의 한정자: <c>void AActor::BeginPlay()</c>, <c>AActor::AActor()</c>의 앞 이름, <c>int AActor::Count = 0;</c>,
/// <c>void AActor::FInner::Do()</c>. 이름 뒤에 쓰인 <c>::</c> 사슬이 소속 이름의 끝과 같을 때입니다. 함수 몸체 안의 <c>AActor::Get()</c>은
/// 소속 이름이 감싼 함수라 남습니다. 함수 이름 뒤에는 <c>::</c>가 올 수 없어 찾는 심볼의 종류와 상관없이 적용합니다.</item>
/// <item>생성자·소멸자 이름: <c>AActor::AActor()</c>의 뒤 이름, <c>~AActor()</c>, 클래스 본문의 <c>AActor();</c>. 소속 이름이 클래스 자신이고
/// 이름 뒤가 <c>(</c>일 때입니다. 함수 안의 재귀 호출도 같은 모양이므로 찾는 심볼이 타입일 때만 적용합니다.</item>
/// </list>
/// 반환형·매개변수형·변수 선언·정적 멤버 호출·임시 객체 생성처럼 실제로 쓰는 위치는 남깁니다.
/// </remarks>
public static class OwnDefinitionReferences
{
    /// <summary>뺄 위치를 제외한 위치의 순번을 입력 순서대로 돌려줍니다.</summary>
    /// <param name="lines">위치마다의 원래 줄 글자(<see cref="SourceLinePreview.LoadLines"/>)입니다.</param>
    public static IReadOnlyList<int> Kept(IReadOnlyList<NavigationLocation> locations, IReadOnlyList<string> lines, bool symbolIsType)
    {
        if (locations is null) throw new ArgumentNullException(nameof(locations));
        if (lines is null) throw new ArgumentNullException(nameof(lines));
        var kept = new List<int>(locations.Count);
        for (var i = 0; i < locations.Count; i++)
        {
            if (!IsOwnDefinitionName(locations[i], i < lines.Count ? lines[i] : null, symbolIsType)) kept.Add(i);
        }

        return kept;
    }

    /// <param name="location">참조 위치입니다. 이름 범위가 한 줄 안에 있어야 합니다.</param>
    /// <param name="lineText">그 위치의 원래 줄 글자입니다.</param>
    /// <param name="symbolIsType">찾는 심볼이 클래스·구조체 같은 타입이면 true입니다. 생성자·소멸자 이름 규칙에만 씁니다.</param>
    public static bool IsOwnDefinitionName(NavigationLocation location, string? lineText, bool symbolIsType)
    {
        if (location is null) throw new ArgumentNullException(nameof(location));
        if (lineText is null || location.Container is not { Length: > 0 } container || location.EndLine != location.Line) return false;
        var start = location.Character;
        var end = location.EndCharacter;
        if (start < 0 || end <= start || end > lineText.Length) return false;
        var name = lineText.Substring(start, end - start);
        var scopes = Segments(container);
        var chain = WrittenChain(lineText, end, out var after);

        // 멤버 정의 머리: 소속 이름이 "…이름::쓰인 사슬"로 끝납니다.
        if (chain.Count > 0)
        {
            var offset = scopes.Count - chain.Count - 1;
            if (offset < 0 || scopes[offset] != name) return false;
            for (var i = 0; i < chain.Count; i++)
            {
                if (scopes[offset + 1 + i] != chain[i]) return false;
            }

            return true;
        }

        // 생성자·소멸자 이름: 소속 이름이 클래스 자신이거나 그 클래스의 생성자·소멸자이고 이름 뒤가 여는 괄호입니다.
        if (!symbolIsType || !Follows(lineText, after, "(")) return false;
        var last = scopes[scopes.Count - 1];
        return last == name || (scopes.Count >= 2 && scopes[scopes.Count - 2] == name && (last == name || last == "~" + name));
    }

    private static bool IsIdentifier(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static int SkipSpaces(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        return index;
    }

    private static bool Follows(string text, int index, string token)
    {
        index = SkipSpaces(text, index);
        return string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
    }

    /// <summary>이름 뒤에 이어 쓴 <c>::이름</c>들입니다(템플릿 인수는 건너뜀). <paramref name="after"/>는 사슬이 끝난 위치입니다.</summary>
    private static List<string> WrittenChain(string text, int index, out int after)
    {
        var chain = new List<string>();
        after = SkipTemplateArguments(text, index);
        while (Follows(text, after, "::"))
        {
            var next = SkipSpaces(text, SkipSpaces(text, after) + 2);
            var segmentStart = next;
            if (next < text.Length && text[next] == '~') next++;
            while (next < text.Length && IsIdentifier(text[next])) next++;
            if (next == segmentStart || (next == segmentStart + 1 && text[segmentStart] == '~')) break;
            chain.Add(text.Substring(segmentStart, next - segmentStart));
            after = SkipTemplateArguments(text, next);
        }

        return chain;
    }

    /// <summary>이름 바로 뒤의 템플릿 인수(<c>&lt;…&gt;</c>)를 건너뛴 위치입니다. 없으면 그대로입니다.</summary>
    private static int SkipTemplateArguments(string text, int index)
    {
        var at = SkipSpaces(text, index);
        if (at >= text.Length || text[at] != '<') return index;
        var depth = 0;
        for (var i = at; i < text.Length; i++)
        {
            if (text[i] == '<') depth++;
            else if (text[i] == '>' && --depth == 0) return i + 1;
        }

        return index;
    }

    /// <summary>바깥 단계(꺾쇠·괄호 밖)의 <c>::</c>로 나눈 이름들입니다. 각 이름의 템플릿 인수는 뗍니다(clangd는 <c>TBox::TBox&lt;T&gt;</c>처럼 씀).</summary>
    private static List<string> Segments(string qualified)
    {
        var segments = new List<string>();
        var depth = 0;
        var segmentStart = 0;
        for (var i = 0; i < qualified.Length; i++)
        {
            var c = qualified[i];
            if (c is '<' or '(') depth++;
            else if (c is '>' or ')') depth = Math.Max(0, depth - 1);
            else if (c == ':' && depth == 0 && i + 1 < qualified.Length && qualified[i + 1] == ':')
            {
                segments.Add(Strip(qualified.Substring(segmentStart, i - segmentStart)));
                segmentStart = i + 2;
                i++;
            }
        }

        segments.Add(Strip(qualified.Substring(segmentStart)));
        return segments;
    }

    private static string Strip(string segment)
    {
        var open = segment.IndexOf('<');
        return (open < 0 ? segment : segment.Substring(0, open)).Trim();
    }
}
