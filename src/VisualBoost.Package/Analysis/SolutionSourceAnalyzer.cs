using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Searching;

namespace VisualBoost.Analysis;

internal sealed class SolutionSourceAnalyzer : IDisposable
{
    private const long MaximumSourceLength = 8 * 1024 * 1024;
    private static readonly HashSet<string> CppExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".inl", ".ixx", ".cppm",
    };

    private readonly SourceAnalysisCache cache = new();
    private readonly SourceSymbolIndex symbols = new();
    private readonly object gate = new();
    private IReadOnlyDictionary<string, string[]> includeGraph =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    private int includeEdgeCount;

    public int SymbolCount => symbols.Count;

    public int IncludeEdgeCount
    {
        get
        {
            lock (gate) return includeEdgeCount;
        }
    }

    public IReadOnlyList<string> Analyze(
        string solutionPath,
        IReadOnlyList<string> files,
        IReadOnlyList<string> includeRoots,
        CancellationToken cancellationToken)
    {
        var previous = cache.Load(solutionPath);
        var current = new ConcurrentDictionary<string, CachedSourceAnalysis>(StringComparer.OrdinalIgnoreCase);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            // 편집기 응답성을 우선하고 남는 처리량만 초기 분석에 사용합니다.
            MaxDegreeOfParallelism = Math.Min(2, Math.Max(1, Environment.ProcessorCount - 1)),
        };
        Parallel.ForEach(files.Where(IsCppFile), parallelOptions, file =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = TryGetInfo(file);
            if (info is null || info.Length > MaximumSourceLength) return;

            if (previous.TryGetValue(file, out var cached) &&
                cached.Length == info.Length && cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
            {
                current[file] = cached;
                return;
            }

            try
            {
                var analysis = CppSourceAnalyzer.Analyze(file, File.ReadAllText(file));
                current[file] = new CachedSourceAnalysis(info.Length, info.LastWriteTimeUtc.Ticks, analysis);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // 잠겼거나 사라진 파일은 다음 증분 분석에서 다시 시도합니다.
            }
        });

        var knownFiles = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var filesByName = knownFiles
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var graph = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var externalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in current.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = entry.Analysis.Includes
                .Select(include => ResolveInclude(entry.Analysis.Path, include, includeRoots, filesByName))
                .Where(path => path is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            graph[entry.Analysis.Path] = resolved;
            foreach (var path in resolved)
            {
                if (!knownFiles.Contains(path)) externalFiles.Add(path);
            }
        }

        symbols.ReplaceAll(current.Values.SelectMany(entry => entry.Analysis.Symbols));
        lock (gate)
        {
            includeGraph = graph;
            includeEdgeCount = graph.Values.Sum(paths => paths.Length);
        }
        cache.Save(solutionPath, current);
        return externalFiles.ToArray();
    }

    public void LoadCachedSymbols(string solutionPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = cache.Load(solutionPath);
        cancellationToken.ThrowIfCancellationRequested();
        symbols.ReplaceAll(cached.Values.SelectMany(entry => entry.Analysis.Symbols));
    }

    public IReadOnlyList<SourceSymbolLocation> FindSymbol(string name) => symbols.Find(name);

    public IReadOnlyList<SourceSymbolMatch> SearchSymbols(
        string query,
        int maximumResults,
        CancellationToken cancellationToken) =>
        symbols.Search(query, maximumResults, cancellationToken);

    public void Clear()
    {
        symbols.ReplaceAll(Array.Empty<SourceSymbolLocation>());
        lock (gate)
        {
            includeGraph = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            includeEdgeCount = 0;
        }
    }

    public void Dispose() => symbols.Dispose();

    private static string? ResolveInclude(
        string sourcePath,
        SourceIncludeReference include,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, string[]> filesByName)
    {
        var relative = include.Value.Replace('/', Path.DirectorySeparatorChar);
        if (!include.IsSystem)
        {
            var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, relative));
            if (File.Exists(local)) return local;
        }

        var name = Path.GetFileName(relative);
        if (filesByName.TryGetValue(name, out var candidates))
        {
            var suffix = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var match = candidates
                .Where(path => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.Length)
                .FirstOrDefault();
            if (match is not null) return match;
        }

        foreach (var root in roots)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, relative));
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static bool IsCppFile(string path) => CppExtensions.Contains(Path.GetExtension(path));

    private static FileInfo? TryGetInfo(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path) : null; }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return null;
        }
    }
}
