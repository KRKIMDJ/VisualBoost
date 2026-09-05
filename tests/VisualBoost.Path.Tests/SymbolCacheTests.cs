using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using VisualBoost.Analysis;
using VisualBoost.Core.Analysis;

internal static class SymbolCacheTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoostSymbolCache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new SourceAnalysisCache(root);
            var solution = Path.Combine(root, "Independent.sln");
            var path = Path.Combine(root, "Actor.h");
            var location = new SourceSymbolLocation("Move", path, 4, 10, SourceSymbolKind.Function, "Game::Actor", "(float distance)");
            cache.Save(solution, new[] { new KeyValuePair<string, CachedSourceAnalysis>(path,
                new CachedSourceAnalysis(10, 20, new SourceFileAnalysis(path, Array.Empty<SourceIncludeReference>(), new[] { location }))) });
            var restored = cache.Load(solution)[path].Analysis.Symbols.Single();
            if (restored.Description != location.Description || restored.Column != 10) throw new InvalidOperationException("상세 정보 캐시 왕복 실패");
            var file = Directory.GetFiles(root, "*.bin").Single();
            using (var stream = File.Create(file))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            { writer.Write("VisualBoost.SourceAnalysis"); writer.Write(2); }
            if (cache.Load(solution).Count != 0) throw new InvalidOperationException("구버전 캐시 무효화 실패");
            File.WriteAllText(file, "broken");
            if (cache.Load(solution).Count != 0) throw new InvalidOperationException("손상 캐시 처리 실패");
            Console.WriteLine("PASS: 상세 정보 캐시 왕복·v2 무효화·손상 복구 (독립 임시 폴더)");

            File.WriteAllText(path, "class Actor {};\nenum class Mode { First };\n");
            var info = new FileInfo(path);
            cache.Save(solution, new[] { new KeyValuePair<string, CachedSourceAnalysis>(path,
                new CachedSourceAnalysis(info.Length, info.LastWriteTimeUtc.Ticks,
                    new SourceFileAnalysis(path, Array.Empty<SourceIncludeReference>(), new[] {
                        new SourceSymbolLocation("Actor", path, 1, 7, SourceSymbolKind.Type) }))) });
            using var analyzer = new SolutionSourceAnalyzer(cache);
            analyzer.LoadCachedSymbols(solution, CancellationToken.None);
            if (analyzer.FindSymbol("Actor").Count != 1) throw new InvalidOperationException("구 타입 캐시 선공개 실패");
            analyzer.Analyze(solution, new[] { path }, Array.Empty<string>(), CancellationToken.None);
            var updated = cache.Load(solution)[path].Analysis.Symbols;
            if (updated.Single(s => s.Name == "Actor").Kind != SourceSymbolKind.Class ||
                updated.Single(s => s.Name == "Mode").Kind != SourceSymbolKind.Enum)
                throw new InvalidOperationException("기존 타입 캐시의 상세 종류 보강 실패");
            analyzer.Analyze(solution, new[] { path }, Array.Empty<string>(), CancellationToken.None);
            if (analyzer.FindSymbol("Actor").Single().Kind != SourceSymbolKind.Class)
                throw new InvalidOperationException("상세 종류 캐시 재사용 실패");
            Console.WriteLine("PASS: 기존 타입 캐시 선공개·상세 종류 보강·저장 및 재사용");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
