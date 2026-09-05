using System;
using System.IO;
using VisualBoost.Core.Analysis;

internal static class Program
{
    private static int Main()
    {
        try
        {
            Check(SearchPath.TryNormalize("  \"C:\\Samples\\Game\\Main.cpp\"  ", out var normalized) && normalized == @"C:\Samples\Game\Main.cpp", "따옴표를 포함한 파일 경로 정규화");
            Check(SearchPath.DirectoryOf(@"C:\Samples\Game\Main.cpp") == @"C:\Samples\Game", "공용 문서 폴더 확인");
            Check(SearchPath.TryNormalize("file:///C:/Samples/Game/Main.cpp", out var file) && file == @"C:\Samples\Game\Main.cpp", "file URI 정규화");
            Check(SearchPath.TryNormalize(@"\\server\share\소스 코드\Main.cpp", out _), "UNC 및 한글·공백 경로 보존");
            foreach (var invalid in new[] { "", "C:relative.cpp", "metadata://assembly/Type.cs", "vsls://session/Main.cpp", "C:\\bad\"file.cpp", "C:\\bad:file.cpp", "\\only-root.cpp", "\\\\server" })
                Check(!SearchPath.TryNormalize(invalid, out _), "비파일·불완전 경로 제외: " + invalid);
            NameUsageProviderTests.Run();
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
