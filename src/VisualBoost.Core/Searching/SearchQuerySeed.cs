using System;
using System.Linq;

namespace VisualBoost.Core.Searching;

/// <summary>
/// 편집기에서 선택한 텍스트로 검색 창의 첫 검색어를 만듭니다. 선택이 검색어로 보이지 않으면 null을 돌려주어
/// 빈 검색(추천 목록)으로 시작하게 합니다. 남아 있던 선택 때문에 엉뚱한 검색어가 채워지는 일을 줄이려고
/// 검색 창마다 보수적인 조건을 둡니다.
/// </summary>
public static class SearchQuerySeed
{
    /// <summary>검색어로 받을 최대 길이입니다. 이보다 긴 선택은 코드 조각으로 봅니다.</summary>
    public const int MaximumLength = 200;

    /// <summary>
    /// 파일 탐색용 검색어입니다. 파일 이름·경로로 보이는 선택(점·경로 구분자 포함)만 받고,
    /// <c>#include "Foo.h"</c> 줄이나 따옴표·꺾쇠로 감싼 경로는 경로만 남깁니다.
    /// </summary>
    public static string? ForFileSearch(string? selection)
    {
        var text = SingleLine(selection);
        if (text is null) return null;
        foreach (var directive in new[] { "#include", "#import" })
        {
            if (text.StartsWith(directive, StringComparison.Ordinal)) text = text.Substring(directive.Length).Trim();
        }

        // 줄 끝 주석(`#include "Foo.h" // 설명`)은 경로가 아니므로 닫는 따옴표·꺾쇠 뒤를 버립니다.
        text = CutAfter(text, '"', '"');
        text = CutAfter(text, '<', '>');
        text = Unwrap(text, '"', '"');
        text = Unwrap(text, '<', '>');
        return text.Length > 0 && text.IndexOfAny(new[] { '.', '/', '\\' }) >= 0 ? text : null;
    }

    /// <summary>
    /// 심볼 탐색용 검색어입니다. 식별자 하나만 받으며, <c>Widget::Tick</c>처럼 한정한 이름은 마지막 이름만 씁니다
    /// (심볼 탐색은 이름만 검색합니다).
    /// </summary>
    public static string? ForSymbolSearch(string? selection)
    {
        var text = SingleLine(selection);
        if (text is null) return null;
        var qualifier = text.LastIndexOf("::", StringComparison.Ordinal);
        if (qualifier >= 0) text = text.Substring(qualifier + 2);
        return text.Length > 0 && !char.IsDigit(text[0]) && text.All(c => char.IsLetterOrDigit(c) || c == '_') ? text : null;
    }

    private static string? SingleLine(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection) || selection!.IndexOfAny(new[] { '\r', '\n' }) >= 0) return null;
        var text = selection.Trim();
        return text.Length <= MaximumLength ? text : null;
    }

    private static string CutAfter(string text, char open, char close)
    {
        if (text.Length == 0 || text[0] != open) return text;
        var end = text.IndexOf(close, 1);
        return end > 0 ? text.Substring(0, end + 1) : text;
    }

    private static string Unwrap(string text, char open, char close) =>
        text.Length >= 2 && text[0] == open && text[text.Length - 1] == close ? text.Substring(1, text.Length - 2).Trim() : text;
}
