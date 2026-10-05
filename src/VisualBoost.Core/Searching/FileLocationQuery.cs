using System.Globalization;
using System.Text.RegularExpressions;

namespace VisualBoost.Core.Searching;

/// <summary>
/// 파일 탐색 검색어 끝의 줄·열 표기를 떼어 냅니다. 빌드 로그·진단·주석에서 복사한
/// <c>Foo.cpp:120</c>, <c>Foo.cpp:120:5</c>, <c>Foo.cpp(120)</c>, <c>Foo.cpp(120,5)</c> 형식과
/// 그 뒤에 붙는 콜론 하나(<c>Foo.cpp(120):</c>)를 받습니다. 줄·열은 1부터 셉니다.
/// </summary>
/// <remarks>
/// 괄호 형식은 파일 이름에 바로 붙은 경우만 받습니다. <c>Widget (2).png</c>처럼 공백 뒤 괄호는
/// 파일 이름의 일부일 수 있어 줄 표기로 보지 않습니다. 줄 앞부분이 비면 줄 표기로 보지 않습니다.
/// </remarks>
public sealed class FileLocationQuery
{
    private static readonly Regex Suffix = new(
        @"^(?<text>.*?\S)(?::(?<line>\d{1,9})(?::(?<column>\d{1,9}))?|\((?<line>\d{1,9})(?:\s*,\s*(?<column>\d{1,9}))?\)):?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    private FileLocationQuery(string searchText, int? line, int? column)
    {
        SearchText = searchText;
        Line = line;
        Column = column;
    }

    /// <summary>파일 검색에 쓸 검색어입니다. 줄 표기가 없으면 입력과 같습니다.</summary>
    public string SearchText { get; }

    /// <summary>열 뒤 이동할 줄(1부터)입니다. 줄 표기가 없으면 null입니다.</summary>
    public int? Line { get; }

    /// <summary>열 뒤 이동할 열(1부터)입니다. 열 표기가 없거나 0이면 null입니다.</summary>
    public int? Column { get; }

    public static FileLocationQuery Parse(string? query)
    {
        var text = query ?? string.Empty;
        var match = Suffix.Match(text.Trim());
        if (!match.Success || !TryPositive(match.Groups["line"].Value, out var line))
        {
            return new FileLocationQuery(text, null, null);
        }

        int? column = TryPositive(match.Groups["column"].Value, out var value) ? value : null;
        return new FileLocationQuery(match.Groups["text"].Value, line, column);
    }

    private static bool TryPositive(string digits, out int value) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
}
