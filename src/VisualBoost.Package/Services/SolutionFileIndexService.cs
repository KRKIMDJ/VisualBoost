using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Analysis;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Indexing;
using VisualBoost.Core.Searching;

namespace VisualBoost.Services;

internal sealed class SolutionFileIndexService : IDisposable
{
    private readonly object gate = new();
    private readonly FilePathIndex index = new();
    private readonly FileIndexCache cache;
    private readonly SolutionSourceAnalyzer sourceAnalyzer;
    private readonly SemaphoreSlim workerGate = new(1, 1);
    private Timer? refreshTimer;
    private int refreshEpoch;
    // 저장·생성·삭제된 파일입니다. 묶어서 그 파일만 다시 분석합니다(UpdateSourcesAsync). 수집·분석 패스 중이면 패스가 끝날 때까지 모읍니다.
    private readonly HashSet<string> changedFiles = new(StringComparer.OrdinalIgnoreCase);
    // 폴더가 생겨 파일 열거부터 다시 해야 합니다.
    private bool fullRefreshPending;
    private Task outstandingWork = Task.CompletedTask;
    private long generation;
    private CancellationTokenSource rebuildCancellation = new();
    // BeginDiscovery의 저장된 분석 복원입니다. 수집을 마친 같은 Solution의 Start는 이것을 취소하지 않습니다. 취소하면 거의 다 만든 이름
    // 인덱스를 버리고 BuildIndex가 처음부터 다시 만듭니다(엔진 규모 다시 열기에서 전체 이름 검색이 약 6초 늦어짐).
    private CancellationTokenSource restoreCancellation = new();
    private IReadOnlyList<string> roots = Array.Empty<string>();
    private IReadOnlyList<string> explicitFiles = Array.Empty<string>();
    private IReadOnlyList<string> requestedRoots = Array.Empty<string>();
    private IReadOnlyList<string> requestedFiles = Array.Empty<string>();
    private string solutionPath = string.Empty;
    private IReadOnlyList<FileSystemWatcher> watchers = Array.Empty<FileSystemWatcher>();
    private Task activeBuild = Task.CompletedTask;
    private Task activeAnalysis = Task.CompletedTask;
    private SolutionFileIndexState state = SolutionFileIndexState.Empty;
    private TimeSpan lastBuildDuration;
    private string? lastError;
    private bool isAnalyzing;
    // 저장된 파일 목록이 있는 Solution을 다시 수집하는 중입니다. 상태 표시줄은 이때 수집·검증 단계를 띄우지 않습니다.
    private bool refreshing;
    private string? analysisError;
    private SourceAnalysisProgress? analysisProgress;
    private readonly LinkedList<string> recentFiles = new();
    private readonly HashSet<string> recentFileSet = new(StringComparer.OrdinalIgnoreCase);
    private SolutionFileIndexConfiguration configuration = SolutionFileIndexConfiguration.Default;
    private bool disposed;
    private IReadOnlyList<SymbolSearchScope> symbolScopes = new[] { SymbolSearchScope.All };
    private SourceAnalysisPriority analysisPriority = SourceAnalysisPriority.None;
    public IReadOnlyList<SymbolSearchScope> SymbolScopes { get { lock (gate) return symbolScopes; } }

    internal SolutionFileIndexService(SolutionSourceAnalyzer? sourceAnalyzer = null, FileIndexCache? cache = null)
    {
        this.sourceAnalyzer = sourceAnalyzer ?? new SolutionSourceAnalyzer();
        this.cache = cache ?? new FileIndexCache();
    }

    public int Count => index.Count;
    public SymbolCompletionSnapshot CompletionSnapshot => sourceAnalyzer.CompletionSnapshot;

    /// <summary>
    /// 이름 인덱스가 바뀔 때마다(전체 교체·분석 중 추가 묶음) 늘어나는 번호입니다. 수가 같은 재분석도 구별하며 잠금 없이 읽으므로
    /// UI thread의 표시용 캐시가 <see cref="GetSnapshot"/> 대신 바뀜 여부만 볼 때 씁니다.
    /// </summary>
    public int SymbolRevision => sourceAnalyzer.SymbolRevision;
    public long Generation => Interlocked.Read(ref generation);
    public void BeginDiscovery(string path)
    {
        bool stored;
        lock (gate) stored = configuration.UsePersistentFileCache;
        // 잠금 밖에서 디스크를 봅니다. 수집 결과를 저장한 적이 있으면 다시 여는 Solution입니다.
        stored = stored && cache.Exists(path);
        lock (gate)
        {
            ThrowIfDisposed();
            Interlocked.Increment(ref generation);
            CancelBuildNoLock();
            rebuildCancellation = new CancellationTokenSource();
            CancelRestoreNoLock();
            DisposeWatchersNoLock();
            refreshTimer?.Dispose();
            refreshTimer = null;
            refreshEpoch++;
            changedFiles.Clear();
            fullRefreshPending = false;
            requestedRoots = Array.Empty<string>();
            requestedFiles = Array.Empty<string>();
            roots = Array.Empty<string>();
            explicitFiles = Array.Empty<string>();
            solutionPath = path;
            refreshing = stored;
            symbolScopes = new[] { SymbolSearchScope.All };
            analysisPriority = SourceAnalysisPriority.None;
            index.Clear();
            sourceAnalyzer.Clear();
            state = SolutionFileIndexState.Building;
            analysisProgress = null;
            lastError = analysisError = null;
            isAnalyzing = configuration.EnableSourceAnalysis;
            if (configuration.EnableSourceAnalysis)
            {
                var token = restoreCancellation.Token;
                var restore = Task.Run(async () =>
                {
                    var entered = false;
                    try
                    {
                        await workerGate.WaitAsync(token).ConfigureAwait(false); entered = true;
                        // 저장된 분석 전체를 수집 결과를 기다리지 않고 한 번에 공개합니다. 수집이 끝나면 BuildIndex가 목록 밖 파일만 뺍니다.
                        sourceAnalyzer.LoadCachedSymbols(path, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException)
                    {
                        lock (gate) if (!disposed && !token.IsCancellationRequested) analysisError = exception.Message;
                    }
                    finally { if (entered) workerGate.Release(); }
                }, token);
                outstandingWork = Task.WhenAll(outstandingWork, restore);
            }
        }
    }

    public void PublishDiscoveredFiles(IReadOnlyList<string> files, CancellationToken token)
    {
        // 디스크 확인은 호출자가 백그라운드에서 수행하며, 수집 취소와 공개를 같은 잠금으로 보호합니다.
        var existing = files.Where(File.Exists).ToArray();
        lock (gate)
        {
            if (disposed || token.IsCancellationRequested) return;
            foreach (var file in existing) index.Add(file);
        }
    }

    public void FailDiscovery(string message, CancellationToken token)
    {
        lock (gate)
        {
            if (disposed || token.IsCancellationRequested) return;
            state = SolutionFileIndexState.Faulted;
            lastError = message;
            isAnalyzing = false;
            analysisProgress = null;
        }
    }

    public void Configure(SolutionFileIndexConfiguration value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        lock (gate)
        {
            ThrowIfDisposed();
            configuration = value;
        }
    }

    public void Start(SolutionIndexDiscoveryResult discovery, bool force = false)
    {
        if (discovery is null)
        {
            throw new ArgumentNullException(nameof(discovery));
        }

        var candidateRoots = discovery.SearchRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidateFiles = discovery.ExplicitFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (gate)
        {
            ThrowIfDisposed();
            if (!force && string.Equals(solutionPath, discovery.SolutionPath, StringComparison.OrdinalIgnoreCase) &&
                requestedRoots.SequenceEqual(candidateRoots, StringComparer.OrdinalIgnoreCase) &&
                requestedFiles.SequenceEqual(candidateFiles, StringComparer.OrdinalIgnoreCase) &&
                state == SolutionFileIndexState.Ready)
            {
                if (discovery.SymbolScopes is not null) symbolScopes = discovery.SymbolScopes;
                if (discovery.AnalysisPriority is not null) analysisPriority = discovery.AnalysisPriority;
                return;
            }

            requestedRoots = candidateRoots;
            // 새 패스가 디스크에서 다시 읽으므로 그 전에 모은 변경은 따로 처리하지 않습니다.
            changedFiles.Clear();
            fullRefreshPending = false;
            Interlocked.Increment(ref generation);
            requestedFiles = candidateFiles;
            if (!string.Equals(solutionPath, discovery.SolutionPath, StringComparison.OrdinalIgnoreCase))
            {
                CancelRestoreNoLock();
                index.Clear();
                sourceAnalyzer.Clear();
                symbolScopes = new[] { SymbolSearchScope.All };
                analysisPriority = SourceAnalysisPriority.None;
            }
            if (discovery.SymbolScopes is not null) symbolScopes = discovery.SymbolScopes;
            if (discovery.AnalysisPriority is not null) analysisPriority = discovery.AnalysisPriority;
            solutionPath = discovery.SolutionPath;
            analysisProgress = null;
            CancelBuildNoLock();
            rebuildCancellation = new CancellationTokenSource();
            var cancellationToken = rebuildCancellation.Token;
            state = candidateRoots.Length == 0 && candidateFiles.Length == 0
                ? SolutionFileIndexState.Empty
                : SolutionFileIndexState.Building;
            lastError = null;
            DisposeWatchersNoLock();
            activeBuild = Task.Run(async () =>
            {
                await workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { BuildIndex(discovery.SolutionPath, candidateRoots, candidateFiles, cancellationToken); }
                finally { workerGate.Release(); }
            }, cancellationToken);
            outstandingWork = Task.WhenAll(outstandingWork, activeBuild);
        }
    }

    public SolutionFileIndexSnapshot GetSnapshot()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return new SolutionFileIndexSnapshot(
                state,
                index.Count,
                roots.Count,
                isAnalyzing,
                sourceAnalyzer.SymbolCount,
                lastBuildDuration,
                lastError,
                analysisError,
                analysisProgress,
                refreshing,
                sourceAnalyzer.CachedSymbolsPublished);
        }
    }

    public IReadOnlyList<FileSearchMatch> Search(
        string query,
        int maximumResults,
        string? preferredRoot,
        CancellationToken cancellationToken) =>
        Search(
            query,
            maximumResults,
            preferredRoot,
            solutionRoot: null,
            FileSearchScope.All,
            openFiles: null,
            cancellationToken);

    public IReadOnlyList<FileSearchMatch> Search(
        string query,
        int maximumResults,
        string? preferredRoot,
        string? solutionRoot,
        FileSearchScope scope,
        ISet<string>? openFiles,
        CancellationToken cancellationToken)
    {
        string[] recentPaths;
        lock (gate)
        {
            ThrowIfDisposed();
            recentPaths = recentFiles.ToArray();
        }

        var candidates = index
            .GetPathsSnapshot()
            .Where(path => FileSearchScopeFilter.Includes(
                path,
                scope,
                preferredRoot,
                solutionRoot,
                openFiles));
        return FuzzyFileSearch.Search(
            query,
            candidates,
            new FileSearchRankingContext(preferredRoot, recentPaths),
            maximumResults,
            cancellationToken);
    }

    public IReadOnlyList<FileSearchMatch> GetSuggestions(
        int maximumResults,
        string? preferredRoot,
        string? solutionRoot,
        FileSearchScope scope,
        ISet<string>? openFiles,
        CancellationToken cancellationToken)
    {
        var code = GetSuggestionsOfType(maximumResults, preferredRoot, solutionRoot, scope, openFiles, cancellationToken, true);
        if (code.Count >= maximumResults) return code;
        return code.Concat(GetSuggestionsOfType(maximumResults - code.Count, preferredRoot, solutionRoot,
            scope, openFiles, cancellationToken, false)).ToArray();
    }

    private IReadOnlyList<FileSearchMatch> GetSuggestionsOfType(
        int maximumResults,
        string? preferredRoot,
        string? solutionRoot,
        FileSearchScope scope,
        ISet<string>? openFiles,
        CancellationToken cancellationToken,
        bool code)
    {
        if (maximumResults <= 0)
        {
            return Array.Empty<FileSearchMatch>();
        }

        string[] recentPaths;
        lock (gate)
        {
            ThrowIfDisposed();
            recentPaths = recentFiles.ToArray();
        }

        var paths = index.GetPathsSnapshot().Where(path => CodeFilePriority.IsCode(path) == code).ToArray();
        var availablePaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<FileSearchMatch>(maximumResults);

        for (var recentIndex = 0; recentIndex < recentPaths.Length; recentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recentPath = recentPaths[recentIndex];
            if (!availablePaths.Contains(recentPath) ||
                !FileSearchScopeFilter.Includes(
                    recentPath,
                    scope,
                    preferredRoot,
                    solutionRoot,
                    openFiles) ||
                !selectedPaths.Add(recentPath))
            {
                continue;
            }

            suggestions.Add(new FileSearchMatch(recentPath, 1000 - recentIndex));
            if (suggestions.Count == maximumResults)
            {
                return suggestions;
            }
        }

        var remainingLimit = maximumResults - suggestions.Count;
        var remainingPaths = new List<string>(remainingLimit);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!selectedPaths.Contains(path) &&
                FileSearchScopeFilter.Includes(
                    path,
                    scope,
                    preferredRoot,
                    solutionRoot,
                    openFiles))
            {
                InsertSuggestion(remainingPaths, path, remainingLimit, preferredRoot);
            }
        }

        suggestions.AddRange(remainingPaths.Select(path => new FileSearchMatch(path, 0)));

        return suggestions;
    }

    public IReadOnlyList<string> GetRecentFilesSnapshot()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return recentFiles.ToArray();
        }
    }

    private static void InsertSuggestion(
        List<string> suggestions,
        string candidate,
        int maximumResults,
        string? preferredRoot)
    {
        if (maximumResults <= 0)
        {
            return;
        }

        var low = 0;
        var high = suggestions.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (CompareSuggestions(candidate, suggestions[middle], preferredRoot) < 0)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        if (low >= maximumResults)
        {
            return;
        }

        suggestions.Insert(low, candidate);
        if (suggestions.Count > maximumResults)
        {
            suggestions.RemoveAt(suggestions.Count - 1);
        }
    }

    private static int CompareSuggestions(string first, string second, string? preferredRoot)
    {
        var codeComparison = CodeFilePriority.IsCode(second).CompareTo(CodeFilePriority.IsCode(first));
        if (codeComparison != 0) return codeComparison;
        var firstIsPreferred = FileSearchScopeFilter.IsInside(first, preferredRoot);
        var secondIsPreferred = FileSearchScopeFilter.IsInside(second, preferredRoot);
        if (firstIsPreferred != secondIsPreferred)
        {
            return firstIsPreferred ? -1 : 1;
        }

        var fileNameComparison = StringComparer.OrdinalIgnoreCase.Compare(
            Path.GetFileName(first),
            Path.GetFileName(second));
        return fileNameComparison != 0
            ? fileNameComparison
            : StringComparer.OrdinalIgnoreCase.Compare(first, second);
    }

    public void RecordRecentFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path!);
        }
        catch (Exception exception) when (
            exception is ArgumentException ||
            exception is NotSupportedException ||
            exception is PathTooLongException)
        {
            return;
        }

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (recentFileSet.Remove(normalizedPath))
            {
                recentFiles.Remove(normalizedPath);
            }

            recentFiles.AddFirst(normalizedPath);
            recentFileSet.Add(normalizedPath);
            while (recentFiles.Count > 20)
            {
                var last = recentFiles.Last!.Value;
                recentFiles.RemoveLast();
                recentFileSet.Remove(last);
            }
        }

        // 분석 중이면 방금 연 파일과 그 프로젝트를 먼저 분석합니다. 분석기 대기열 잠금은 이 서비스 잠금과 겹치지 않게 밖에서 잡습니다.
        sourceAnalyzer.Focus(normalizedPath);
    }

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task build;
            lock (gate)
            {
                ThrowIfDisposed();
                build = activeBuild;
            }

            try
            {
                // 창을 닫거나 새 검색을 시작하면 공유 인덱싱 작업과 독립적으로 대기를 끝냅니다.
                while (!build.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.WhenAny(build, Task.Delay(50, cancellationToken)).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await build.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Solution 변경으로 취소되면 아래에서 새 빌드가 끝났는지 다시 확인합니다.
            }

            lock (gate)
            {
                ThrowIfDisposed();
                if (ReferenceEquals(build, activeBuild))
                {
                    return;
                }
            }
        }
    }

    public IReadOnlyList<string> FindByStem(string stem) => index.FindByStem(stem);

    public IReadOnlyList<string> GetFilePathsSnapshot() => index.GetPathsSnapshot();

    public IReadOnlyList<SourceSymbolLocation> FindSymbol(string name) => sourceAnalyzer.FindSymbol(name);

    public IReadOnlyList<SourceSymbolMatch> SearchSymbols(
        string query,
        int maximumResults,
        CancellationToken cancellationToken, SymbolSearchScope? scope = null) =>
        sourceAnalyzer.SearchSymbols(query, maximumResults, cancellationToken,
            scope is null || ReferenceEquals(scope, SymbolSearchScope.All) ? null : scope.Includes);

    public async Task WaitUntilAnalysisReadyAsync()
    {
        while (true)
        {
            Task analysis;
            lock (gate)
            {
                ThrowIfDisposed();
                analysis = activeAnalysis;
            }

            try
            {
                await analysis.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Solution 변경으로 취소되면 새 분석 작업이 끝났는지 다시 확인합니다.
            }

            lock (gate)
            {
                ThrowIfDisposed();
                if (ReferenceEquals(analysis, activeAnalysis))
                {
                    return;
                }
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            roots = Array.Empty<string>();
            explicitFiles = Array.Empty<string>();
            requestedRoots = Array.Empty<string>();
            requestedFiles = Array.Empty<string>();
            solutionPath = string.Empty;
            symbolScopes = new[] { SymbolSearchScope.All };
            Interlocked.Increment(ref generation);
            CancelBuildNoLock();
            rebuildCancellation = new CancellationTokenSource();
            CancelRestoreNoLock();
            refreshTimer?.Dispose();
            refreshTimer = null;
            refreshEpoch++;
            changedFiles.Clear();
            fullRefreshPending = false;
            DisposeWatchersNoLock();
            index.Clear();
            sourceAnalyzer.Clear();
            sourceAnalyzer.ReleasePreviousAnalysis();
            recentFiles.Clear();
            recentFileSet.Clear();
            state = SolutionFileIndexState.Empty;
            lastBuildDuration = TimeSpan.Zero;
            lastError = null;
            isAnalyzing = false;
            refreshing = false;
            analysisError = null;
            analysisProgress = null;
        }
    }

    public void Dispose()
    {
        Task build;
        Task analysis;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CancelBuildNoLock();
            CancelRestoreNoLock();
            refreshTimer?.Dispose();
            refreshEpoch++;
            DisposeWatchersNoLock();
            build = outstandingWork;
            analysis = activeAnalysis;
        }

        _ = Task.WhenAll(build, analysis).ContinueWith(
            _ =>
            {
                index.Dispose();
                sourceAnalyzer.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void BuildIndex(
        string currentSolutionPath,
        IReadOnlyList<string> searchRoots,
        IReadOnlyList<string> projectFiles,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        SolutionFileIndexConfiguration currentConfiguration;
        lock (gate)
        {
            currentConfiguration = configuration;
        }

        try
        {
            var effectiveRoots = NormalizeRoots(searchRoots);
            var effectiveProjectFiles = projectFiles
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var cachedFiles = currentConfiguration.UsePersistentFileCache
                ? cache.Load(currentSolutionPath)
                : Array.Empty<string>();
            var explicitSet = new HashSet<string>(effectiveProjectFiles, StringComparer.OrdinalIgnoreCase);
            bool IsInScope(string path) => explicitSet.Contains(path) ||
                (ProjectSourceScope.IsSupplementalCode(path) && effectiveRoots.Any(root => IsInside(path, root)));
            // 디스크 검증과 큰 검색 맵 구축 중에는 UI 상태 조회 잠금을 점유하지 않습니다.
            index.ReplaceAll(cachedFiles.Where(IsInScope).Where(File.Exists).Concat(effectiveProjectFiles), cancellationToken);
            if (currentConfiguration.EnableSourceAnalysis)
            {
                // 수집 전에 공개한 저장 심볼에서 이번 파일 목록 밖의 파일을 뺍니다(복원이 공개하지 못했으면 목록 안의 것만 공개).
                sourceAnalyzer.LoadCachedSymbols(currentSolutionPath, cancellationToken, index.GetPathsSnapshot());
            }
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                roots = effectiveRoots;
                explicitFiles = effectiveProjectFiles;
                // 순회 중 추가·삭제된 파일도 놓치지 않게 먼저 감시를 시작합니다.
                ReplaceWatchersNoLock(effectiveRoots);
            }

            FileSystemPathCatalog.GetFiles(effectiveRoots, cancellationToken, ProjectSourceScope.IsSupplementalCode,
                file =>
                {
                    lock (gate)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        index.Add(file);
                    }
                });
            cancellationToken.ThrowIfCancellationRequested();
            var files = index.GetPathsSnapshot().Where(File.Exists).ToArray();
            // 감시 이벤트보다 늦게 열거된 삭제 파일을 정리하되, 순회 이후 생성된 파일은 보존합니다.
            var surviving = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var vanished = index.GetPathsSnapshot().Where(path => !surviving.Contains(path) && !File.Exists(path)).ToArray();
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var path in vanished) index.Remove(path);
            }
            stopwatch.Stop();

            lock (gate)
            {
                if (!disposed && !cancellationToken.IsCancellationRequested)
                {
                    lastBuildDuration = stopwatch.Elapsed;
                    roots = effectiveRoots;
                    explicitFiles = effectiveProjectFiles;
                    state = effectiveRoots.Count == 0 && effectiveProjectFiles.Length == 0
                        ? SolutionFileIndexState.Empty
                        : SolutionFileIndexState.Ready;
                    lastError = null;
                    isAnalyzing = currentConfiguration.EnableSourceAnalysis;
                    analysisError = null;
                    var priority = analysisPriority;
                    activeAnalysis = currentConfiguration.EnableSourceAnalysis
                        ? Task.Run(
                            () => AnalyzeSourcesAsync(
                                    currentSolutionPath,
                                    files,
                                    currentConfiguration.SourceAnalysisDelay,
                                    priority,
                                    cancellationToken),
                            cancellationToken)
                        : Task.CompletedTask;
                    outstandingWork = Task.WhenAll(outstandingWork, activeAnalysis);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (currentConfiguration.UsePersistentFileCache)
            {
                cache.Save(currentSolutionPath, files);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 더 최신 Solution 구성으로 다시 시작된 빌드는 상태를 덮어쓰지 않습니다.
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            lock (gate)
            {
                if (!disposed && !cancellationToken.IsCancellationRequested)
                {
                    lastBuildDuration = stopwatch.Elapsed;
                    state = SolutionFileIndexState.Faulted;
                    lastError = exception.Message;
                }
            }
        }
    }

    private async Task AnalyzeSourcesAsync(
        string currentSolutionPath,
        IReadOnlyList<string> files,
        TimeSpan delay,
        SourceAnalysisPriority priority,
        CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            // BuildIndex에서 이미 캐시를 공개했습니다. 같은 큰 심볼 검색 인덱스를 다시 만들지 않습니다.
            // 캐시 갱신과 대기 중 오류도 이 경계에서 처리합니다.
            ReportProgress(new SourceAnalysisProgress(SourceAnalysisStage.Waiting, 0, 0));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            // 최근에 연 순서(열린 문서 포함)입니다. 대기·지연 중에 연 파일도 들어가게 분석 직전에 읽고,
            // 분석 도중 연 파일은 RecordRecentFile이 대기열에 직접 알립니다.
            string[] focus;
            lock (gate) focus = recentFiles.ToArray();
            sourceAnalyzer.Analyze(
                currentSolutionPath,
                files,
                cancellationToken,
                ReportProgress,
                priority,
                focus);

            lock (gate)
            {
                if (!disposed && !cancellationToken.IsCancellationRequested)
                {
                    isAnalyzing = false;
                    analysisProgress = null;
                    analysisError = sourceAnalyzer.LastWarning;
                    // 패스 중에 바뀐 파일을 이제 반영합니다.
                    if (changedFiles.Count > 0) ArmRefreshTimerNoLock();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                if (!disposed && !cancellationToken.IsCancellationRequested)
                {
                    isAnalyzing = false;
                    analysisProgress = null;
                    analysisError = exception.Message;
                }
            }
        }
        finally { if (entered) workerGate.Release(); }

        void ReportProgress(SourceAnalysisProgress value)
        {
            lock (gate)
                if (!disposed && !cancellationToken.IsCancellationRequested) analysisProgress = value;
        }
    }

    private void ReplaceWatchersNoLock(IReadOnlyList<string> searchRoots)
    {
        DisposeWatchersNoLock();
        var replacements = new List<FileSystemWatcher>();

        foreach (var root in searchRoots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = false,
                };
                watcher.Created += OnCreated;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                replacements.Add(watcher);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // 감시할 수 없는 루트도 초기 인덱스 결과는 유지합니다.
            }
        }

        watchers = replacements;
    }

    private void OnCreated(object sender, FileSystemEventArgs eventArgs)
    {
        // 네트워크·잠긴 경로의 디스크 조회가 UI 상태 조회 잠금을 붙잡지 않게 합니다.
        var fileExists = File.Exists(eventArgs.FullPath);
        var directoryExists = !fileExists && Directory.Exists(eventArgs.FullPath);
        lock (gate)
        {
            if (disposed || !watchers.Contains(sender)) return;
            if (fileExists && (explicitFiles.Contains(eventArgs.FullPath, StringComparer.OrdinalIgnoreCase) ||
                ProjectSourceScope.IsSupplementalCode(eventArgs.FullPath)))
            {
                index.Add(eventArgs.FullPath);
                ScheduleUpdateNoLock(new[] { eventArgs.FullPath });
            }
            else if (directoryExists && !SolutionFileCatalog.IsExcludedPath(eventArgs.FullPath))
                ScheduleRefreshNoLock();
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs)
    {
        // 폴더의 Changed는 안의 항목이 생기거나 지워질 때마다(저장할 때도) 오고, 그 항목의 알림이 따로 옵니다. 파일만 봅니다.
        if (!File.Exists(eventArgs.FullPath)) return;
        OnCreated(sender, eventArgs);
    }

    private void OnDeleted(object sender, FileSystemEventArgs eventArgs)
    {
        lock (gate)
        {
            if (disposed || !watchers.Contains(sender)) return;
            var removed = index.GetPathsSnapshot().Where(path =>
                string.Equals(path, eventArgs.FullPath, StringComparison.OrdinalIgnoreCase) || IsInside(path, eventArgs.FullPath)).ToArray();
            foreach (var path in removed)
                index.Remove(path);
            if (removed.Length > 0)
            {
                ScheduleUpdateNoLock(removed);
            }
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs eventArgs)
    {
        OnDeleted(sender, new FileSystemEventArgs(WatcherChangeTypes.Deleted,
            Path.GetDirectoryName(eventArgs.OldFullPath)!, Path.GetFileName(eventArgs.OldFullPath)));
        OnCreated(sender, eventArgs);
    }

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs)
    {
        lock (gate)
        {
            if (disposed || !watchers.Contains(sender))
            {
                return;
            }

            // 검증과 재시작 사이에 솔루션이 바뀌어 이전 루트가 되살아나는 경합을 막습니다.
            Start(new SolutionIndexDiscoveryResult(solutionPath, requestedRoots, requestedFiles), force: true);
        }
    }

    private void ScheduleRefreshNoLock()
    {
        fullRefreshPending = true;
        ArmRefreshTimerNoLock();
    }

    private void ScheduleUpdateNoLock(IEnumerable<string> paths)
    {
        changedFiles.UnionWith(paths);
        ArmRefreshTimerNoLock();
    }

    private void ArmRefreshTimerNoLock()
    {
        if (refreshTimer is null)
        {
            var epoch = ++refreshEpoch;
            refreshTimer = new Timer(_ =>
            {
                lock (gate)
                {
                    if (disposed || epoch != refreshEpoch || state == SolutionFileIndexState.Empty) return;
                    if (fullRefreshPending)
                    {
                        Start(new SolutionIndexDiscoveryResult(solutionPath, requestedRoots, requestedFiles), force: true);
                        return;
                    }
                    // 수집·분석 패스 중에 바뀐 파일은 패스가 끝난 뒤 처리합니다(AnalyzeSourcesAsync가 다시 예약). 패스를 처음부터 다시 돌리지
                    // 않으므로 첫 분석 중에 저장해도 그때까지 분석한 결과를 잃지 않습니다.
                    if (changedFiles.Count == 0 || state != SolutionFileIndexState.Ready || isAnalyzing) return;
                    var paths = changedFiles.ToArray();
                    changedFiles.Clear();
                    var current = solutionPath;
                    var analyze = configuration.EnableSourceAnalysis;
                    var token = rebuildCancellation.Token;
                    var work = Task.Run(() => UpdateSourcesAsync(current, paths, analyze, token), token);
                    outstandingWork = Task.WhenAll(outstandingWork, work);
                }
            }, null, Timeout.Infinite, Timeout.Infinite);
        }
        // 저장·생성 시 연속으로 발생하는 알림을 한 번의 처리로 합칩니다.
        refreshTimer.Change(750, Timeout.Infinite);
    }

    /// <summary>
    /// 바뀐 파일만 파일 목록과 이름 인덱스에 반영합니다. 마친 분석 패스가 없어 부분 갱신할 수 없으면 전체 다시 수집합니다.
    /// </summary>
    /// <remarks>
    /// 예전에는 파일 하나를 저장해도 전체 다시 수집(파일 열거·모든 파일 확인·이름 인덱스 두 번 재구성·include 연결·분석 캐시 저장)을 해서
    /// 엔진 규모에서 저장마다 수십 초의 배경 작업과 1 GB 넘는 일시 메모리가 들었고, 그동안 상태가 수집 중으로 바뀌었습니다.
    /// </remarks>
    private async Task UpdateSourcesAsync(string currentSolutionPath, IReadOnlyList<string> paths, bool analyze, CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            // 저장은 임시 파일 이름 바꾸기로 지움·생성 알림이 섞여 오므로, 알림 순서가 아니라 지금 디스크 상태로 파일 목록을 맞춥니다.
            var present = new HashSet<string>(paths.Where(File.Exists), StringComparer.OrdinalIgnoreCase);
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var path in paths)
                {
                    if (present.Contains(path) && (explicitFiles.Contains(path, StringComparer.OrdinalIgnoreCase) ||
                        ProjectSourceScope.IsSupplementalCode(path)))
                        index.Add(path);
                    else index.Remove(path);
                }
            }
            if (analyze && !sourceAnalyzer.UpdateFiles(currentSolutionPath, paths, cancellationToken))
            {
                lock (gate)
                {
                    if (!disposed && !cancellationToken.IsCancellationRequested)
                        Start(new SolutionIndexDiscoveryResult(solutionPath, requestedRoots, requestedFiles), force: true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (gate)
                if (!disposed && !cancellationToken.IsCancellationRequested) analysisError = exception.Message;
        }
        finally { if (entered) workerGate.Release(); }
    }

    private void CancelBuildNoLock()
    {
        rebuildCancellation.Cancel();
        rebuildCancellation.Dispose();
    }

    private void CancelRestoreNoLock()
    {
        // 복원 작업이 이 토큰을 분석기 작업에 연결해 쓰는 중일 수 있어 Dispose하지 않습니다(타이머가 없어 정리할 자원도 없음).
        restoreCancellation.Cancel();
        restoreCancellation = new CancellationTokenSource();
    }

    private void DisposeWatchersNoLock()
    {
        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        watchers = Array.Empty<FileSystemWatcher>();
    }

    private static IReadOnlyList<string> NormalizeRoots(IEnumerable<string> searchRoots)
    {
        var orderedRoots = searchRoots
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ToList();
        var results = new List<string>();

        foreach (var candidate in orderedRoots)
        {
            if (!results.Any(root => IsInside(candidate, root)))
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    private static bool IsInside(string candidate, string root) =>
        candidate.StartsWith(
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(SolutionFileIndexService));
        }
    }
}
