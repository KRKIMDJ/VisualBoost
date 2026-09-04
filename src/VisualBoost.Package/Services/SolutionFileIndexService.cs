using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Core.Indexing;

namespace VisualBoost.Services;

internal sealed class SolutionFileIndexService : IDisposable
{
    private readonly object gate = new();
    private readonly FilePathIndex index = new();
    private CancellationTokenSource rebuildCancellation = new();
    private IReadOnlyList<string> roots = Array.Empty<string>();
    private IReadOnlyList<FileSystemWatcher> watchers = Array.Empty<FileSystemWatcher>();
    private Task activeBuild = Task.CompletedTask;
    private bool disposed;

    public int Count => index.Count;

    public TimeSpan LastBuildDuration { get; private set; }

    public void Start(IReadOnlyList<string> searchRoots)
    {
        if (searchRoots is null)
        {
            throw new ArgumentNullException(nameof(searchRoots));
        }

        var normalizedRoots = NormalizeRoots(searchRoots);

        lock (gate)
        {
            ThrowIfDisposed();
            if (roots.SequenceEqual(normalizedRoots, StringComparer.OrdinalIgnoreCase) && !activeBuild.IsFaulted)
            {
                return;
            }

            roots = normalizedRoots;
            CancelBuildNoLock();
            rebuildCancellation = new CancellationTokenSource();
            var cancellationToken = rebuildCancellation.Token;
            activeBuild = Task.Run(() => BuildIndex(normalizedRoots, cancellationToken), cancellationToken);
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

    public void Clear()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            roots = Array.Empty<string>();
            CancelBuildNoLock();
            rebuildCancellation = new CancellationTokenSource();
            DisposeWatchersNoLock();
            index.Clear();
        }
    }

    public void Dispose()
    {
        Task build;
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
        }

        _ = build.ContinueWith(
            _ => index.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void BuildIndex(IReadOnlyList<string> searchRoots, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var files = SolutionFileCatalog.GetFiles(searchRoots, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        index.ReplaceAll(files);
        stopwatch.Stop();
        LastBuildDuration = stopwatch.Elapsed;

        lock (gate)
        {
            if (!disposed && !cancellationToken.IsCancellationRequested)
            {
                ReplaceWatchersNoLock(searchRoots);
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
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            currentRoots = roots;
            roots = Array.Empty<string>();
        }

        Start(currentRoots);
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
