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
/// 소속 이름이 감싼 함수라 남습니다. 몸체 안에서 자기 자신을 한정해 부르는 <c>AActor::F(n - 1)</c>·같은 이름 오버로드 호출은 사슬과 소속
/// 이름이 같으므로 이름 앞뒤 글자로 식 안의 호출인지 가려 남깁니다(2026-10-07 검토). 함수 이름 뒤에는 <c>::</c>가 올 수 없어 찾는
/// 심볼의 종류와 상관없이 적용합니다.</item>
/// <item>생성자·소멸자 이름: <c>AActor::AActor()</c>의 뒤 이름, <c>~AActor()</c>, 클래스 본문의 <c>AActor();</c>. 소속 이름이 클래스 자신이고
/// 이름 뒤가 <c>(</c>일 때입니다. 생성자·소멸자 몸체와 초기화 목록 안의 위임 생성자 <c>: AActor(0)</c>·임시 객체 <c>AActor(1)</c>은 소속
/// 이름이 <c>AActor::AActor</c>처럼 그 생성자·소멸자라 남깁니다(clangd 22 확인).
/// 함수 안의 재귀 호출도 같은 모양이므로 찾는 심볼이 타입일 때만 적용합니다.</item>
/// </list>
/// 반환형·매개변수형·변수 선언·정적 멤버 호출·임시 객체 생성처럼 실제로 쓰는 위치는 남깁니다. 연산자 정의 머리(<c>A&amp; A::operator=(…)</c>)는
/// 사슬이 소속 이름과 달라 남습니다.
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

            return !IsCall(lineText, start, after);
        }

        // 생성자·소멸자 이름: 소속 이름이 클래스 자신이고 이름 뒤가 여는 괄호입니다. 소속이 그 클래스의 생성자·소멸자(A::A, A::~A)이면
        // 몸체·초기화 목록 안의 사용이므로 남깁니다.
        if (!symbolIsType || !Follows(lineText, after, "(")) return false;
        var last = scopes[scopes.Count - 1];
        return last == name && (scopes.Count == 1 || scopes[scopes.Count - 2] != name);
    }

    /// <summary>
    /// 정의 머리와 같은 사슬이 식 안의 호출로 쓰였는지 봅니다. 이름 앞이 식에서만 오는 기호·키워드이거나, 같은 줄에서 인수 괄호를 닫은 뒤
    /// 식이 이어지면 호출입니다. 정의 머리는 앞에 반환형(이름·<c>*</c>·<c>&amp;</c>·<c>&gt;</c>)이나 줄 시작이 오고 뒤에 <c>{</c>·<c>const</c>·
    /// <c>:</c>·<c>= default</c>·줄 끝이 옵니다. 판단할 수 없으면 정의 머리로 봅니다.
    /// </summary>
    private static bool IsCall(string text, int start, int after)
    {
        var before = start - 1;
        while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
        if (before >= 0)
        {
            var c = text[before];
            if (c is '(' or ',' or '=' or '!' or '?' or '{' or ';' or '[' or '+' or '-' or '/' or '%' or '|' or '^' or '<') return true;
            // 포인터 멤버 접근 p->A::F()입니다.
            if (c == '>' && before > 0 && text[before - 1] == '-') return true;
            if (IsIdentifier(c))
            {
                var wordEnd = before + 1;
                while (before >= 0 && IsIdentifier(text[before])) before--;
                var word = text.Substring(before + 1, wordEnd - before - 1);
                if (word is "return" or "co_return" or "co_await" or "co_yield" or "throw") return true;
            }
        }

        // 같은 줄 뒤에 붙은 주석(void A::F() // 설명)은 줄 끝으로 봅니다. 주석의 '/'를 나눗셈으로 읽어 정의 머리를 호출로 보지 않게 합니다.
        var end = text.IndexOf("//", after, StringComparison.Ordinal);
        var block = text.IndexOf("/*", after, StringComparison.Ordinal);
        if (end < 0 || (block >= 0 && block < end)) end = block;
        if (end < 0) end = text.Length;
        var open = SkipSpaces(text, after);
        if (open >= end || text[open] != '(') return false;
        var depth = 0;
        for (var i = open; i < end; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0)
            {
                var next = SkipSpaces(text, i + 1);
                if (next >= end) return false;
                var c = text[next];
                var following = next + 1 < end ? text[next + 1] : '\0';
                return c is ';' or ')' or ',' or '.' or ']' or '}' or '?' or '+' or '*' or '/' or '%' or '|' or '^' or '<' or '>' or '!' ||
                       (c == '=' && following == '=') || (c == '-' && following != '>');
            }
        }

        return false;
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
