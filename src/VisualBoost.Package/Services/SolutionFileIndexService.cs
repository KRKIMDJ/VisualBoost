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
    private readonly FileIndexCache cache = new();
    private readonly SolutionSourceAnalyzer sourceAnalyzer = new();
    private CancellationTokenSource rebuildCancellation = new();
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
    private string? analysisError;
    private readonly LinkedList<string> recentFiles = new();
    private readonly HashSet<string> recentFileSet = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public int Count => index.Count;

    public void Start(SolutionIndexDiscoveryResult discovery)
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
            if (requestedRoots.SequenceEqual(candidateRoots, StringComparer.OrdinalIgnoreCase) &&
                requestedFiles.SequenceEqual(candidateFiles, StringComparer.OrdinalIgnoreCase) &&
                state != SolutionFileIndexState.Faulted)
            {
                return;
            }

            requestedRoots = candidateRoots;
            requestedFiles = candidateFiles;
            solutionPath = discovery.SolutionPath;
            CancelBuildNoLock();
            rebuildCancellation = new CancellationTokenSource();
            var cancellationToken = rebuildCancellation.Token;
            state = candidateRoots.Length == 0 && candidateFiles.Length == 0
                ? SolutionFileIndexState.Empty
                : SolutionFileIndexState.Building;
            lastError = null;
            activeBuild = Task.Run(
                () => BuildIndex(
                    discovery.SolutionPath,
                    candidateRoots,
                    candidateFiles,
                    cancellationToken),
                cancellationToken);
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
                sourceAnalyzer.IncludeEdgeCount,
                lastBuildDuration,
                lastError,
                analysisError);
        }
    }

    public IReadOnlyList<FileSearchMatch> Search(
        string query,
        int maximumResults,
        string? preferredRoot,
        CancellationToken cancellationToken)
    {
        string[] recentPaths;
        lock (gate)
        {
            ThrowIfDisposed();
            recentPaths = recentFiles.ToArray();
        }

        return index.Search(
            query,
            new FileSearchRankingContext(preferredRoot, recentPaths),
            maximumResults,
            cancellationToken);
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
    }

    public async Task WaitUntilReadyAsync()
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
                await build.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
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

    public IReadOnlyList<SourceSymbolLocation> FindSymbol(string name) => sourceAnalyzer.FindSymbol(name);

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
            CancelBuildNoLock();
            rebuildCancellation = new CancellationTokenSource();
            DisposeWatchersNoLock();
            index.Clear();
            sourceAnalyzer.Clear();
            recentFiles.Clear();
            recentFileSet.Clear();
            state = SolutionFileIndexState.Empty;
            lastBuildDuration = TimeSpan.Zero;
            lastError = null;
            isAnalyzing = false;
            analysisError = null;
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
            DisposeWatchersNoLock();
            build = activeBuild;
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
        try
        {
            var solutionDirectory = Path.GetDirectoryName(currentSolutionPath);
            var engineRoots = UnrealEngineSourceLocator.Find(
                solutionDirectory,
                searchRoots.Concat(projectFiles));
            var initialRoots = NormalizeRoots(searchRoots.Concat(engineRoots));
            var effectiveProjectFiles = projectFiles
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var cachedFiles = cache.Load(currentSolutionPath);
            if (cachedFiles.Count > 0)
            {
                index.ReplaceAll(cachedFiles.Concat(effectiveProjectFiles));
            }

            var initialFiles = SolutionFileCatalog.GetFiles(initialRoots, cancellationToken);
            var includeRoots = CppProjectSearchRootLocator.Find(solutionDirectory, initialFiles);
            var effectiveRoots = NormalizeRoots(initialRoots.Concat(includeRoots));
            var additionalRoots = effectiveRoots.Where(candidate =>
                !initialRoots.Any(root =>
                    string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
                    IsInside(candidate, root)));
            var files = initialFiles
                .Concat(SolutionFileCatalog.GetFiles(additionalRoots, cancellationToken))
                .Concat(effectiveProjectFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            index.ReplaceAll(files);
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
                    ReplaceWatchersNoLock(effectiveRoots);
                    isAnalyzing = true;
                    analysisError = null;
                    activeAnalysis = Task.Run(
                        async () =>
                        {
                            // Solution 로드 직후의 Visual Studio 작업과 CPU 및 디스크 사용이 겹치지 않게 양보합니다.
                            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                            AnalyzeSources(
                                currentSolutionPath,
                                files,
                                effectiveRoots,
                                cancellationToken);
                        },
                        cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            cache.Save(currentSolutionPath, files);
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

    private void AnalyzeSources(
        string currentSolutionPath,
        IReadOnlyList<string> files,
        IReadOnlyList<string> includeRoots,
        CancellationToken cancellationToken)
    {
        try
        {
            var externalFiles = sourceAnalyzer.Analyze(
                currentSolutionPath,
                files,
                includeRoots,
                cancellationToken);
            foreach (var file in externalFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index.Add(file);
            }

            lock (gate)
            {
                if (!disposed && !cancellationToken.IsCancellationRequested)
                {
                    isAnalyzing = false;
                    analysisError = null;
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
                    analysisError = exception.Message;
                }
            }
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
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    EnableRaisingEvents = false,
                };
                watcher.Created += OnCreated;
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
        if (File.Exists(eventArgs.FullPath) && !SolutionFileCatalog.IsExcludedPath(eventArgs.FullPath))
        {
            index.Add(eventArgs.FullPath);
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs eventArgs) => index.Remove(eventArgs.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs eventArgs)
    {
        index.Remove(eventArgs.OldFullPath);
        OnCreated(sender, eventArgs);
    }

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs)
    {
        IReadOnlyList<string> currentRoots;
        IReadOnlyList<string> currentFiles;
        string currentSolutionPath;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            currentRoots = roots;
            currentFiles = explicitFiles;
            currentSolutionPath = solutionPath;
            requestedRoots = Array.Empty<string>();
        }

        Start(new SolutionIndexDiscoveryResult(currentSolutionPath, currentRoots, currentFiles));
    }

    private void CancelBuildNoLock()
    {
        rebuildCancellation.Cancel();
        rebuildCancellation.Dispose();
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
