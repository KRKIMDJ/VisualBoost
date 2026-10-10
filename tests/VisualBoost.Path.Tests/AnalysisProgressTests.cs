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
            analyzer.Analyze(Path.Combine(root, "Fixture.sln"), files, default, states.Add);
            var fileStates = states.ToArray();
            Check(states.Count > 0 && fileStates.All(p => p.Total == 3) && fileStates.Last().Completed == 3,
                "진행률은 C++ 대상과 읽기 실패를 포함해 완료하며 비소스 파일은 제외");
            Check(fileStates.Select(p => p.Completed).SequenceEqual(fileStates.Select(p => p.Completed).OrderBy(n => n)),
                "병렬 파일 처리의 완료 수가 역행하지 않음");
            Check(states.Any(p => p.Stage == SourceAnalysisStage.Parsing && files.Contains(p.Path)) &&
                states.Any(p => p.Stage == SourceAnalysisStage.Indexing) &&
                states.FindIndex(p => p.Stage == SourceAnalysisStage.Indexing) < states.FindIndex(p => p.Stage == SourceAnalysisStage.Saving),
                "현재 파싱 파일·공개·저장 단계를 구분");
            states.Clear();
            analyzer.Analyze(Path.Combine(root, "Fixture.sln"), files, default, states.Add);
            Check(!states.Any(p => p.Stage == SourceAnalysisStage.Parsing) && states.All(p => p.Refreshing) &&
                  states.Where(p => p.Stage == SourceAnalysisStage.CacheChecking).Max(p => p.Completed) == 3,
                "캐시 재사용을 파싱으로 오표시하지 않고 다시 열기로 표시");
            // 다시 열기에서는 저장된 분석을 먼저 모두 채택하고 없는 파일(Missing)도 1단계에서 정해, 파싱 진행 수가 바뀐 A만 셉니다.
            // 2단계는 파일 사이에 캐시 확인을 보고하지 않아 다시 열기 문구가 끊기지 않습니다.
            File.WriteAllText(files[0], "void FirstChanged() {}\n");
            File.SetLastWriteTimeUtc(files[0], DateTime.UtcNow.AddMinutes(1));
            states.Clear();
            analyzer.Analyze(Path.Combine(root, "Fixture.sln"), files, default, states.Add);
            var parsing = states.Where(p => p.Stage == SourceAnalysisStage.Parsing).ToArray();
            Check(parsing.Length > 0 && parsing.All(p => p.Refreshing && p.Total == 1 && (p.Path == files[0] || p.Path is null)) &&
                  parsing.Max(p => p.Completed) == 1 && states.Where(p => p.Stage == SourceAnalysisStage.CacheChecking).All(p => p.Total == 3),
                "다시 열기의 파싱 진행은 바뀐 파일 수만 셈");
            // 저장된 분석을 하나도 쓰지 못하면(분석 형식 변경 등) 1단계 뒤로는 처음 분석처럼 모든 단계를 보입니다.
            foreach (var file in files.Take(2))
            {
                File.AppendAllText(file, "// changed\n");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(2));
            }
            states.Clear();
            analyzer.Analyze(Path.Combine(root, "Fixture.sln"), files, default, states.Add);
            var afterReuse = states.SkipWhile(p => p.Refreshing).ToArray();
            Check(afterReuse.Length > 0 && afterReuse.All(p => !p.Refreshing) &&
                  afterReuse.Any(p => p.Stage == SourceAnalysisStage.CacheChecking) && afterReuse.Any(p => p.Stage == SourceAnalysisStage.Parsing && p.Total == 2),
                "저장된 분석을 하나도 쓰지 못한 다시 열기는 처음 분석처럼 표시");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            states.Clear();
            try { analyzer.Analyze(Path.Combine(root, "Fixture.sln"), files, cancellation.Token, states.Add); }
            catch (OperationCanceledException) { }
            Check(states.Count == 0, "취소 요청 이후 진행 상태를 게시하지 않음");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
