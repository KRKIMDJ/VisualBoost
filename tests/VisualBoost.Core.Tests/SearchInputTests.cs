using System;
using VisualBoost.Core.Searching;

namespace VisualBoost.Core.Tests;

/// <summary>검색 창 입력 해석: 파일 탐색의 줄 표기와 편집기 선택으로 만드는 첫 검색어.</summary>
internal static class SearchInputTests
{
    public static void Run()
    {
        Location("Foo.cpp:120", "Foo.cpp", 120, null, "콜론 줄 표기");
        Location("Foo.cpp:120:7", "Foo.cpp", 120, 7, "콜론 줄·열 표기");
        Location("Foo.cpp(120)", "Foo.cpp", 120, null, "괄호 줄 표기");
        Location("Foo.cpp(120, 7):", "Foo.cpp", 120, 7, "괄호 줄·열과 뒤따르는 콜론");
        Location("Foo.cpp:120:7:", "Foo.cpp", 120, 7, "진단 형식의 뒤따르는 콜론");
        Location(@"C:\Work\Source\Foo.cpp(42)", @"C:\Work\Source\Foo.cpp", 42, null, "드라이브 콜론은 줄 표기가 아님");
        Location("  foo bar:12  ", "foo bar", 12, null, "AND 검색어 뒤 줄 표기와 앞뒤 공백");
        Location("Foo.cpp:12:0", "Foo.cpp", 12, null, "열 0은 무시");
        Location("Foo.cpp", "Foo.cpp", null, null, "줄 표기 없음");
        Location("Widget (2).png", "Widget (2).png", null, null, "공백 뒤 괄호는 파일 이름의 일부");
        Location("Foo.cpp:0", "Foo.cpp:0", null, null, "줄 0은 줄 표기가 아님");
        Location(":120", ":120", null, null, "파일 부분이 없으면 줄 표기가 아님");
        Location("Foo.cpp:", "Foo.cpp:", null, null, "숫자 없는 콜론");
        Location("Foo.cpp:1234567890", "Foo.cpp:1234567890", null, null, "범위를 넘는 줄 번호");
        Location(null, "", null, null, "null 입력");

        Check(SearchQuerySeed.ForFileSearch("Widget.h") == "Widget.h", "파일 이름 선택");
        Check(SearchQuerySeed.ForFileSearch("  Source/Game/Widget.cpp ") == "Source/Game/Widget.cpp", "경로 선택과 공백 제거");
        Check(SearchQuerySeed.ForFileSearch("#include \"Game/Widget.h\"") == "Game/Widget.h", "include 줄 전체 선택");
        Check(SearchQuerySeed.ForFileSearch("#include\"Widget.h\" // 설명") == "Widget.h", "공백 없는 include와 줄 끝 주석");
        Check(SearchQuerySeed.ForFileSearch("<vector.h>") == "vector.h", "꺾쇠 경로");
        Check(SearchQuerySeed.ForFileSearch("Widget.cpp(120)") == "Widget.cpp(120)", "줄 표기는 파일 탐색이 해석하도록 유지");
        Check(SearchQuerySeed.ForFileSearch("Widget") is null, "점·구분자 없는 이름은 남은 선택일 수 있어 채우지 않음");
        Check(SearchQuerySeed.ForFileSearch("a.b;\nc.d") is null, "여러 줄 선택");
        Check(SearchQuerySeed.ForFileSearch(new string('a', 199) + ".h") is null, "최대 길이 초과");
        Check(SearchQuerySeed.ForFileSearch("   ") is null && SearchQuerySeed.ForFileSearch(null) is null, "빈 선택");

        Check(SearchQuerySeed.ForSymbolSearch("Tick") == "Tick", "식별자 선택");
        Check(SearchQuerySeed.ForSymbolSearch(" UWidget::Tick ") == "Tick", "한정 이름은 마지막 이름");
        Check(SearchQuerySeed.ForSymbolSearch("Game::Detail::_Value2") == "_Value2", "여러 단계 한정과 밑줄·숫자");
        Check(SearchQuerySeed.ForSymbolSearch("this->Tick") is null, "멤버 접근 식");
        Check(SearchQuerySeed.ForSymbolSearch("Tick(0)") is null, "호출 식");
        Check(SearchQuerySeed.ForSymbolSearch("2D") is null, "숫자로 시작");
        Check(SearchQuerySeed.ForSymbolSearch("Widget::") is null, "이름 없는 한정");
        Check(SearchQuerySeed.ForSymbolSearch("a\r\nb") is null, "여러 줄 선택");
    }

    private static void Location(string? input, string text, int? line, int? column, string message)
    {
        var parsed = FileLocationQuery.Parse(input);
        Check(parsed.SearchText == text && parsed.Line == line && parsed.Column == column,
            $"{message}: '{parsed.SearchText}' {parsed.Line}:{parsed.Column}");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
