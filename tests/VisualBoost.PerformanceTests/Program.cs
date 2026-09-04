using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Core.Indexing;

namespace VisualBoost.PerformanceTests;

internal static class Program
{
    private static readonly int[] ScaleTargets = { 100_000, 500_000 };
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".cs", ".ixx", ".inl",
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            var sourceRoot = Path.GetFullPath(options.SourceRoot);
            var outputPath = Path.GetFullPath(options.OutputPath);
            EnsureOutputIsOutsideSource(sourceRoot, outputPath);

            var enumerationWatch = Stopwatch.StartNew();
            var actualPaths = FileSystemPathCatalog.GetFiles(new[] { sourceRoot });
            enumerationWatch.Stop();

            var queries = SelectRepresentativeQueries(actualPaths);
            var scenarios = new List<ScenarioResult>
            {
                await RunScenarioAsync(
                    "actual",
                    actualPaths,
                    Math.Max(options.Iterations, 1),
                    queries,
                    measureCancellation: false),
            };

            foreach (var target in ScaleTargets)
            {
                var scaledPaths = CreateScaledPaths(sourceRoot, actualPaths, target);
                scenarios.Add(await RunScenarioAsync(
                    $"scaled-{target}",
                    scaledPaths,
                    Math.Max(3, options.Iterations / (target >= 500_000 ? 10 : 5)),
                    queries,
                    measureCancellation: target == ScaleTargets[^1]));
            }

            var report = new PerformanceReport(
                DateTimeOffset.Now,
                sourceRoot,
                Environment.OSVersion.ToString(),
                Environment.Version.ToString(),
                Environment.ProcessorCount,
                enumerationWatch.Elapsed.TotalMilliseconds,
                actualPaths.Count,
                GetTotalBytes(actualPaths),
                GetExtensionCounts(actualPaths),
                queries,
                scenarios);

            var outputDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidOperationException("출력 디렉터리를 확인할 수 없습니다.");
            Directory.CreateDirectory(outputDirectory);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(outputPath, json, new UTF8Encoding(false));

            var markdownPath = Path.ChangeExtension(outputPath, ".md");
            await File.WriteAllTextAsync(markdownPath, CreateMarkdown(report), new UTF8Encoding(false));

            Console.WriteLine($"실제 파일: {report.ActualFileCount:N0}개");
            Console.WriteLine($"열거 시간: {report.EnumerationMilliseconds:N2} ms");
            foreach (var scenario in scenarios)
            {
                Console.WriteLine(
                    $"{scenario.Name}: {scenario.PathCount:N0}개, 인덱스 {scenario.IndexBuildMilliseconds:N2} ms, " +
                    $"검색 p95 {scenario.Searches.Max(search => search.P95Milliseconds):N2} ms");
            }

            Console.WriteLine($"보고서: {outputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<ScenarioResult> RunScenarioAsync(
        string name,
        IReadOnlyList<string> paths,
        int iterations,
        IReadOnlyList<string> queries,
        bool measureCancellation)
    {
        ForceCollection();
        var memoryBefore = GC.GetTotalMemory(true);
        using var index = new FilePathIndex();
        var indexWatch = Stopwatch.StartNew();
        index.ReplaceAll(paths);
        indexWatch.Stop();
        var memoryAfter = GC.GetTotalMemory(true);

        if (queries.Count > 0)
        {
            _ = index.Search(queries[0], 100);
        }

        var searchResults = new List<SearchResult>();
        foreach (var query in queries)
        {
            var samples = new double[iterations];
            var resultCount = 0;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var watch = Stopwatch.StartNew();
                var matches = index.Search(query, 100);
                watch.Stop();
                samples[iteration] = watch.Elapsed.TotalMilliseconds;
                resultCount = matches.Count;
            }

            Array.Sort(samples);
            searchResults.Add(new SearchResult(
                query,
                iterations,
                resultCount,
                Percentile(samples, 0.50),
                Percentile(samples, 0.95),
                samples[^1]));
        }

        CancellationResult? cancellation = null;
        if (measureCancellation)
        {
            cancellation = await MeasureCancellationAsync(index, queries.FirstOrDefault() ?? "source");
        }

        return new ScenarioResult(
            name,
            paths.Count,
            indexWatch.Elapsed.TotalMilliseconds,
            Math.Max(0, memoryAfter - memoryBefore),
            searchResults,
            cancellation);
    }

    private static async Task<CancellationResult> MeasureCancellationAsync(
        FilePathIndex index,
        string query)
    {
        using var cancellation = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var searchTask = Task.Run(() => index.Search(query, 100, cancellation.Token));
        await Task.Delay(10);
        var requestMilliseconds = watch.Elapsed.TotalMilliseconds;
        cancellation.Cancel();

        try
        {
            await searchTask;
            watch.Stop();
            return new CancellationResult(false, requestMilliseconds, 0, "취소 요청 전에 검색이 완료되었습니다.");
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return new CancellationResult(
                true,
                requestMilliseconds,
                Math.Max(0, watch.Elapsed.TotalMilliseconds - requestMilliseconds),
                "검색 루프가 취소 요청을 확인했습니다.");
        }
    }

    private static IReadOnlyList<string> CreateScaledPaths(
        string sourceRoot,
        IReadOnlyList<string> actualPaths,
        int targetCount)
    {
        if (actualPaths.Count == 0)
        {
            throw new InvalidOperationException("확장 시나리오를 만들 실제 파일 경로가 없습니다.");
        }

        var paths = new string[targetCount];
        var virtualRoot = Path.Combine(Path.GetTempPath(), "VisualBoostPerformanceVirtual");
        for (var index = 0; index < targetCount; index++)
        {
            var actualPath = actualPaths[index % actualPaths.Count];
            var relativePath = Path.GetRelativePath(sourceRoot, actualPath);
            paths[index] = Path.Combine(
                virtualRoot,
                $"batch-{index / actualPaths.Count:D6}",
                relativePath);
        }

        return paths;
    }

    private static IReadOnlyList<string> SelectRepresentativeQueries(IReadOnlyList<string> paths)
    {
        var sourcePaths = paths
            .Where(path => SourceExtensions.Contains(Path.GetExtension(path)))
            .ToArray();
        var queryPaths = sourcePaths.Length > 0 ? sourcePaths : paths;
        var stems = queryPaths
            .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
            .Where(stem => !string.IsNullOrWhiteSpace(stem) && stem.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(stem => stem, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (stems.Length == 0)
        {
            return new[] { "source" };
        }

        var queries = new List<string>();
        for (var index = 0; index < Math.Min(6, stems.Length); index++)
        {
            var position = stems.Length == 1
                ? 0
                : (int)Math.Round(index * (stems.Length - 1d) / Math.Min(5, stems.Length - 1));
            var stem = stems[position];
            if (!queries.Contains(stem, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(stem);
            }
        }

        return queries;
    }

    private static IReadOnlyDictionary<string, int> GetExtensionCounts(IReadOnlyList<string> paths) =>
        paths
            .GroupBy(path => Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => string.IsNullOrEmpty(group.Key) ? "(none)" : group.Key,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);

    private static long GetTotalBytes(IReadOnlyList<string> paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            try
            {
                total += new FileInfo(path).Length;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is NotSupportedException)
            {
                // 성능 측정 중 사라지거나 접근할 수 없는 파일의 크기는 합계에서 제외합니다.
            }
        }

        return total;
    }

    private static double Percentile(IReadOnlyList<double> sortedSamples, double percentile)
    {
        var index = Math.Max(0, (int)Math.Ceiling(sortedSamples.Count * percentile) - 1);
        return sortedSamples[index];
    }

    private static void EnsureOutputIsOutsideSource(string sourceRoot, string outputPath)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, outputPath);
        if (!relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("보고서는 검증 대상 프로젝트 밖에 저장해야 합니다.");
        }
    }

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string CreateMarkdown(PerformanceReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# VisualBoost 파일 탐색 성능 검증");
        builder.AppendLine();
        builder.AppendLine($"- 측정 시각: `{report.MeasuredAt:O}`");
        builder.AppendLine($"- 입력 경로: `{report.SourceRoot}`");
        builder.AppendLine($"- 실제 탐색 파일: `{report.ActualFileCount:N0}`개");
        builder.AppendLine($"- 파일 열거: `{report.EnumerationMilliseconds:N2} ms`");
        builder.AppendLine();
        builder.AppendLine("| 시나리오 | 경로 수 | 인덱스 생성 | 메모리 증가 | 검색 p95 최댓값 | 취소 응답 |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var scenario in report.Scenarios)
        {
            var searchP95 = scenario.Searches.Count == 0
                ? 0
                : scenario.Searches.Max(search => search.P95Milliseconds);
            var cancellation = scenario.Cancellation is null
                ? "-"
                : scenario.Cancellation.Observed
                    ? $"{scenario.Cancellation.LatencyMilliseconds:N2} ms"
                    : "검색 선완료";
            builder.AppendLine(
                $"| {scenario.Name} | {scenario.PathCount:N0} | {scenario.IndexBuildMilliseconds:N2} ms | " +
                $"{scenario.ManagedMemoryIncreaseBytes / 1024d / 1024d:N2} MiB | {searchP95:N2} ms | {cancellation} |");
        }

        builder.AppendLine();
        builder.AppendLine("> 확장 시나리오는 실제 파일명과 상대 경로를 메모리에서 반복해 구성하며 파일을 생성하지 않습니다.");
        return builder.ToString();
    }

    private sealed record PerformanceReport(
        DateTimeOffset MeasuredAt,
        string SourceRoot,
        string OperatingSystem,
        string RuntimeVersion,
        int ProcessorCount,
        double EnumerationMilliseconds,
        int ActualFileCount,
        long ActualTotalBytes,
        IReadOnlyDictionary<string, int> ExtensionCounts,
        IReadOnlyList<string> Queries,
        IReadOnlyList<ScenarioResult> Scenarios);

    private sealed record ScenarioResult(
        string Name,
        int PathCount,
        double IndexBuildMilliseconds,
        long ManagedMemoryIncreaseBytes,
        IReadOnlyList<SearchResult> Searches,
        CancellationResult? Cancellation);

    private sealed record SearchResult(
        string Query,
        int Iterations,
        int ResultCount,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds);

    private sealed record CancellationResult(
        bool Observed,
        double RequestedAtMilliseconds,
        double LatencyMilliseconds,
        string Note);

    private sealed record Options(string SourceRoot, string OutputPath, int Iterations)
    {
        public static Options Parse(IReadOnlyList<string> args)
        {
            string? sourceRoot = null;
            string? outputPath = null;
            var iterations = 20;
            for (var index = 0; index < args.Count; index++)
            {
                switch (args[index])
                {
                    case "--source" when index + 1 < args.Count:
                        sourceRoot = args[++index];
                        break;
                    case "--output" when index + 1 < args.Count:
                        outputPath = args[++index];
                        break;
                    case "--iterations" when index + 1 < args.Count && int.TryParse(args[++index], out var value):
                        iterations = value;
                        break;
                    default:
                        throw new ArgumentException($"알 수 없거나 값이 빠진 인수입니다: {args[index]}");
                }
            }

            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                throw new ArgumentException("존재하는 검증 대상 경로를 --source로 지정해야 합니다.");
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("검증 대상 밖의 보고서 경로를 --output으로 지정해야 합니다.");
            }

            return new Options(sourceRoot, outputPath, iterations);
        }
    }
}
