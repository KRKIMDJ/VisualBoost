using System;
using System.IO;
using System.Linq;
using VisualBoost.Core.Coloring;
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

        // 색 구간은 자르기 전 줄로 분류합니다. 앞을 문자열 중간에서 잘라도 닫는 따옴표를 여는 따옴표로 보지 않아야 합니다.
        var log = "UE_LOG(LogGame, Warning, TEXT(\"" + new string('m', 100) + "\"), *Actor->GetName());";
        var call = log.IndexOf("GetName", StringComparison.Ordinal);
        var logged = new NavigationResultItem(new NavigationLocation(path, 0, call, 0, call + 7), log, null);
        var preview = logged.Before + logged.Match + logged.After;
        Assert(Describe(preview, logged.PreviewSpans) == "mmm:String GetName:Function", "잘린 문자열 리터럴: " + Describe(preview, logged.PreviewSpans));
        var quote = preview.IndexOf('"');
        Assert(logged.PreviewSpans[0].Start == 1 && logged.PreviewSpans[0].Start + logged.PreviewSpans[0].Length == quote + 1, "문자열 구간은 줄임표 뒤에서 닫는 따옴표까지");
        var commented = "/* " + new string('c', 100) + " */ Compute(Value); // 끝";
        var at = commented.IndexOf("Compute", StringComparison.Ordinal);
        var commentItem = new NavigationResultItem(new NavigationLocation(path, 0, at, 0, at + 7), commented, null);
        Assert(Describe(commentItem.Before + commentItem.Match + commentItem.After, commentItem.PreviewSpans) == "ccc:Comment Compute:Function // 끝:Comment",
            "잘린 블록 주석: " + Describe(commentItem.Before + commentItem.Match + commentItem.After, commentItem.PreviewSpans));
        Assert(ReferenceEquals(logged.PreviewSpans, logged.PreviewSpans), "색 구간은 한 번만 만듦");
        var tail = "Compute(" + new string('a', 250) + " \"text\");";
        var tailItem = new NavigationResultItem(new NavigationLocation(path, 0, 0, 0, 7), tail, null);
        Assert(tailItem.PreviewSpans.All(span => span.Start + span.Length <= tailItem.Before.Length + tailItem.Match.Length + tailItem.After.Length - 1),
            "뒤를 자르면 줄임표와 보이지 않는 구간을 칠하지 않음");

        // 패키지가 연결한 이름 판정(Solution 이름 인덱스)으로 줄 형태만으로 모르는 매크로·타입 이름을 칠합니다.
        var declaration = "UE_API virtual UInputUserSettings* GetUserSettings() const;";
        var target = declaration.IndexOf("GetUserSettings", StringComparison.Ordinal);
        var shapeOnly = new NavigationResultItem(new NavigationLocation(path, 0, target, 0, target + 15), declaration, null);
        Assert(Describe(declaration, shapeOnly.PreviewSpans) == "virtual:Keyword GetUserSettings:Function const:Keyword",
            "이름 판정 없으면 형태만: " + Describe(declaration, shapeOnly.PreviewSpans));
        try
        {
            CodePreviewStyle.NameKind = name => name switch
            {
                "UE_API" => CodePreviewKind.Macro,
                "UInputUserSettings" => CodePreviewKind.Type,
                _ => null,
            };
            var named = new NavigationResultItem(new NavigationLocation(path, 0, target, 0, target + 15), declaration, null);
            Assert(Describe(declaration, named.PreviewSpans) == "UE_API:Macro virtual:Keyword UInputUserSettings:Type GetUserSettings:Function const:Keyword",
                "이름 판정으로 매크로·타입 보강: " + Describe(declaration, named.PreviewSpans));
        }
        finally
        {
            CodePreviewStyle.NameKind = null;
        }

        Console.WriteLine("PASS: 정의·참조 결과 줄 구성과 필터");
    }

    /// <summary>구간을 "글자:종류"로 적습니다. 긴 반복 글자는 앞 세 글자만 남겨 비교를 짧게 합니다.</summary>
    private static string Describe(string text, System.Collections.Generic.IReadOnlyList<CodePreviewSpan> spans) =>
        string.Join(" ", spans.Select(span =>
        {
            var part = text.Substring(span.Start, span.Length);
            if (part.Length > 20) part = part.Substring(0, 3);
            return part + ":" + span.Kind;
        }));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
