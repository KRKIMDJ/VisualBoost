using System;
using System.IO;
using VisualBoost.Core.Analysis;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            VisualBoost.Core.Tests.QuickColorTests.Run();
            Check(SearchPath.TryNormalize("  \"C:\\Samples\\Game\\Main.cpp\"  ", out var normalized) && normalized == @"C:\Samples\Game\Main.cpp", "따옴표를 포함한 파일 경로 정규화");
            Check(SearchPath.DirectoryOf(@"C:\Samples\Game\Main.cpp") == @"C:\Samples\Game", "공용 문서 폴더 확인");
            Check(SearchPath.TryNormalize("file:///C:/Samples/Game/Main.cpp", out var file) && file == @"C:\Samples\Game\Main.cpp", "file URI 정규화");
            Check(SearchPath.TryNormalize(@"\\server\share\소스 코드\Main.cpp", out _), "UNC 및 한글·공백 경로 보존");
            foreach (var invalid in new[] { "", "C:relative.cpp", "metadata://assembly/Type.cs", "vsls://session/Main.cpp", "C:\\bad\"file.cpp", "C:\\bad:file.cpp", "\\only-root.cpp", "\\\\server" })
                Check(!SearchPath.TryNormalize(invalid, out _), "비파일·불완전 경로 제외: " + invalid);
            // .NET Framework의 경로 함수는 | < 제어 문자에서 예외를 냅니다. 색인 앞당기기가 편집 중인 깨진 include 줄에서 실패하지 않아야 합니다.
            var stems = VisualBoost.Core.SemanticNavigation.IndexQueuePriority.IncludedStems("#include \"Bad|Name.h\"\n#include <x\u0001y.h>\n#include <a<b.h>\n#include \"Dir/Ok.h\"\n");
            Check(string.Join(",", stems) == "Ok", "깨진 include 이름 거르기: " + string.Join(",", stems));
            SymbolCacheTests.Run();
            SourceAnalysisRegressionTests.Run();
            AnalysisProgressTests.Run();
            RestartCacheTests.Run();
            DiscoveryReuseTests.Run();
            ProjectScopeTests.Run();
            ProgressiveIndexTests.Run();
            SymbolScopeTests.Run();
            CodePreviewNamesTests.Run();
            Console.WriteLine("모든 .NET Framework 경로·이름 검색 회귀 테스트가 통과했습니다.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
