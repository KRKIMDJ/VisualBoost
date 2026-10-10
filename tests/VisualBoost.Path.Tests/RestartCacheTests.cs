using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using VisualBoost.Analysis;
using VisualBoost.Core.Analysis;
using VisualBoost.Services;

internal static class RestartCacheTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost-Restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { VerifySymbols(root); VerifyPooling(root); VerifyPartialUpdate(root); VerifyServiceReopen(root); VerifyProjectItemChanges(root);
              VerifyCacheRobustness(root); VerifyServiceReset(root); }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifySymbols(string root)
    {
        var dir = Path.Combine(root, "symbols"); var solution = Path.Combine(root, "Fixture.sln"); var file = Path.Combine(root, "A.h");
        File.WriteAllText(file, "struct A {};\n");
        using (var first = new SolutionSourceAnalyzer(new SourceAnalysisCache(dir)))
            first.Analyze(solution, new[] { file }, default);
        var stored = Directory.GetFiles(dir, "*.bin").Single();
        var sentinel = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(stored, sentinel);
        var phases = new List<SourceAnalysisProgress>();
        using (var reopened = new SolutionSourceAnalyzer(new SourceAnalysisCache(dir)))
        {
            var unrelated = Path.Combine(root, "Unrelated.h");
            var start = reopened.SymbolRevision;
            reopened.LoadCachedSymbols(solution, default);
            Check(reopened.FindSymbol("A").Count == 1 && reopened.CachedSymbolsPublished && reopened.SymbolRevision == start + 1,
                "다시 열 때 저장된 분석 전체를 수집 전에 한 번 공개(전체 공개 표시)");
            reopened.LoadCachedSymbols(solution, default, new[] { file, unrelated });
            Check(reopened.SymbolRevision == start + 1, "수집 목록이 저장된 분석을 모두 담으면 이름 인덱스를 다시 만들지 않음");
            reopened.Analyze(solution, new[] { file }, default, phases.Add);
            Check(!phases.Any(p => p.Stage == SourceAnalysisStage.Parsing || p.Stage == SourceAnalysisStage.Saving) && reopened.SymbolRevision == start + 1,
                "재실행 무변경 파일 재파싱·재저장·이름 인덱스 재공개 0회");
            Check(File.GetLastWriteTimeUtc(stored) == sentinel, "무변경 캐시 파일 실제 쓰기 없음");
            reopened.LoadCachedSymbols(solution, default, new[] { unrelated });
            Check(reopened.FindSymbol("A").Count == 0 && reopened.SymbolCount == 0, "수집 목록에서 빠진 파일의 저장 심볼만 뺌");
            reopened.Clear();
            Check(!reopened.CachedSymbolsPublished, "Clear 뒤 전체 공개 표시 해제");
            using (var stale = new CancellationTokenSource())
            {
                stale.Cancel();
                Throws<OperationCanceledException>(() => reopened.LoadCachedSymbols(solution, stale.Token));
                Check(reopened.SymbolCount == 0 && !reopened.CachedSymbolsPublished, "취소된 복원은 공개하지 않음");
            }
            reopened.LoadCachedSymbols(solution + ".none", default);
            Check(!reopened.CachedSymbolsPublished && reopened.SymbolCount == 0, "저장된 분석이 없으면 전체 공개로 보지 않음");
            reopened.LoadCachedSymbols(solution, default, new[] { file });
            Check(reopened.FindSymbol("A").Count == 1, "다른 Solution을 본 뒤 저장된 분석을 다시 읽어 공개");
            File.WriteAllText(file, "struct Changed {};\n"); phases.Clear();
            var beforeChange = reopened.SymbolRevision;
            reopened.Analyze(solution, new[] { file }, default, phases.Add);
            Check(phases.Count(p => p.Stage == SourceAnalysisStage.Parsing) == 1 && reopened.FindSymbol("Changed").Count == 1 && reopened.FindSymbol("A").Count == 0 &&
                  reopened.SymbolRevision == beforeChange + 1,
                "변경 파일만 재파싱하고 이전 심볼을 한 번의 부분 갱신으로 교체");
            File.Delete(file); reopened.Analyze(solution, new[] { file }, default);
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
        Check(old.NeedsUpgrade && entries[file].Revision == 1, "v4 캐시는 분석 버전 1로 읽어 다음 분석에서 다시 분석");
        old.Save(solution, entries);
        Check(!old.NeedsUpgrade && new SourceAnalysisCache(dir).Load(solution).Count == 1, "v6 명시적 분석 버전 왕복");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Throws<OperationCanceledException>(() => old.Load(solution, canceled.Token));
        Throws<OperationCanceledException>(() => old.Save(solution, entries, canceled.Token));
    }

    private static void VerifyPooling(string root)
    {
        var dir = Path.Combine(root, "pool"); var solution = Path.Combine(root, "Pool.sln");
        var first = Path.Combine(root, "PoolA.h"); var second = Path.Combine(root, "PoolB.h");
        File.WriteAllText(first, "namespace Game\n{\nstruct FShared\n{\n    int Value();\n};\n}\n");
        File.WriteAllText(second, "namespace Game\n{\nstruct FShared\n{\n    int Value();\n};\n}\n");
        var files = new[] { first, second };
        bool Pooled(SolutionSourceAnalyzer analyzer)
        {
            var found = analyzer.FindSymbol("Value");
            return found.Count == 2 && ReferenceEquals(found[0].Name, found[1].Name) && ReferenceEquals(found[0].Scope, found[1].Scope) &&
                   ReferenceEquals(found[0].Signature, found[1].Signature);
        }

        using (var analyzer = new SolutionSourceAnalyzer(new SourceAnalysisCache(dir)))
        {
            analyzer.Analyze(solution, files, default);
            Check(Pooled(analyzer), "새로 분석한 파일들의 같은 이름·소속·시그니처는 한 문자열");
            var phases = new List<SourceAnalysisProgress>();
            analyzer.ReleasePreviousAnalysis();
            analyzer.Analyze(solution, files, default, phases.Add);
            Check(!phases.Any(p => p.Stage == SourceAnalysisStage.Parsing), "지난 분석을 놓은 뒤에도 저장된 분석을 다시 읽어 재파싱 없음");
        }

        using (var reopened = new SolutionSourceAnalyzer(new SourceAnalysisCache(dir)))
        {
            reopened.LoadCachedSymbols(solution, default, files);
            Check(Pooled(reopened), "저장된 분석을 읽을 때도 같은 문자열은 한 인스턴스");
        }
    }

    /// <summary>
    /// 서비스의 다시 열기(수집 시작 → 수집 공개 → Start → 분석)에서 이름 인덱스를 비우기와 저장된 분석 공개 한 번으로만 바꿉니다. 수집 완료(Start)가
    /// 복원을 취소하거나, 수집 묶음·수집 완료·분석 끝마다 다시 만들면 공개 번호가 더 늘어납니다.
    /// </summary>
    private static void VerifyServiceReopen(string root)
    {
        var source = Path.Combine(root, "ServiceSource");
        Directory.CreateDirectory(source);
        var header = Path.Combine(source, "Service.h");
        File.WriteAllText(header, "struct ServiceName {};\n");
        var solution = Path.Combine(root, "Service.sln");
        SolutionFileIndexService Open() => new(new SolutionSourceAnalyzer(new SourceAnalysisCache(Path.Combine(root, "service-analysis"))),
            new FileIndexCache(Path.Combine(root, "service-files")));
        void Reopen(SolutionFileIndexService service)
        {
            service.Configure(new SolutionFileIndexConfiguration(true, true, TimeSpan.Zero));
            service.BeginDiscovery(solution);
            service.PublishDiscoveredFiles(new[] { header }, default);
            service.Start(new SolutionIndexDiscoveryResult(solution, new[] { source }, new[] { header }));
            service.WaitUntilReadyAsync().GetAwaiter().GetResult();
            service.WaitUntilAnalysisReadyAsync().GetAwaiter().GetResult();
        }
        using (var first = Open()) Reopen(first);
        using var second = Open();
        var start = second.SymbolRevision;
        Reopen(second);
        Check(second.FindSymbol("ServiceName").Count == 1 && second.SymbolRevision - start == 2,
            "다시 열기는 비우기와 저장된 분석 공개 한 번으로 이름 인덱스 완성: 공개 " + (second.SymbolRevision - start) + "회");
    }

    /// <summary>
    /// 수집 루트가 그대로인 프로젝트 항목 변경은 인덱스를 비우거나 파일 목록을 다시 만들지 않고 바뀐 파일만 반영합니다. 디스크에 남은 파일도
    /// 프로젝트에서 빠지고 수집 루트 밖이면 목록과 이름 인덱스에서 뺍니다.
    /// </summary>
    private static void VerifyProjectItemChanges(string root)
    {
        var source = Path.Combine(root, "ItemSource");
        var outside = Path.Combine(root, "ItemOutside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        var kept = Path.Combine(source, "Kept.h");
        var linked = Path.Combine(outside, "Linked.h");
        File.WriteAllText(kept, "struct KeptItem {};\n");
        File.WriteAllText(linked, "struct LinkedItem {};\n");
        var solution = Path.Combine(root, "Items.sln");
        SolutionIndexDiscoveryResult Discovery(params string[] files) => new(solution, new[] { source }, files);
        bool Listed(SolutionFileIndexService index, string path) => index.GetFilePathsSnapshot().Contains(path, StringComparer.OrdinalIgnoreCase);
        using var service = new SolutionFileIndexService(new SolutionSourceAnalyzer(new SourceAnalysisCache(Path.Combine(root, "items-analysis"))),
            new FileIndexCache(Path.Combine(root, "items-files")));
        service.Configure(new SolutionFileIndexConfiguration(true, true, TimeSpan.Zero));
        service.BeginDiscovery(solution);
        service.Start(Discovery(kept));
        service.WaitUntilReadyAsync().GetAwaiter().GetResult();
        service.WaitUntilAnalysisReadyAsync().GetAwaiter().GetResult();
        var generation = service.Generation;

        service.Start(Discovery(kept, linked));
        Check(service.GetSnapshot().State == SolutionFileIndexState.Ready && service.FindSymbol("KeptItem").Count == 1 && service.Generation == generation,
            "항목 추가는 인덱스를 비우거나 파일 목록을 다시 만들지 않음");
        Check(SpinUntil(() => service.FindSymbol("LinkedItem").Count == 1 && Listed(service, linked)), "추가한 항목 파일만 분석해 목록·이름 인덱스에 반영");

        service.Start(Discovery(kept));
        Check(SpinUntil(() => service.FindSymbol("LinkedItem").Count == 0 && !Listed(service, linked)) && File.Exists(linked),
            "디스크에 남아도 프로젝트에서 뺀 수집 루트 밖 파일은 목록·이름 인덱스에서 뺌");

        service.Start(Discovery());
        Check(SpinUntil(() => !service.GetSnapshot().IsAnalyzing) && !SpinUntil(() => service.FindSymbol("KeptItem").Count == 0 || !Listed(service, kept), 1500) &&
              service.Generation == generation, "수집 루트 안 파일은 항목에서 빠져도 보충 코드로 남고, 항목 변경 내내 다시 수집하지 않음");
    }

    private static void VerifyCacheRobustness(string root)
    {
        var dir = Path.Combine(root, "robust");
        var solution = Path.Combine(root, "Robust.sln");
        var generated = Path.Combine(root, "Generated.h");
        var many = Enumerable.Range(0, 100_001)
            .Select(i => new SourceSymbolLocation("Generated" + i, generated, i + 1, 1, SourceSymbolKind.Function)).ToArray();
        var cache = new SourceAnalysisCache(dir);
        cache.Save(solution, new Dictionary<string, CachedSourceAnalysis>
        {
            [generated] = new(10, 20, new SourceFileAnalysis(generated, Array.Empty<SourceIncludeReference>(), many)),
        });
        var loaded = cache.Load(solution);
        Check(loaded.Count == 1 && loaded[generated].Analysis.Symbols.Count == 100_000,
            "파일당 항목 상한을 넘는 분석은 잘라 저장해 다음 열기에서 캐시 전체를 버리지 않음");
        var stored = Directory.GetFiles(dir, "*.bin").Single();
        var leftover = stored + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(leftover, "partial");
        cache.Save(solution, loaded);
        Check(!File.Exists(leftover) && File.Exists(stored), "저장 도중 끝나 남은 임시 파일을 다음 저장에서 지움");
        File.WriteAllText(leftover, "partial");
        Check(cache.Delete(solution) && !File.Exists(stored) && !File.Exists(leftover) && cache.Load(solution).Count == 0,
            "Solution의 저장된 분석과 남은 임시 파일 지우기");
    }

    /// <summary>인덱스 다시 만들기의 서비스 쪽: 인덱스를 비우고 이 Solution의 저장된 파일 목록·분석을 지웁니다. Dispose는 취소한 작업을 잠시 기다립니다.</summary>
    private static void VerifyServiceReset(string root)
    {
        var source = Path.Combine(root, "ResetSource");
        Directory.CreateDirectory(source);
        var files = Enumerable.Range(0, 200).Select(i => Path.Combine(source, "Reset" + i + ".h")).ToArray();
        for (var i = 0; i < files.Length; i++) File.WriteAllText(files[i], "struct ResetName" + i + " { void Run(); };\n");
        var solution = Path.Combine(root, "Reset.sln");
        var analysisDirectory = Path.Combine(root, "reset-analysis");
        var filesDirectory = Path.Combine(root, "reset-files");
        SolutionFileIndexService Open()
        {
            var opened = new SolutionFileIndexService(new SolutionSourceAnalyzer(new SourceAnalysisCache(analysisDirectory)), new FileIndexCache(filesDirectory));
            opened.Configure(new SolutionFileIndexConfiguration(true, true, TimeSpan.Zero));
            opened.BeginDiscovery(solution);
            opened.Start(new SolutionIndexDiscoveryResult(solution, new[] { source }, files));
            return opened;
        }
        bool Stored(string directory) => Directory.Exists(directory) && Directory.GetFiles(directory, "*.bin").Length > 0;

        using (var service = Open())
        {
            service.WaitUntilReadyAsync().GetAwaiter().GetResult();
            service.WaitUntilAnalysisReadyAsync().GetAwaiter().GetResult();
            Check(Stored(analysisDirectory) && Stored(filesDirectory) && service.FindSymbol("ResetName0").Count == 1, "다시 만들기 전 저장된 결과");
            Check(service.ResetAsync().GetAwaiter().GetResult() && !Stored(analysisDirectory) && !Stored(filesDirectory) &&
                  service.FindSymbol("ResetName0").Count == 0 && service.GetSnapshot().State == SolutionFileIndexState.Empty,
                "인덱스를 비우고 이 Solution의 저장된 파일 목록·분석을 지움");
        }

        // 분석 도중 닫아도 Dispose가 취소한 작업을 기다려 임시 파일이 남지 않고 폴더를 바로 지울 수 있습니다.
        var closing = Open();
        closing.Dispose();
        Check(!Directory.Exists(analysisDirectory) || Directory.GetFiles(analysisDirectory, "*.tmp").Length == 0, "분석 도중 Dispose 뒤 임시 파일 없음");
        Directory.Delete(source, recursive: true);
    }

    private static bool SpinUntil(Func<bool> condition, int milliseconds = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(25);
        }
        return true;
    }

    private static void VerifyPartialUpdate(string root)
    {
        var dir = Path.Combine(root, "partial"); var solution = Path.Combine(root, "Partial.sln");
        var first = Path.Combine(root, "PartA.h"); var second = Path.Combine(root, "PartB.h");
        File.WriteAllText(first, "void KeepPart();\n");
        File.WriteAllText(second, "void OldPart();\n");
        var files = new[] { first, second };
        using (var analyzer = new SolutionSourceAnalyzer(new SourceAnalysisCache(dir)))
        {
            Check(!analyzer.UpdateFiles(solution, new[] { second }, default), "마친 분석 패스 전에는 부분 갱신하지 않음");
            analyzer.Analyze(solution, files, default);
            File.WriteAllText(second, "void NewPartName();\n");
            Check(analyzer.UpdateFiles(solution, new[] { second }, default) && analyzer.FindSymbol("NewPartName").Count == 1 &&
                analyzer.FindSymbol("OldPart").Count == 0 && analyzer.FindSymbol("KeepPart").Count == 1, "저장한 파일만 다시 분석해 이름 인덱스 갱신");
            File.Delete(first);
            Check(analyzer.UpdateFiles(solution, new[] { first }, default) && analyzer.FindSymbol("KeepPart").Count == 0, "지운 파일의 이름 제거");
            // 부분 갱신은 분석 캐시 파일에 쓰지 않으므로, 다음 패스는 바뀐 파일이 없어도 저장해야 합니다.
            analyzer.Analyze(solution, new[] { second }, default);
        }
        using (var reopened = new SolutionSourceAnalyzer(new SourceAnalysisCache(dir)))
        {
            reopened.LoadCachedSymbols(solution, default);
            Check(reopened.FindSymbol("NewPartName").Count == 1 && reopened.FindSymbol("OldPart").Count == 0 &&
                reopened.FindSymbol("KeepPart").Count == 0, "부분 갱신 결과를 다음 분석 패스가 저장");
        }
    }

    private static void Check(bool value, string message) { CheckSilent(value, message); Console.WriteLine("PASS: " + message); }
    private static void CheckSilent(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { Console.WriteLine("PASS: " + typeof(T).Name); return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
