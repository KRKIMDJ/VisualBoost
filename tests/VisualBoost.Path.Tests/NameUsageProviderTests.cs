using System;
using System.IO;
using System.Linq;
using System.Threading;
using VisualBoost.Services;

internal static class NameUsageProviderTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoostNameUsage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var owned = Path.Combine(root, "Owned.cpp");
            var other = Path.Combine(root, "Other.cpp");
            var comment = Path.Combine(root, "Comments.h");
            var absent = Path.Combine(root, "Absent.cpp");
            var project = Path.Combine(root, "Game.vcxproj");
            File.WriteAllText(owned, "void Move();\nvoid Move() {}\nMove(); Movement(); move(); // Move\n");
            File.WriteAllText(other, "OtherType::Move();");
            File.WriteAllText(comment, "// Move\nconst char* s = \"Move\";");
            File.WriteAllText(absent, "void Unrelated();");
            File.WriteAllText(project, "<Project><ItemGroup><ClCompile Include=\"Owned.cpp\" /></ItemGroup></Project>");
            var paths = new[] { owned, other, comment, absent, owned.ToUpperInvariant(), "metadata://assembly/Move.cpp", "C:\\bad:file.cpp", Path.Combine(root, "Missing.cpp") };
            var provider = new IndexedSymbolUsageProvider();
            var all = provider.FindUsages("Move", paths, null, SymbolUsageScope.EntireSolution, 5000, CancellationToken.None);
            Check(all.Count == 4, "정확한 이름·대소문자·주석·문자열·중복 경로·없는 이름 처리");
            Check(all.Any(result => result.Path == other), "다른 타입의 같은 이름도 후보에 포함");
            var current = provider.FindUsages("Move", paths, "\"" + project + "\"", SymbolUsageScope.CurrentProject, 5000, CancellationToken.None);
            Check(current.Count == 3 && current.All(result => result.Path == owned), "실행 프로젝트의 명시적 파일 범위 및 따옴표 경로");
            Check(current.Select(result => result.Line).SequenceEqual(new[] { 1, 2, 3 }), "선언·정의·사용 위치와 라인 유지");
            Check(provider.FindUsages("Move", paths, null, SymbolUsageScope.EntireSolution, 2, CancellationToken.None).Count == 2, "결과 상한");
            Check(provider.FindUsages("Move", paths, null, SymbolUsageScope.EntireSolution, 0, CancellationToken.None).Count == 0, "0개 제한");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try { provider.FindUsages("Move", paths, null, SymbolUsageScope.EntireSolution, 5000, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "검색 시작 전 취소");
        }
        finally
        {
            // 이 테스트가 만든 고유 임시 디렉터리만 정리합니다.
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: 이름 기반 공급자 " + message);
    }
}
