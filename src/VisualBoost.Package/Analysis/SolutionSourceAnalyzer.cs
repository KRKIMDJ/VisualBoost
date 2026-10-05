using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
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

    private readonly SourceAnalysisCache cache;
    private readonly SourceSymbolIndex symbols = new();
    private readonly object gate = new();
    private IReadOnlyDictionary<string, string[]> includeGraph =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    private int includeEdgeCount;
    private string? cachedSolution;
    private IReadOnlyDictionary<string, CachedSourceAnalysis>? loadedCache;
    private string? discoverySolution;
    private readonly HashSet<string> discoveredCacheFiles = new(StringComparer.OrdinalIgnoreCase);
    // 진행 중인 분석 패스의 대기열입니다. 사용자가 연 파일을 앞으로 옮길 때만 다른 스레드에서 읽습니다.
    private volatile SourceAnalysisQueue? activeQueue;

    internal void PrepareCachedDiscovery(string solutionPath, CancellationToken token)
    {
        LoadPrevious(solutionPath, token);
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            discoverySolution = solutionPath;
            discoveredCacheFiles.Clear();
        }
    }

    internal void PublishCachedDiscovery(string solutionPath, IReadOnlyList<string> files, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (discoverySolution != solutionPath || loadedCache is null) return;
            var batch = new List<SourceSymbolLocation>();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (discoveredCacheFiles.Add(file) && loadedCache.TryGetValue(file, out var entry)) batch.AddRange(entry.Analysis.Symbols);
            }
            if (batch.Count > 0) symbols.AppendBatch(batch, token);
        }
    }

    internal SolutionSourceAnalyzer(SourceAnalysisCache? cache = null)
    {
        this.cache = cache ?? new SourceAnalysisCache();
    }

    public int SymbolCount => symbols.Count;
    public string? LastWarning { get; private set; }
    public SymbolCompletionSnapshot CompletionSnapshot => symbols.CompletionSnapshot;
    internal event Action<int>? SymbolsPublished;

    public int IncludeEdgeCount
    {
        get
        {
            lock (gate) return includeEdgeCount;
        }
    }

    /// <summary>진행 중인 분석에서 이 파일과 같은 프로젝트의 파일을 먼저 분석하게 합니다. 분석 중이 아니면 아무것도 하지 않습니다.</summary>
    public void Focus(string path) => activeQueue?.Focus(path);

    /// <param name="priority">프로젝트 소속·엔진 위치로 정하는 기본 분석 순서입니다.</param>
    /// <param name="focus">먼저 분석할 파일(열린 문서, 최근에 연 순서)입니다.</param>
    public IReadOnlyList<string> Analyze(
        string solutionPath,
        IReadOnlyList<string> files,
        IReadOnlyList<string> includeRoots,
        CancellationToken cancellationToken,
        Action<SourceAnalysisProgress>? reportProgress = null,
        SourceAnalysisPriority? priority = null,
        IReadOnlyList<string>? focus = null)
    {
        LastWarning = null;
        cancellationToken.ThrowIfCancellationRequested();
        var sourceFiles = files.Where(IsCppFile).ToArray();
        // 열린 파일 → 같은 프로젝트 → 다른 프로젝트 → 보충 파일 → 엔진 순서로 분석하고, 도중에 연 파일은 앞으로 옮깁니다.
        // 캐시를 읽는 동안 연 파일도 반영되게 대기열을 먼저 공개합니다.
        var queue = new SourceAnalysisQueue(sourceFiles, priority ?? SourceAnalysisPriority.None, focus);
        activeQueue = queue;
        IReadOnlyDictionary<string, CachedSourceAnalysis> previous;
        try
        {
            previous = LoadPrevious(solutionPath, cancellationToken);
        }
        catch
        {
            // 분석을 시작하지 못하면 대기열을 거둬, 이후 Focus가 끝난 패스를 붙잡지 않게 합니다.
            if (ReferenceEquals(activeQueue, queue)) activeQueue = null;
            throw;
        }

        var progressGate = new object();
        var completedFiles = 0;
        var activeFiles = new Dictionary<string, SourceAnalysisStage>(StringComparer.OrdinalIgnoreCase);
        void Report(SourceAnalysisStage stage, string? path = null, bool completed = false)
        {
            lock (progressGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completed)
                {
                    completedFiles++;
                    if (path is not null) activeFiles.Remove(path);
                    path = null;
                }
                else if (path is not null) activeFiles[path] = stage;
                // 병렬 작업 중 다른 파일의 캐시 확인이 실제 파싱 파일 표시를 가리지 않게 합니다.
                var parsing = activeFiles.FirstOrDefault(p => p.Value == SourceAnalysisStage.Parsing);
                if (parsing.Key is not null) { stage = parsing.Value; path = parsing.Key; }
                reportProgress?.Invoke(new SourceAnalysisProgress(stage, completedFiles, sourceFiles.Length, path));
            }
        }
        if (previous.Count == 0) symbols.ReplaceAll(Array.Empty<SourceSymbolLocation>(), cancellationToken);
        var current = new ConcurrentDictionary<string, CachedSourceAnalysis>(StringComparer.OrdinalIgnoreCase);
        var timedOutFiles = 0;
        var publicationGate = new object();
        var pendingSymbols = new List<SourceSymbolLocation>();
        var pendingFiles = 0;
        var pendingBestRank = SourceAnalysisRank.Engine;
        // 최초 분석은 고정 크기 묶음으로 공개하고 검색 인덱스가 묶음을 계층적으로 병합합니다.
        // 사용자가 연 파일과 그 프로젝트는 작은 묶음으로 공개해 검색에 빨리 나타나게 합니다.
        void PublishProgress(CachedSourceAnalysis entry, SourceAnalysisRank rank)
        {
            // 재방문 시 이미 공개한 전체 캐시를 더 작은 부분 결과로 퇴행시키지 않습니다.
            if (previous.Count > 0) return;
            lock (publicationGate)
            {
                pendingSymbols.AddRange(entry.Analysis.Symbols);
                // 묶음 크기는 묶음에 든 가장 앞선 등급으로 정해, 연 파일이 뒤 등급 파일 128개를 기다리지 않게 합니다.
                // 관련 파일은 최대 수천 개이므로 연 파일보다 큰 묶음으로 병합 횟수를 줄입니다.
                if (rank < pendingBestRank) pendingBestRank = rank;
                var threshold = pendingBestRank switch
                {
                    SourceAnalysisRank.Focus => 8,
                    SourceAnalysisRank.Related => 64,
                    _ => 128,
                };
                if (++pendingFiles < threshold) return;
                symbols.AppendBatch(pendingSymbols, cancellationToken);
                pendingSymbols.Clear();
                pendingFiles = 0;
                pendingBestRank = SourceAnalysisRank.Engine;
                SymbolsPublished?.Invoke(current.Count);
            }
        }
        // 편집기 응답성을 우선하고 남는 처리량만 초기 분석에 사용합니다.
        var workers = Math.Min(2, Math.Max(1, Environment.ProcessorCount - 1));
        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = workers };
        try
        {
            // 작업자 하나가 예외로 끝나면 다른 작업자도 남은 대기열(엔진 포함)을 계속 비우지 않고 멈춥니다.
            Parallel.For(0, workers, parallelOptions, (_, loop) =>
            {
                while (!loop.ShouldExitCurrentIteration && queue.TryTake(out var file, out var rank)) AnalyzeFile(file, rank);
            });
        }
        finally
        {
            if (ReferenceEquals(activeQueue, queue)) activeQueue = null;
        }

        void AnalyzeFile(string file, SourceAnalysisRank rank)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(SourceAnalysisStage.CacheChecking, file);
            try
            {
                var info = TryGetInfo(file);
                if (info is null || info.Length > MaximumSourceLength) return;

                // 표시 종류가 아니라 저장된 분석 버전과 파일 변경 여부로 재사용을 판정합니다.
                if (previous.TryGetValue(file, out var cached) &&
                    cached.Length == info.Length && cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
                    cached.Revision == CachedSourceAnalysis.CurrentRevision)
                {
                    current[file] = cached;
                    PublishProgress(cached, rank);
                    return;
                }

                Report(SourceAnalysisStage.Parsing, file);
                var analysis = CppSourceAnalyzer.Analyze(file, File.ReadAllText(file), cancellationToken);
                var after = TryGetInfo(file);
                if (after is null || after.Length != info.Length || after.LastWriteTimeUtc != info.LastWriteTimeUtc) return;
                current[file] = new CachedSourceAnalysis(info.Length, info.LastWriteTimeUtc.Ticks, analysis);
                PublishProgress(current[file], rank);
            }
            catch (RegexMatchTimeoutException)
            {
                // 비정상 구문 하나가 전체 탐색을 막지 않게 격리하고, 불완전한 결과는 캐시하지 않습니다.
                Interlocked.Increment(ref timedOutFiles);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // 잠겼거나 사라진 파일은 다음 증분 분석에서 다시 시도합니다.
            }
            finally { if (!cancellationToken.IsCancellationRequested) Report(SourceAnalysisStage.CacheChecking, file, completed: true); }
        }

        cancellationToken.ThrowIfCancellationRequested();
        LastWarning = timedOutFiles == 0 ? null : $"복잡한 구문으로 {timedOutFiles:N0}개 파일 분석을 건너뛰었습니다. 일부 심볼이 누락될 수 있습니다.";
        Report(SourceAnalysisStage.Indexing);
        // 검색에 필요하지 않은 include 경로 확인 및 캐시 저장이 완료되기 전에 심볼을 공개합니다.
        symbols.ReplaceAll(current.Values.SelectMany(entry => entry.Analysis.Symbols), cancellationToken);
        // include 후처리 도중 종료되어도 이미 완료한 소스 분석을 다음 실행에서 다시 파싱하지 않습니다.
        if (cache.NeedsUpgrade || current.Count != previous.Count || current.Any(pair =>
            !previous.TryGetValue(pair.Key, out var old) || !ReferenceEquals(old, pair.Value)))
        {
            Report(SourceAnalysisStage.Saving);
            cache.Save(solutionPath, current, cancellationToken);
        }
        cachedSolution = solutionPath;
        loadedCache = current;
        var knownFiles = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var filesByName = knownFiles
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var graph = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var externalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 공통 roots는 한 패스 안에서 불변입니다. 로컬 상대 include는 먼저 확인하고,
        // 그 밖의 같은 경로 검색은 실패도 재사용해 반복 디스크 조회를 줄입니다.
        var sharedIncludes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var rootLookup = new IncludeRootLookup(includeRoots);
        var linkedFiles = 0;
        var invalidIncludes = 0;
        string? ResolveSafe(string path, SourceIncludeReference include)
        {
            try { return ResolveInclude(path, include, rootLookup, filesByName, sharedIncludes, cancellationToken); }
            catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
            {
                // 구문 분석 중 추출된 include가 운영체제 경로가 아닐 수 있습니다. 해당 항목만 건너뛰고 진단을 남깁니다.
                invalidIncludes++;
                return null;
            }
        }
        foreach (var entry in current.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportProgress?.Invoke(new SourceAnalysisProgress(SourceAnalysisStage.Linking, linkedFiles, current.Count, entry.Analysis.Path));
            var resolved = entry.Analysis.Includes
                .Select(include => ResolveSafe(entry.Analysis.Path, include))
                .Where(path => path is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            graph[entry.Analysis.Path] = resolved;
            foreach (var path in resolved)
            {
                if (!knownFiles.Contains(path)) externalFiles.Add(path);
            }
            linkedFiles++;
        }

        if (invalidIncludes > 0)
            LastWarning = (LastWarning is null ? string.Empty : LastWarning + " ") +
                $"파일 경로로 해석할 수 없는 include {invalidIncludes:N0}개를 건너뛰었습니다. 일부 연결이 누락될 수 있습니다.";

        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            includeGraph = graph;
            includeEdgeCount = graph.Values.Sum(paths => paths.Length);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return externalFiles.ToArray();
    }

    public void LoadCachedSymbols(string solutionPath, CancellationToken cancellationToken, IReadOnlyList<string>? allowedFiles = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = LoadPrevious(solutionPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = allowedFiles is null ? null : new HashSet<string>(allowedFiles, StringComparer.OrdinalIgnoreCase);
        symbols.ReplaceAll(cached.Where(entry => allowed is null || allowed.Contains(entry.Key))
            .SelectMany(entry => entry.Value.Analysis.Symbols), cancellationToken);
    }

    public IReadOnlyList<SourceSymbolLocation> FindSymbol(string name) => symbols.Find(name);

    public IReadOnlyList<SourceSymbolMatch> SearchSymbols(
        string query,
        int maximumResults,
        CancellationToken cancellationToken, Func<string, bool>? includes = null) =>
        symbols.Search(query, maximumResults, cancellationToken, includes);

    public void Clear()
    {
        lock (gate)
        {
            discoverySolution = null;
            discoveredCacheFiles.Clear();
            symbols.ReplaceAll(Array.Empty<SourceSymbolLocation>());
            includeGraph = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            includeEdgeCount = 0;
        }
    }

    public void Dispose() => symbols.Dispose();

    private IReadOnlyDictionary<string, CachedSourceAnalysis> LoadPrevious(string solutionPath, CancellationToken cancellationToken)
    {
        // 동일 분석 패스의 선공개와 본 분석에서 큰 캐시 파일을 두 번 역직렬화하지 않습니다.
        if (loadedCache is not null && string.Equals(cachedSolution, solutionPath, StringComparison.OrdinalIgnoreCase))
            return loadedCache;
        loadedCache = cache.Load(solutionPath, cancellationToken);
        cachedSolution = solutionPath;
        return loadedCache;
    }

    private static string? ResolveInclude(
        string sourcePath,
        SourceIncludeReference include,
        IncludeRootLookup rootLookup,
        IReadOnlyDictionary<string, string[]> filesByName,
        Dictionary<string, string?> sharedIncludes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relative = include.Value.Replace('/', Path.DirectorySeparatorChar);
        if (!include.IsSystem)
        {
            var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, relative));
            if (File.Exists(local)) return local;
        }

        if (sharedIncludes.TryGetValue(relative, out var shared)) return shared;

        var name = Path.GetFileName(relative);
        if (filesByName.TryGetValue(name, out var candidates))
        {
            var suffix = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var match = candidates
                .Where(path => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.Length)
                .FirstOrDefault();
            if (match is not null) { sharedIncludes[relative] = match; return match; }
        }

        var resolved = rootLookup.Find(relative, cancellationToken);
        sharedIncludes[relative] = resolved;
        return resolved;
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
