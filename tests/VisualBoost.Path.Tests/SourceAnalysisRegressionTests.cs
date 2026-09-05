using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Analysis;
using VisualBoost.Core.Analysis;
using VisualBoost.Services;

internal static class SourceAnalysisRegressionTests
{
    public static void Run()
    {
        // 실제 확장과 같은 .NET Framework에서 구 정규식의 정체를 안전한 시간 제한으로 재현합니다.
        const string oldPattern = @"(?<name>[A-Za-z_~]\w*(?:::[A-Za-z_~]\w*)*)\s*\([^;{}]*\)\s*(?:const\s*)?(?:noexcept\s*)?(?:(?:override|final)\s*)?(?:->[^;{]+)?\s*(?<terminator>[;{]?)\s*$";
        var stalled = false;
        var masked = "void Unsupported()" + new string(' ', 4000) + "QUALIFIER";
        try { new Regex(oldPattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)).Match(masked); }
        catch (RegexMatchTimeoutException) { stalled = true; }
        Check(stalled, "수정 전 긴 공백 구문의 100ms 시간 제한 초과 재현");

        var source = "namespace Sample {\nvoid Unsupported() /*" + new string('x', 4000) + "*/ QUALIFIER\n" +
            "void Valid() /*" + new string('x', 20000) + "*/ const;\n" +
            new string('A', 20000) + "(\nvoid After();\n}\n";
        var watch = Stopwatch.StartNew();
        var analysis = CppSourceAnalyzer.Analyze("Independent.h", source);
        Check(analysis.Symbols.Any(s => s.Name == "Valid" && s.Scope == "Sample") &&
            analysis.Symbols.Any(s => s.Name == "After"), "긴 주석·식별자 뒤의 정상 선언 유지");
        Check(watch.Elapsed < TimeSpan.FromSeconds(2), "수정 후 병적 구문 처리 2초 이내");
        Console.WriteLine($"INFO: 병적 구문 분석 {watch.Elapsed.TotalMilliseconds:F1}ms");

        using (var token = new CancellationTokenSource())
        {
            token.Cancel();
            ExpectCancellation(() => CppSourceAnalyzer.Analyze("Cancelled.h", source, token.Token));
        }
        using (var token = new CancellationTokenSource())
        using (var entered = new ManualResetEventSlim())
        {
            var large = string.Concat(Enumerable.Repeat(source, 1000));
            var running = Task.Run(() =>
            {
                entered.Set();
                ExpectCancellation(() => CppSourceAnalyzer.Analyze("Running.h", large, token.Token));
            });
            entered.Wait();
            token.CancelAfter(10);
            Check(running.Wait(TimeSpan.FromSeconds(2)), "진행 중인 파일 분석 취소 2초 이내");
        }

        var root = Path.Combine(Path.GetTempPath(), "VisualBoostAnalysisRegression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new SourceAnalysisCache(Path.Combine(root, "Cache"));
            var solution = Path.Combine(root, "Independent.sln");
            var files = Enumerable.Range(0, 100).Select(i => Path.Combine(root, "Sample" + i + ".h")).ToArray();
            foreach (var file in files) File.WriteAllText(file, source);
            using var analyzer = new SolutionSourceAnalyzer(cache);
            watch.Restart();
            analyzer.Analyze(solution, files, Array.Empty<string>(), CancellationToken.None);
            var cold = watch.Elapsed.TotalMilliseconds;
            Check(analyzer.FindSymbol("After").Count == files.Length && analyzer.LastWarning is null,
                "캐시 없는 100개 파일 전체 분석 및 심볼 공개");
            watch.Restart();
            analyzer.Analyze(solution, files, Array.Empty<string>(), CancellationToken.None);
            Check(analyzer.FindSymbol("After").Count == files.Length, "재분석 캐시 복원");
            Console.WriteLine($"INFO: 독립 100개 파일 최초 {cold:F1}ms / 캐시 {watch.Elapsed.TotalMilliseconds:F1}ms");

            using var service = new SolutionFileIndexService(new SolutionSourceAnalyzer(cache));
            var analyze = typeof(SolutionFileIndexService).GetMethod("AnalyzeSourcesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var flag = typeof(SolutionFileIndexService).GetField("isAnalyzing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            // 실제 작업 경계를 호출하되 파일 탐색/엔진 탐지 및 사용자 캐시에는 접근하지 않습니다.
            flag.SetValue(service, true);
            ((Task)analyze.Invoke(service, new object[] { "\0invalid", files, Array.Empty<string>(), TimeSpan.Zero, CancellationToken.None })!).GetAwaiter().GetResult();
            Check(!service.GetSnapshot().IsAnalyzing && service.GetSnapshot().AnalysisError is not null,
                "캐시 초기화 예외도 분석 중 표시 해제 및 오류 공개");
            flag.SetValue(service, true);
            ((Task)analyze.Invoke(service, new object[] { solution, files, Array.Empty<string>(), TimeSpan.Zero, CancellationToken.None })!).GetAwaiter().GetResult();
            Check(!service.GetSnapshot().IsAnalyzing && service.GetSnapshot().AnalysisError is null && service.FindSymbol("After").Count == 100,
                "오류 후 재실행 성공 및 상태 복구");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void ExpectCancellation(Action action)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("분석 취소가 전파되지 않았습니다.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
