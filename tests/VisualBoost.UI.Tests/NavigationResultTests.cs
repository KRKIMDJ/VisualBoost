using System;
using System.IO;
using VisualBoost.Core.SemanticNavigation;
using VisualBoost.UI;

internal static class NavigationResultTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Game");
        var path = Path.Combine(root, "Source", "Game", "Use.cpp");
        var item = new NavigationResultItem(new NavigationLocation(path, 9, 15, 9, 22), "\t\treturn FMod::Compute(3);  ", root);
        Assert(item.Before == "return FMod::" && item.Match == "Compute" && item.After == "(3);" && item.Code == "return FMod::Compute(3);", "범위 기준 앞·일치·뒤");
        Assert(item.FileName == "Use.cpp" && item.Line == "10" && item.Folder == Path.Combine("Source", "Game"), "파일·1기반 줄·Solution 기준 폴더");
        Assert(item.Matches("compute") && item.Matches("use.CPP") && item.Matches("source") && !item.Matches("Other"), "대소문자 무시 필터");
        var contained = new NavigationResultItem(new NavigationLocation(path, 9, 15, 9, 22, "Game::Tick"), "\t\treturn FMod::Compute(3);  ", root);
        Assert(item.Container.Length == 0 && contained.Container == "Game::Tick" && contained.Matches("game::t") && !item.Matches("Tick"), "포함 함수 표시·필터");

        var outside = new NavigationResultItem(new NavigationLocation(Path.Combine(Path.GetTempPath(), "Engine", "A.h"), 0, 50, 2, 1), "short", root);
        Assert(outside.Before == "short" && outside.Match.Length == 0 && outside.Folder == Path.Combine(Path.GetTempPath(), "Engine"), "범위 밖 위치·여러 줄 범위·Solution 밖 폴더");
        var longLine = new string('x', 120) + "Name" + new string('y', 260);
        var shortened = new NavigationResultItem(new NavigationLocation(path, 0, 120, 0, 124), longLine, null);
        Assert(shortened.Before.Length == 81 && shortened.Before[0] == '…' && shortened.Match == "Name" && shortened.After.Length == 201, "긴 줄 앞뒤 생략");
        Console.WriteLine("PASS: 정의·참조 결과 줄 구성과 필터");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
