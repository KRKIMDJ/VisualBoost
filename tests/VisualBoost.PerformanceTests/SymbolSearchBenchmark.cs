using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Searching;

namespace VisualBoost.PerformanceTests;

internal static class SymbolSearchBenchmark
{
    private static int Main(string[] args) => Run(args[0]);

    public static int Run(string cachePath)
    {
        // 기존 캐시는 읽기 전용으로 열고 분석 결과의 위치만 성능 검증에 사용합니다.
        var symbols = new List<SourceSymbolLocation>();
        using (var reader = new BinaryReader(File.OpenRead(cachePath)))
        {
            if (reader.ReadString() != "VisualBoost.SourceAnalysis" || reader.ReadInt32() != 2)
                throw new InvalidDataException("지원하지 않는 심볼 캐시 형식입니다.");
            var files = reader.ReadInt32();
            for (var f = 0; f < files; f++)
            {
                var path = reader.ReadString();
                reader.ReadInt64(); reader.ReadInt64();
                var includes = reader.ReadInt32();
                for (var i = 0; i < includes; i++) { reader.ReadString(); reader.ReadBoolean(); reader.ReadInt32(); }
                var count = reader.ReadInt32();
                for (var i = 0; i < count; i++)
                    symbols.Add(new SourceSymbolLocation(reader.ReadString(), path,
                        reader.ReadInt32(), reader.ReadInt32(), (SourceSymbolKind)reader.ReadInt32()));
            }
        }
        using var index = new SourceSymbolIndex();
        index.ReplaceAll(symbols);
        Console.WriteLine($"심볼 위치 {symbols.Count:N0}, 고유 이름 {symbols.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count():N0}");
        foreach (var query in new[] { "S", "Set", "SetMove", "SetMovementMode", "SMMode", "GetWorld", "XYZMissingSymbol" })
        {
            var timer = Stopwatch.StartNew();
            var baseline = FuzzySymbolSearch.Search(query, symbols, 200);
            var baselineMs = timer.Elapsed.TotalMilliseconds;
            var samples = new double[7];
            for (var iteration = 0; iteration < samples.Length; iteration++)
            {
                timer.Restart();
                var result = index.Search(query, 200);
                samples[iteration] = timer.Elapsed.TotalMilliseconds;
                if (!result.Select(Key).SequenceEqual(baseline.Select(Key)))
                    throw new InvalidOperationException("검색 순위 또는 결과가 달라졌습니다: " + query);
            }
            Array.Sort(samples);
            Console.WriteLine($"{query}: 이전 {baselineMs:F1} ms / 개선 median {samples[3]:F1} ms, max {samples[6]:F1} ms / {baseline.Count}개");
        }
        return 0;
    }

    private static string Key(SourceSymbolMatch match) =>
        $"{match.Score}|{match.Location.Name}|{match.Location.Path}|{match.Location.Line}|{match.Location.Column}";
}
