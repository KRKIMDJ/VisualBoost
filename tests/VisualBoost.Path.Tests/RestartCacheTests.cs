using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using VisualBoost.Analysis;
using VisualBoost.Core.Analysis;

internal static class RestartCacheTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost-Restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { VerifySymbols(root); VerifyRoots(root); }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifySymbols(string root)
    {
        var dir = Path.Combine(root, "symbols"); var solution = Path.Combine(root, "Fixture.sln"); var file = Path.Combine(root, "A.h");
        File.WriteAllText(file, "struct A {};\n");
        using (var first = new SolutionSourceAnalyzer(new SourceAnalysisCache(dir)))
        using (var cancel = new CancellationTokenSource())
        {
            try { first.Analyze(solution, new[] { file }, Array.Empty<string>(), cancel.Token,
                p => { if (p.Stage == SourceAnalysisStage.Linking) cancel.Cancel(); }); }
            catch (OperationCanceledException) { }
        }
        var stored = Directory.GetFiles(dir, "*.bin").Single();
        var sentinel = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(stored, sentinel);
        var phases = new List<SourceAnalysisProgress>();
        using (var reopened = new SolutionSourceAnalyzer(new SourceAnalysisCache(dir)))
        {
            reopened.PublishCachedDiscovery(solution, new[] { file }, default);
            Check(reopened.SymbolCount == 0, "캐시 준비 전에는 수집 파일을 임의 심볼로 공개하지 않음");
            reopened.PrepareCachedDiscovery(solution, default);
            reopened.PublishCachedDiscovery(solution + ".other", new[] { file }, default);
            Check(reopened.SymbolCount == 0, "다른 Solution의 발견 묶음 제외");
            reopened.PublishCachedDiscovery(solution, new[] { Path.Combine(root, "Unrelated.h") }, default);
            Check(reopened.SymbolCount == 0, "발견되지 않은 캐시 파일은 선공개하지 않음");
            reopened.PublishCachedDiscovery(solution, new[] { file }, default);
            reopened.PublishCachedDiscovery(solution, new[] { file }, default);
            Check(reopened.FindSymbol("A").Count == 1, "수집 도중 확인된 파일의 저장 심볼을 중복 없이 공개");
            Check(!reopened.CachedSymbolsPublished, "수집 도중 공개한 일부 저장 심볼은 전체 공개로 보지 않음");
            reopened.Clear();
            reopened.PublishCachedDiscovery(solution, new[] { file }, default);
            Check(reopened.SymbolCount == 0, "Clear 뒤 늦은 수집 결과 제외");
            using (var stale = new CancellationTokenSource())
            {
                reopened.PrepareCachedDiscovery(solution, default); stale.Cancel();
                Throws<OperationCanceledException>(() => reopened.PublishCachedDiscovery(solution, new[] { file }, stale.Token));
                Check(reopened.SymbolCount == 0, "취소된 수집 세대의 저장 심볼 제외");
            }
            reopened.LoadCachedSymbols(solution, default, new[] { file });
            Check(reopened.FindSymbol("A").Count == 1 && reopened.CachedSymbolsPublished, "후처리 취소 후 새 인스턴스에서 저장된 심볼 즉시 복원(전체 공개 표시)");
            reopened.LoadCachedSymbols(solution + ".none", default);
            Check(!reopened.CachedSymbolsPublished, "저장된 분석이 없으면 전체 공개로 보지 않음");
            reopened.LoadCachedSymbols(solution, default, new[] { file });
            reopened.Clear();
            Check(!reopened.CachedSymbolsPublished, "Clear 뒤 전체 공개 표시 해제");
            reopened.LoadCachedSymbols(solution, default, new[] { file });
            reopened.Analyze(solution, new[] { file }, Array.Empty<string>(), default, phases.Add);
            Check(!phases.Any(p => p.Stage == SourceAnalysisStage.Parsing || p.Stage == SourceAnalysisStage.Saving), "재실행 무변경 파일 재파싱·재저장 0회");
            Check(File.GetLastWriteTimeUtc(stored) == sentinel, "무변경 캐시 파일 실제 쓰기 없음");
            File.WriteAllText(file, "struct Changed {};\n"); phases.Clear();
            reopened.Analyze(solution, new[] { file }, Array.Empty<string>(), default, phases.Add);
            Check(phases.Count(p => p.Stage == SourceAnalysisStage.Parsing) == 1 && reopened.FindSymbol("Changed").Count == 1 && reopened.FindSymbol("A").Count == 0,
                "변경 파일만 재파싱하고 이전 심볼 제거");
            File.Delete(file); reopened.Analyze(solution, new[] { file }, Array.Empty<string>(), default);
            Check(reopened.FindSymbol("Changed").Count == 0, "삭제 파일의 저장 심볼 제거");
        }
        using (var stream = File.Create(stored))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write("VisualBoost.SourceAnalysis"); writer.Write(4); writer.Write(1); writer.Write(file);
            writer.Write(1L); writer.Write(1L); writer.Write(0); writer.Write(1);
            writer.Write("Legacy"); writer.Write(1); writer.Write(1); writer.Write((int)SourceSymbolKind.Class);
            writer.Write(""); writer.Write(""); writer.Write(2);
        }
        var old = new SourceAnalysisCache(dir); var entries = old.Load(solution);
        Check(old.NeedsUpgrade && entries[file].Revision == CachedSourceAnalysis.CurrentRevision, "v4 현재 상세 심볼을 재파싱 없이 이관 가능");
        old.Save(solution, entries);
        Check(!old.NeedsUpgrade && new SourceAnalysisCache(dir).Load(solution).Count == 1, "v6 명시적 분석 버전 왕복");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Throws<OperationCanceledException>(() => old.Load(solution, canceled.Token));
        Throws<OperationCanceledException>(() => old.Save(solution, entries, canceled.Token));
    }

    private static void VerifyRoots(string root)
    {
        var paths = Enumerable.Range(0, 80).Select(i => Path.Combine(root, "includes", "Root" + i)).ToArray();
        foreach (var path in paths) Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(paths[5], "Nested"));
        File.WriteAllText(Path.Combine(paths[5], "Nested", "Found.h"), "");
        File.WriteAllText(Path.Combine(paths[3], "Shared.h"), ""); File.WriteAllText(Path.Combine(paths[9], "Shared.h"), "");
        var lookup = new IncludeRootLookup(paths);
        var invalidSource = Path.Combine(root, "InvalidInclude.cpp");
        File.WriteAllText(invalidSource, "#include <bad<path>\n#include \"Shared.h\"\nstruct Survives {};\n");
        using (var analyzer = new SolutionSourceAnalyzer(new SourceAnalysisCache(Path.Combine(root, "invalid-cache"))))
        {
            analyzer.Analyze(Path.Combine(root, "Invalid.sln"), new[] { invalidSource }, paths, default);
            Check(analyzer.FindSymbol("Survives").Count == 1 && analyzer.IncludeEdgeCount == 1 &&
                analyzer.LastWarning?.Contains("include 1개") == true, "잘못된 include만 진단하고 정상 연결·이름 결과 유지");
        }
        for (var i = 0; i < 500; i++) CheckSilent(lookup.Find("Missing" + i + ".h", default) is null, "missing include");
        Check(lookup.FileProbeCount == 0, "80개 루트·없는 include 500개에서 40,000회 파일 확인 제거");
        Check(lookup.Find("Shared.h", default) == Path.Combine(paths[3], "Shared.h"), "공통 루트 원래 우선순위 보존");
        Check(lookup.Find(Path.Combine("Nested", "Found.h"), default) == Path.Combine(paths[5], "Nested", "Found.h"), "하위 폴더 include 연결");
        File.WriteAllText(Path.Combine(paths[0], "New.h"), "");
        Check(new IncludeRootLookup(paths).Find("New.h", default) == Path.Combine(paths[0], "New.h"), "다음 분석 패스의 새 파일 반영");
    }
    private static void Check(bool value, string message) { CheckSilent(value, message); Console.WriteLine("PASS: " + message); }
    private static void CheckSilent(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { Console.WriteLine("PASS: " + typeof(T).Name); return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
