using System.Globalization;
using System.Text.RegularExpressions;

namespace VisualBoost.Core.Searching;

/// <summary>
/// 파일 탐색 검색어에서 줄·열 표기를 떼어 냅니다. 빌드 로그·진단·주석에서 복사한
/// <c>Foo.cpp:120</c>, <c>Foo.cpp:120:5</c>, <c>Foo.cpp(120)</c>, <c>Foo.cpp(120,5)</c> 형식을 받고,
/// 진단 줄을 통째로 붙여 넣은 경우(<c>1&gt;C:\src\Foo.cpp(120,5): error C2065: ...</c>,
/// <c>Foo.cpp:120:5: error: ...</c>)도 앞의 프로젝트 번호와 위치 뒤 메시지를 버리고 받습니다. 줄·열은 1부터 셉니다.
/// </summary>
/// <remarks>
/// Windows 파일 이름에는 <c>:</c>와 <c>&gt;</c>가 올 수 없으므로 위치 뒤의 <c>: 메시지</c>와 앞의 <c>N&gt;</c>는
/// 파일 이름의 일부가 아닙니다. 괄호 형식은 파일 이름에 바로 붙고 뒤가 끝이거나 콜론일 때만 받습니다.
/// <c>Widget (2).png</c>, <c>Widget(2).png</c>처럼 이름 안의 괄호는 줄 표기로 보지 않습니다.
/// </remarks>
public sealed class FileLocationQuery
{
    // MSBuild 병렬 빌드 출력의 프로젝트 번호 접두어입니다(예: "12>").
    private static readonly Regex ProjectPrefix = new(@"^\s*\d+>", RegexOptions.CultureInvariant);

    private static readonly Regex Location = new(
        @"^(?<text>.*?\S)(?::(?<line>\d{1,9})(?::(?<column>\d{1,9}))?|\((?<line>\d{1,9})(?:\s*,\s*(?<column>\d{1,9}))?\))(?:\s*:.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Singleline);

    private FileLocationQuery(string searchText, int? line, int? column)
    {
        SearchText = searchText;
        Line = line;
        Column = column;
    }

    /// <summary>파일 검색에 쓸 검색어입니다. 위치 표기가 없으면 프로젝트 번호 접두어만 뗀 입력입니다.</summary>
    public string SearchText { get; }

    /// <summary>열 뒤 이동할 줄(1부터)입니다. 줄 표기가 없으면 null입니다.</summary>
    public int? Line { get; }

    /// <summary>열 뒤 이동할 열(1부터)입니다. 열 표기가 없거나 0이면 null입니다.</summary>
    public int? Column { get; }

    public static FileLocationQuery Parse(string? query)
    {
        var text = ProjectPrefix.Replace(query ?? string.Empty, string.Empty, 1);
        var match = Location.Match(text.Trim());
        if (!match.Success || !TryPositive(match.Groups["line"].Value, out var line))
        {
            return new FileLocationQuery(text, null, null);
        }

        int? column = TryPositive(match.Groups["column"].Value, out var value) ? value : null;
        return new FileLocationQuery(match.Groups["text"].Value.Trim(), line, column);
    }

    private static bool TryPositive(string digits, out int value) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
}
