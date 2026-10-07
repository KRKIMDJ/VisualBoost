using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VisualBoost.Analysis;
using VisualBoost.Core.Analysis;

internal static class AnalysisProgressTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost-Progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new[] { Path.Combine(root, "A.cpp"), Path.Combine(root, "B.cpp"), Path.Combine(root, "Missing.cpp"), Path.Combine(root, "Note.txt") };
            File.WriteAllText(files[0], "void First() {}\n"); File.WriteAllText(files[1], "void Second() {}\n");
            using var analyzer = new SolutionSourceAnalyzer(new SourceAnalysisCache(Path.Combine(root, "cache")));
            var states = new List<SourceAnalysisProgress>();
            analyzer.Analyze(Path.Combine(root, "Fixture.sln"), files, Array.Empty<string>(), default, states.Add);
            var fileStates = states.Where(p => p.Stage != SourceAnalysisStage.Linking).ToArray();
            Check(states.Count > 0 && fileStates.All(p => p.Total == 3) && fileStates.Last().Completed == 3,
                "진행률은 C++ 대상과 읽기 실패를 포함해 완료하며 비소스 파일은 제외");
            Check(fileStates.Select(p => p.Completed).SequenceEqual(fileStates.Select(p => p.Completed).OrderBy(n => n)),
                "병렬 파일 처리의 완료 수가 역행하지 않음");
            Check(states.Any(p => p.Stage == SourceAnalysisStage.Parsing && files.Contains(p.Path)) &&
                states.Any(p => p.Stage == SourceAnalysisStage.Linking) && states.FindIndex(p => p.Stage == SourceAnalysisStage.Saving) < states.FindIndex(p => p.Stage == SourceAnalysisStage.Linking),
                "현재 파싱 파일·연결·저장 단계를 구분");
            Check(states.Where(p => p.Stage == SourceAnalysisStage.Linking).All(p => p.Total == 2 && p.Path is not null),
                "include 후처리는 실제 분석된 파일의 별도 진행 수와 경로를 표시");
            states.Clear();
            analyzer.Analyze(Path.Combine(root, "Fixture.sln"), files, Array.Empty<string>(), default, states.Add);
            Check(!states.Any(p => p.Stage == SourceAnalysisStage.Parsing) && states.All(p => p.Refreshing) &&
                  states.Where(p => p.Stage == SourceAnalysisStage.CacheChecking).Max(p => p.Completed) == 3,
                "캐시 재사용을 파싱으로 오표시하지 않고 다시 열기로 표시");
            // 다시 열기에서는 저장된 분석을 먼저 모두 채택해, 파싱 진행 수가 다시 읽을 파일(바뀐 A와 읽지 못한 Missing)만 셉니다.
            File.WriteAllText(files[0], "void FirstChanged() {}\n");
            File.SetLastWriteTimeUtc(files[0], DateTime.UtcNow.AddMinutes(1));
            states.Clear();
            analyzer.Analyze(Path.Combine(root, "Fixture.sln"), files, Array.Empty<string>(), default, states.Add);
            var parsing = states.Where(p => p.Stage == SourceAnalysisStage.Parsing).ToArray();
            Check(parsing.Length > 0 && parsing.All(p => p.Refreshing && p.Total == 2 && p.Path == files[0]) &&
                  states.Where(p => p.Stage == SourceAnalysisStage.CacheChecking && p.Total == 2).Max(p => p.Completed) == 2,
                "다시 열기의 파싱 진행은 바뀐 파일 수만 셈");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            states.Clear();
            try { analyzer.Analyze(Path.Combine(root, "Fixture.sln"), files, Array.Empty<string>(), cancellation.Token, states.Add); }
            catch (OperationCanceledException) { }
            Check(states.Count == 0, "취소 요청 이후 진행 상태를 게시하지 않음");
            VerifySharedIncludes(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static void VerifySharedIncludes(string root)
    {
        var common = Path.Combine(root, "common"); var local = Path.Combine(root, "local");
        Directory.CreateDirectory(common); Directory.CreateDirectory(local);
        var a = Path.Combine(root, "Global.cpp"); var b = Path.Combine(local, "Local.cpp");
        var commonHeader = Path.Combine(common, "Shared.h"); var localHeader = Path.Combine(local, "Shared.h");
        File.WriteAllText(a, "#include \"Shared.h\"\n"); File.WriteAllText(b, "#include \"Shared.h\"\n");
        File.WriteAllText(localHeader, "// local\n");
        using var analyzer = new SolutionSourceAnalyzer(new SourceAnalysisCache(Path.Combine(root, "include-cache")));
        var solution = Path.Combine(root, "Includes.sln");
        var first = analyzer.Analyze(solution, new[] { a, b }, new[] { common }, default);
        Check(first.Count == 1 && first.Contains(localHeader), "공통 include 탐색 실패 캐시가 다른 문서의 로컬 헤더를 가리지 않음");
        File.WriteAllText(commonHeader, "// shared\n");
        var next = analyzer.Analyze(solution, new[] { a, b }, new[] { common }, default);
        Check(next.Count == 2 && next.Contains(commonHeader) && next.Contains(localHeader),
            "새 분석 패스는 실패 캐시를 재사용하지 않고 공통·로컬 include 우선순위를 보존");
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
