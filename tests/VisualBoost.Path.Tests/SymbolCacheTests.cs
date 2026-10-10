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
            VerifyLegacyVersions(cache, solution, path, file);
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
                        new SourceSymbolLocation("Actor", path, 1, 7, SourceSymbolKind.Type) }), revision: 0)) });
            using var analyzer = new SolutionSourceAnalyzer(cache);
            analyzer.LoadCachedSymbols(solution, CancellationToken.None);
            if (analyzer.FindSymbol("Actor").Count != 1) throw new InvalidOperationException("구 타입 캐시 선공개 실패");
            analyzer.Analyze(solution, new[] { path }, CancellationToken.None);
            var updated = cache.Load(solution)[path].Analysis.Symbols;
            if (updated.Single(s => s.Name == "Actor").Kind != SourceSymbolKind.Class ||
                updated.Single(s => s.Name == "Mode").Kind != SourceSymbolKind.Enum)
                throw new InvalidOperationException("기존 타입 캐시의 상세 종류 보강 실패");
            analyzer.Analyze(solution, new[] { path }, CancellationToken.None);
            if (analyzer.FindSymbol("Actor").Single().Kind != SourceSymbolKind.Class)
                throw new InvalidOperationException("상세 종류 캐시 재사용 실패");
            Console.WriteLine("PASS: 기존 타입 캐시 선공개·상세 종류 보강·저장 및 재사용");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void VerifyLegacyVersions(SourceAnalysisCache cache, string solution, string path, string file)
    {
        // 두 파일과 여러 심볼을 연속 기록하여 폐기 필드의 읽기 위치까지 검증합니다.
        foreach (var version in new[] { 3, 4, 5 })
        {
            using (var writer = new BinaryWriter(File.Create(file), Encoding.UTF8))
            {
                writer.Write("VisualBoost.SourceAnalysis"); writer.Write(version); writer.Write(2);
                foreach (var suffix in new[] { "", ".second" })
                {
                    writer.Write(path + suffix);
                    if (version >= 5) writer.Write(CachedSourceAnalysis.CurrentRevision);
                    writer.Write(10L); writer.Write(20L); writer.Write(1);
                    writer.Write("Base.h"); writer.Write(false); writer.Write(1);
                    writer.Write(2);
                    foreach (var name in new[] { "Move", "Stop" })
                    {
                        writer.Write(name); writer.Write(4); writer.Write(10);
                        writer.Write((int)SourceSymbolKind.Function); writer.Write("Game::Actor"); writer.Write("(float distance)");
                        if (version >= 4) writer.Write(2);
                    }
                }
            }
            var loaded = cache.Load(solution);
            if (!cache.NeedsUpgrade || loaded.Count != 2 || loaded.Any(pair =>
                pair.Value.Revision != (version >= 5 ? CachedSourceAnalysis.CurrentRevision : 1) ||
                pair.Value.Analysis.Includes.Count != 0 ||
                !pair.Value.Analysis.Symbols.Select(s => s.Name).SequenceEqual(new[] { "Move", "Stop" }) ||
                pair.Value.Analysis.Symbols.Any(s => s.Scope != "Game::Actor" || s.Signature != "(float distance)")))
                throw new InvalidOperationException("구형 심볼 캐시 이관 실패: " + version);
            cache.Save(solution, loaded);
            var reopened = new SourceAnalysisCache(Path.GetDirectoryName(file));
            if (reopened.Load(solution).Count != 2 || reopened.NeedsUpgrade)
                throw new InvalidOperationException("새 형식 캐시 재개방 실패: " + version);
            using var reader = new BinaryReader(File.OpenRead(file), Encoding.UTF8);
            reader.ReadString();
            if (reader.ReadInt32() != 6) throw new InvalidOperationException("v6 저장 형식 누락");
            Console.WriteLine("PASS: v" + version + " 심볼 캐시를 v6으로 이관하고(include 목록은 읽고 버림) 새 인스턴스에서 복원");
        }
    }
}
