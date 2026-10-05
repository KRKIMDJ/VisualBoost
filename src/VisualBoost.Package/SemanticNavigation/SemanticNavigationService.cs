using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Core.SemanticNavigation;
using VisualBoost.Services;

namespace VisualBoost.SemanticNavigation;

internal sealed class SemanticNavigationSettings
{
    public SemanticNavigationSettings(bool enabled, bool startOnSolutionOpen, string clangdPath, int workerCount, bool fallbackToVisualStudio)
    {
        Enabled = enabled;
        StartOnSolutionOpen = startOnSolutionOpen;
        ClangdPath = clangdPath ?? string.Empty;
        WorkerCount = Math.Max(0, workerCount);
        FallbackToVisualStudio = fallbackToVisualStudio;
    }

    public bool Enabled { get; }

    public bool StartOnSolutionOpen { get; }

    public string ClangdPath { get; }

    public int WorkerCount { get; }

    public bool FallbackToVisualStudio { get; }

    /// <summary>실행 중인 clangd를 다시 시작해야 하는 설정 차이입니다.</summary>
    public bool RequiresRestart(SemanticNavigationSettings other) =>
        !string.Equals(ClangdPath, other.ClangdPath, StringComparison.OrdinalIgnoreCase) || WorkerCount != other.WorkerCount;
}

/// <summary>
/// Solution 수명에 맞춰 clangd 탐색기를 시작·중지하고, 편집기 문서 알림을 순서대로 전달합니다.
/// </summary>
/// <remarks>
/// 시작은 첫 요청이나 Solution 열기 때 작업 스레드에서 합니다. 쓸 수 없는 이유(경로·빌드 응답 파일 없음 등)는
/// 같은 Solution에서 빌드 완료·옵션 변경 전까지 기억해 매 요청마다 다시 탐색하지 않습니다.
/// clangd가 비정상 종료하면 다음 요청에서 다시 시작하되, 반복되면 중지합니다.
/// 문서 알림은 직렬 큐 하나로 보내 같은 문서의 내용이 뒤바뀌어 도착하지 않게 합니다.
/// 실행 중에는 Solution 폴더의 C++ 소스를 감시해 편집기 밖 변경을 반영합니다. 바뀐 파일이 적으면 하나씩 다시 분석하고,
/// 많거나(브랜치 전환 등) 삭제가 있으면 다시 시작합니다. 재시작한 clangd는 바뀐 파일만 다시 색인합니다.
/// </remarks>
internal sealed class SemanticNavigationService : IDisposable
{
    private const int MaxUnexpectedExits = 3;

    // 하나씩 다시 분석하면 TU마다 작업 스레드 하나로 전체 분석을 하므로, 이보다 많으면 병렬 재색인하는 재시작이 빠릅니다.
    private const int MaxReloadsPerBatch = 16;

    private static readonly TimeSpan SourceChangeQuiet = TimeSpan.FromSeconds(2);

    // 컴파일 명령이 아직 없을 때(빌드 전, CMake 구성 중) 요청이 다시 시도하기까지의 간격입니다.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);

    // 폴더 작업 영역은 VS가 연 뒤에 CMake 구성을 시작하므로 배경에서 몇 번 더 시도합니다.
    private static readonly TimeSpan FolderRetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxFolderRetries = 10;

    private readonly object gate = new();
    private readonly SolutionFileIndexService fileIndex;
    private readonly Func<CancellationToken, Task<IReadOnlyList<MsBuildProjectConfiguration>>>? projectProvider;
    private readonly SerialWorkQueue documentQueue = new();
    private readonly string cacheRoot;
    private SemanticNavigationSettings settings;
    private string? solutionPath;
    private int generation;
    private ClangdNavigator? navigator;
    private SourceChangeMonitor? monitor;
    private Task<ClangdNavigator?>? starting;
    private CancellationTokenSource? startCancellation;
    private string? unavailableReason;
    private bool unavailableRetryable;
    private DateTime unavailableAt;
    private int folderRetries;
    private string? clangdPath;
    private int unexpectedExits;
    private int disposed;

    /// <param name="projectProvider">compile_commands.json이 없을 때 쓸 C++ 프로젝트 목록(활성 구성)을 UI thread에서 모읍니다.</param>
    public SemanticNavigationService(SolutionFileIndexService fileIndex, SemanticNavigationSettings settings,
        Func<CancellationToken, Task<IReadOnlyList<MsBuildProjectConfiguration>>>? projectProvider = null)
    {
        this.fileIndex = fileIndex;
        this.settings = settings;
        this.projectProvider = projectProvider;
        cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualBoost", "Clangd");
    }

    /// <summary>시작·종료·색인 진행이 바뀌었습니다. 임의 스레드에서 호출됩니다.</summary>
    public event Action? StateChanged;

    public SemanticNavigationSettings Settings
    {
        get
        {
            lock (gate) return settings;
        }
    }

    /// <summary>현재 탐색기. 시작 전이거나 종료됐으면 null입니다.</summary>
    public ClangdNavigator? Current
    {
        get
        {
            lock (gate) return navigator is { HasExited: false } ? navigator : null;
        }
    }

    /// <summary>상태 표시줄 문구입니다. 표시할 진행이 없으면 null입니다.</summary>
    public string? StatusText
    {
        get
        {
            lock (gate)
            {
                if (starting is not null) return "VisualBoost: 정의·참조 탐색 준비 중";
                if (navigator is not { HasExited: false } current) return null;
                var progress = current.Progress;
                if (!progress.Active) return null;
                return progress.Total > 0
                    ? $"VisualBoost: 정의·참조 색인 {progress.Done:N0}/{progress.Total:N0}"
                    : "VisualBoost: 정의·참조 색인 중";
            }
        }
    }

    public void ApplySettings(SemanticNavigationSettings value)
    {
        bool restart;
        bool start;
        lock (gate)
        {
            var previous = settings;
            settings = value;
            restart = value.Enabled && navigator is not null && value.RequiresRestart(previous);
            start = value.Enabled && !previous.Enabled && value.StartOnSolutionOpen && solutionPath is not null;
            unavailableReason = null;
        }

        if (!value.Enabled)
        {
            Stop("옵션에서 정의·참조 탐색이 꺼져 있습니다.");
        }
        else if (restart)
        {
            Restart();
        }
        else if (start)
        {
            BeginStart();
        }
    }

    public void SolutionOpened(string path)
    {
        Stop(null);
        bool start;
        lock (gate)
        {
            solutionPath = string.IsNullOrWhiteSpace(path) ? null : path;
            unexpectedExits = 0;
            folderRetries = 0;
            unavailableReason = null;
            start = solutionPath is not null && settings.Enabled && settings.StartOnSolutionOpen;
        }

        if (start) BeginStart();
    }

    public void SolutionClosed()
    {
        Stop(null);
        lock (gate) solutionPath = null;
    }

    /// <summary>빌드가 끝나면 응답 파일·프로젝트 설정이 바뀌었을 수 있으므로 명령을 다시 만들고, 바뀌었으면 다시 시작합니다.</summary>
    public void BuildCompleted() => RefreshCommands();

    /// <summary>컴파일 명령을 다시 만들어 바뀌었으면 다시 시작합니다. 쓸 수 없던 상태는 다음 요청에서 다시 시도하게 합니다.</summary>
    private void RefreshCommands()
    {
        ClangdNavigator? current;
        string? solution;
        int observed;
        lock (gate)
        {
            current = navigator;
            solution = solutionPath;
            observed = generation;
            if (unavailableReason is not null)
            {
                // 빌드 전이라 쓸 수 없던 경우는 다음 요청에서 다시 시도합니다.
                unavailableReason = null;
                current = null;
            }
        }

        if (current is null || solution is null || current.Context.Kind == CompileContextKind.None)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var compiler = Path.Combine(Path.GetDirectoryName(clangdPath ?? string.Empty) ?? string.Empty, "clang-cl.exe");
                var projects = current.Context.Kind == CompileContextKind.MsBuild
                    ? await CollectProjectsAsync(CancellationToken.None).ConfigureAwait(false)
                    : Array.Empty<MsBuildProjectConfiguration>();
                var context = CompileContextBuilder.Prepare(solution, cacheRoot, current.Context.EngineRoot, File.Exists(compiler) ? compiler : "clang-cl.exe",
                    CancellationToken.None, new CompileCommandSources { MsBuildPath = BuildToolLocator.FindMsBuild(), Projects = projects, NinjaPath = BuildToolLocator.FindNinja() });
                bool restart;
                lock (gate) restart = context.Changed && observed == generation && ReferenceEquals(navigator, current);
                if (restart) Restart();
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is InvalidDataException ||
                                              exception is FormatException)
            {
                ActivityLog.LogWarning("VisualBoost/SemanticNavigation", "컴파일 명령 갱신 실패: " + exception);
            }
        });
    }

    /// <summary>탐색기를 돌려줍니다. 필요하면 시작하고, 쓸 수 없으면 이유와 함께 예외를 던집니다.</summary>
    public async Task<ClangdNavigator> GetNavigatorAsync(CancellationToken cancellationToken)
    {
        Task<ClangdNavigator?> task;
        lock (gate)
        {
            if (!settings.Enabled) throw new SemanticNavigationUnavailableException("옵션에서 정의·참조 탐색이 꺼져 있습니다.");
            if (solutionPath is null) throw new SemanticNavigationUnavailableException("열린 Solution이 없습니다.");
            if (navigator is { HasExited: false } current) return current;
            if (unavailableReason is not null)
            {
                if (!unavailableRetryable || DateTime.UtcNow - unavailableAt < RetryInterval) throw new SemanticNavigationUnavailableException(unavailableReason);
                unavailableReason = null;
            }

            task = starting ?? StartLockedAsync();
        }

        RaiseStateChanged();
        var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await ((Task<ClangdNavigator?>)completed).ConfigureAwait(false);
        if (result is null)
        {
            lock (gate) throw new SemanticNavigationUnavailableException(unavailableReason ?? "clangd를 시작하지 못했습니다.");
        }

        return result;
    }

    public void Warm(string path, Func<string> text, long revision) => Post(navigator => navigator.Warm(new DocumentText(path, text, revision)));

    public void Update(string path, Func<string> text, long revision) => Post(navigator => navigator.Update(new DocumentText(path, text, revision)));

    public void Saved(string path, Func<string> text, long revision) => Post(navigator => navigator.Saved(new DocumentText(path, text, revision)));

    public void Closed(string path) => Post(navigator => navigator.Closed(path));

    /// <summary>상태 확인 창에 보여 줄 설명입니다.</summary>
    public string Describe()
    {
        lock (gate)
        {
            var lines = new List<string>();
            if (!settings.Enabled)
            {
                lines.Add("상태: 꺼짐");
            }
            else if (starting is not null)
            {
                lines.Add("상태: 준비 중");
            }
            else if (navigator is { HasExited: false } current)
            {
                var progress = current.Progress;
                lines.Add(progress.Active
                    ? $"상태: 색인 중 {progress.Done:N0}/{progress.Total:N0}"
                    : progress.Completed ? "상태: 준비됨(색인 완료)" : "상태: 준비됨");
                lines.Add("컴파일 명령: " + current.Context.Summary);
                lines.Add("clangd: " + clangdPath + $" (PID {current.ProcessId})");
                lines.Add("캐시: " + current.Context.Directory);
            }
            else
            {
                lines.Add("상태: " + (unavailableReason is null ? "시작 전(첫 요청 때 시작)" : "사용할 수 없음"));
                if (unavailableReason is not null) lines.Add("이유: " + unavailableReason);
            }

            return string.Join("\n", lines);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ClangdNavigator? current;
        SourceChangeMonitor? watcher;
        lock (gate)
        {
            generation++;
            startCancellation?.Cancel();
            current = navigator;
            navigator = null;
            starting = null;
            watcher = monitor;
            monitor = null;
        }

        watcher?.Dispose();
        // VS 종료 중에는 기다리지 않습니다. 색인 조각은 원자적으로 기록되므로 강제 종료해도 캐시가 깨지지 않습니다.
        current?.Dispose();
    }

    private void Post(Action<ClangdNavigator> action)
    {
        var current = Current;
        if (current is null) return;
        documentQueue.Post(() =>
        {
            if (!current.HasExited) action(current);
        });
    }

    private void BeginStart()
    {
        lock (gate)
        {
            if (starting is not null || navigator is { HasExited: false } || solutionPath is null || !settings.Enabled) return;
            _ = StartLockedAsync();
        }

        RaiseStateChanged();
    }

    private Task<ClangdNavigator?> StartLockedAsync()
    {
        var observed = generation;
        var path = solutionPath!;
        var options = settings;
        startCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        startCancellation = cancellation;
        var task = Task.Run(() => StartCoreAsync(observed, path, options, cancellation.Token));
        starting = task;
        return task;
    }

    private async Task<ClangdNavigator?> StartCoreAsync(int observed, string solution, SemanticNavigationSettings options, CancellationToken cancellationToken)
    {
        try
        {
            var executable = ClangdLocator.Find(options.ClangdPath);
            if (executable is null)
            {
                return Unavailable(observed, retryable: false, string.IsNullOrWhiteSpace(options.ClangdPath)
                    ? "clangd를 찾지 못했습니다. Visual Studio 설치 관리자에서 'C++ Clang 도구'를 설치하거나 옵션에서 경로를 지정하세요."
                    : "옵션에 지정한 clangd가 없습니다: " + options.ClangdPath);
            }

            lock (gate) clangdPath = executable;
            var projects = await CollectProjectsAsync(cancellationToken).ConfigureAwait(false);
            var created = await ClangdNavigator.StartAsync(new ClangdNavigatorOptions
            {
                MsBuildPath = BuildToolLocator.FindMsBuild(),
                Projects = projects,
                NinjaPath = BuildToolLocator.FindNinja(),
                ClangdPath = executable,
                CacheRoot = cacheRoot,
                SolutionPath = solution,
                EngineRoot = FindEngineRoot(solution),
                WorkerCount = options.WorkerCount,
                FindSymbols = name => fileIndex.FindSymbol(name),
                FindByStem = stem => fileIndex.FindByStem(stem)
            }, cancellationToken).ConfigureAwait(false);

            var watcher = CreateMonitor(solution, observed);
            lock (gate)
            {
                if (observed != generation || cancellationToken.IsCancellationRequested)
                {
                    watcher?.Dispose();
                    created.Dispose();
                    return null;
                }

                navigator = created;
                monitor = watcher;
                starting = null;
                unavailableReason = null;
            }

            created.Changed += () => OnNavigatorChanged(created);
            if (created.HasExited) OnNavigatorChanged(created);
            RaiseStateChanged();
            return created;
        }
        catch (SemanticNavigationUnavailableException exception)
        {
            return Unavailable(observed, retryable: true, exception.Message);
        }
        catch (OperationCanceledException)
        {
            lock (gate)
            {
                if (observed == generation) starting = null;
            }

            RaiseStateChanged();
            return null;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is InvalidDataException ||
                                          exception is FormatException || exception is LspConnectionClosedException)
        {
            ActivityLog.LogError("VisualBoost/SemanticNavigation", exception.ToString());
            return Unavailable(observed, retryable: true, "clangd 탐색을 준비하지 못했습니다: " + exception.Message);
        }
    }

    /// <param name="retryable">컴파일 명령이 나중에 생길 수 있는 이유입니다(clangd 없음·옵션 꺼짐은 아님).</param>
    private ClangdNavigator? Unavailable(int observed, bool retryable, string reason)
    {
        bool retryInBackground;
        lock (gate)
        {
            if (observed != generation) return null;
            starting = null;
            unavailableReason = reason;
            unavailableRetryable = retryable;
            unavailableAt = DateTime.UtcNow;
            retryInBackground = retryable && settings.StartOnSolutionOpen && solutionPath is not null && Directory.Exists(solutionPath) &&
                                folderRetries++ < MaxFolderRetries;
        }

        if (retryInBackground)
        {
            _ = Task.Delay(FolderRetryDelay).ContinueWith(_ =>
            {
                bool retry;
                lock (gate)
                {
                    retry = observed == generation && unavailableReason is not null && unavailableRetryable;
                    if (retry) unavailableReason = null;
                }

                if (retry) BeginStart();
            }, TaskScheduler.Default);
        }

        RaiseStateChanged();
        return null;
    }

    private void OnNavigatorChanged(ClangdNavigator source)
    {
        if (source.HasExited)
        {
            var unexpected = false;
            SourceChangeMonitor? watcher = null;
            lock (gate)
            {
                if (ReferenceEquals(navigator, source))
                {
                    navigator = null;
                    watcher = monitor;
                    monitor = null;
                    unexpected = true;
                    unexpectedExits++;
                    if (unexpectedExits >= MaxUnexpectedExits)
                    {
                        unavailableReason = $"clangd가 반복해서 종료되어 중지했습니다. 로그: {source.LogPath}";
                    }
                }
            }

            watcher?.Dispose();
            if (unexpected)
            {
                ActivityLog.LogWarning("VisualBoost/SemanticNavigation", "clangd가 예기치 않게 종료되었습니다. 로그: " + source.LogPath);
                source.Dispose();
                bool restart;
                int observed;
                lock (gate)
                {
                    restart = unavailableReason is null && settings.StartOnSolutionOpen;
                    observed = generation;
                }

                if (restart)
                {
                    // 남은 색인을 이어 가도록 잠시 뒤 다시 시작합니다. 반복 종료는 위 상한에서 멈춥니다.
                    _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
                    {
                        bool same;
                        lock (gate) same = observed == generation;
                        if (same) BeginStart();
                    }, TaskScheduler.Default);
                }
            }
        }

        RaiseStateChanged();
    }

    private void Restart()
    {
        Stop(null);
        BeginStart();
    }

    private void Stop(string? reason)
    {
        ClangdNavigator? current;
        SourceChangeMonitor? watcher;
        lock (gate)
        {
            generation++;
            startCancellation?.Cancel();
            starting = null;
            current = navigator;
            navigator = null;
            watcher = monitor;
            monitor = null;
            unavailableReason = reason;
        }

        watcher?.Dispose();

        if (current is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await current.ShutdownAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException || exception is InvalidOperationException || exception is ObjectDisposedException)
                {
                    current.Dispose();
                }
            });
        }

        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    private async Task<IReadOnlyList<MsBuildProjectConfiguration>> CollectProjectsAsync(CancellationToken cancellationToken) =>
        projectProvider is null ? Array.Empty<MsBuildProjectConfiguration>() : await projectProvider(cancellationToken).ConfigureAwait(false);

    private SourceChangeMonitor? CreateMonitor(string solution, int observed)
    {
        var root = CompileContextBuilder.WorkspaceDirectory(solution);
        if (!Directory.Exists(root)) return null;
        try
        {
            return new SourceChangeMonitor(root, SourceChangeQuiet, batch => OnSourcesChanged(observed, batch));
        }
        catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is UnauthorizedAccessException)
        {
            // 감시를 못 해도 편집기 저장·재시작 경로로는 최신성을 유지합니다.
            ActivityLog.LogWarning("VisualBoost/SemanticNavigation", "소스 변경 감시를 시작하지 못했습니다: " + exception.Message);
            return null;
        }
    }

    private void OnSourcesChanged(int observed, SourceChangeBatch batch)
    {
        ClangdNavigator? current;
        lock (gate)
        {
            if (observed != generation) return;
            current = navigator;
        }

        if (current is not { HasExited: false }) return;
        if (batch.CommandsChanged) RefreshCommands();
        // 편집기에 열린 문서는 편집기 내용이 기준이며, 다시 불러오면 문서 추적기가 알립니다.
        var paths = batch.Paths.Where(path => !SemanticDocumentTracker.IsOpenInEditor(path)).ToArray();
        if (batch.RequiresRestart || paths.Length > MaxReloadsPerBatch)
        {
            ActivityLog.LogInformation("VisualBoost/SemanticNavigation",
                $"편집기 밖 소스 변경으로 clangd를 다시 시작합니다(바뀐 파일 {paths.Length}개, 삭제·감시 누락 {batch.RequiresRestart}).");
            Restart();
            return;
        }

        foreach (var path in paths)
        {
            Post(navigator => navigator.Reload(path));
        }
    }

    private static string? FindEngineRoot(string solution)
    {
        var source = UnrealEngineSourceLocator.Find(CompileContextBuilder.WorkspaceDirectory(solution), Array.Empty<string>()).FirstOrDefault();
        // 위치 탐색은 Engine/Source를 돌려주므로 두 단계 위가 설치 루트입니다.
        var engine = source is null ? null : Path.GetDirectoryName(source.TrimEnd('\\', '/'));
        return engine is null ? null : Path.GetDirectoryName(engine);
    }

    /// <summary>작업을 받은 순서대로 작업 스레드 하나에서 실행합니다.</summary>
    private sealed class SerialWorkQueue
    {
        private readonly object queueGate = new();
        private readonly Queue<Action> items = new();
        private bool running;

        public void Post(Action action)
        {
            lock (queueGate)
            {
                items.Enqueue(action);
                if (running) return;
                running = true;
            }

            _ = Task.Run(Drain);
        }

        private void Drain()
        {
            while (true)
            {
                Action action;
                lock (queueGate)
                {
                    if (items.Count == 0)
                    {
                        running = false;
                        return;
                    }

                    action = items.Dequeue();
                }

                try
                {
                    action();
                }
                catch (Exception exception) when (exception is LspConnectionClosedException || exception is ObjectDisposedException || exception is IOException)
                {
                    // 종료된 세션에 대한 늦은 알림입니다. 종료 처리는 StateChanged 경로가 맡습니다.
                }
            }
        }
    }
}
