using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Analysis;

internal static class ProgressiveIndexTests
{
    public static void Run()
    {
        // 사용자 프로젝트나 캐시를 읽지 않고 동명 심볼이 많은 대규모 인덱스를 구성합니다.
        var symbols = Enumerable.Range(0, 300_000).Select(i => new SourceSymbolLocation(
            "SetMovementMode" + i % 30_000, @"C:\Fixture\Unit" + i / 100 + ".h", i % 100 + 1, 1,
            SourceSymbolKind.Function)).ToArray();
        using var progressive = new SourceSymbolIndex();
        using var final = new SourceSymbolIndex();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < symbols.Length; i += 4096)
            progressive.AppendBatch(new ArraySegment<SourceSymbolLocation>(symbols, i, Math.Min(4096, symbols.Length - i)));
        var build = watch.Elapsed.TotalMilliseconds;
        Check(progressive.Count == symbols.Length && progressive.Find("SetMovementMode0").Count == 10,
            "300,000개 부분 인덱스 누락·중복 없음");
        final.ReplaceAll(symbols);
        var timings = new double[20];
        for (var i = 0; i < timings.Length; i++)
        {
            var query = i % 2 == 0 ? "Set Mode" : "SetMovementMode12";
            var expected = final.Search(query, 100);
            watch.Restart();
            var actual = progressive.Search(query, 100);
            timings[i] = watch.Elapsed.TotalMilliseconds;
            Check(actual.Select(x => x.Location).SequenceEqual(expected.Select(x => x.Location)),
                "부분·최종 인덱스 검색 순위 동일");
        }
        Array.Sort(timings);
        Console.WriteLine($"INFO: 독립 300,000개 심볼 부분 인덱스 구축 {build:F1}ms / 검색 p95 {timings[18]:F1}ms");
        using var token = new CancellationTokenSource();
        token.Cancel();
        try { progressive.AppendBatch(symbols.Take(1), token.Token); }
        catch (OperationCanceledException) { }
        Check(progressive.Count == symbols.Length, "취소된 부분 인덱스 공개 차단");
        progressive.ReplaceAll(Array.Empty<SourceSymbolLocation>());
        Check(progressive.Count == 0 && progressive.Search("Set Mode").Count == 0, "솔루션 교체 시 부분 인덱스 제거");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
