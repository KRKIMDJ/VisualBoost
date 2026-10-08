using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public SemanticNavigationSettings(bool enabled, bool startOnSolutionOpen, string clangdPath, int workerCount, bool fallbackToVisualStudio,
        int memoryLimitMegabytes = 0, UnrealPchMode pchMode = UnrealPchMode.Auto)
    {
        Enabled = enabled;
        StartOnSolutionOpen = startOnSolutionOpen;
        ClangdPath = clangdPath ?? string.Empty;
        WorkerCount = Math.Max(0, workerCount);
        FallbackToVisualStudio = fallbackToVisualStudio;
        MemoryLimitMegabytes = Math.Max(0, memoryLimitMegabytes);
        PchMode = Enum.IsDefined(typeof(UnrealPchMode), pchMode) ? pchMode : UnrealPchMode.Auto;
    }

    /// <summary>Unreal 공유 PCH 헤더를 분석 명령에 넣는 방식입니다.</summary>
    public UnrealPchMode PchMode { get; }

    /// <summary>clangd 메모리 정리 기준(MB)입니다. 0이면 <see cref="ClangdMemoryPolicy.DefaultLimitBytes"/>입니다.</summary>
    public int MemoryLimitMegabytes { get; }

    public long MemoryLimitBytes => ClangdMemoryPolicy.ResolveLimitBytes(MemoryLimitMegabytes);

    public bool Enabled { get; }

    public bool StartOnSolutionOpen { get; }

    public string ClangdPath { get; }

    public int WorkerCount { get; }

    public bool FallbackToVisualStudio { get; }

    /// <summary>실행 중인 clangd를 다시 시작해야 하는 설정 차이입니다.</summary>
    public bool RequiresRestart(SemanticNavigationSettings other) =>
        !string.Equals(ClangdPath, other.ClangdPath, StringComparison.OrdinalIgnoreCase) || WorkerCount != other.WorkerCount || PchMode != other.PchMode;
}

/// <summary>
/// Solution 수명에 맞춰 clangd 탐색기를 시작·중지하고, 편집기 문서 알림을 순서대로 전달합니다.
/// </summary>
/// <remarks>
/// 시작은 첫 요청이나 Solution 열기 때 작업 스레드에서 합니다. 쓸 수 없는 이유(경로·빌드 응답 파일 없음 등)는
/// 같은 Solution에서 빌드 완료·옵션 변경 전까지 기억해 매 요청마다 다시 탐색하지 않습니다.
/// clangd가 비정상 종료하면 잠시 뒤 다시 시작하되, 짧은 시간에 반복되면 몇 분 쉰 뒤 다시 시도합니다.
/// clangd가 해제한 메모리를 쥐고 있어 정리 기준을 넘으면 유휴 상태에서 다시 시작합니다(<see cref="ClangdMemoryPolicy"/>).
/// 공유 PCH를 넣어 다시 색인한 결과를 clangd가 메모리에 반영하지 않으므로 그때도 요청이 멈추면 다시 시작합니다(<see cref="ClangdNavigator.NeedsReload"/>).
/// 문서 알림은 직렬 큐 하나로 보내 같은 문서의 내용이 뒤바뀌어 도착하지 않게 합니다.
/// 실행 중에는 Solution 폴더의 C++ 소스를 감시해 편집기 밖 변경을 반영합니다. 바뀐 파일이 적으면 하나씩 다시 분석하고,
/// 많거나(브랜치 전환 등) 삭제가 있으면 다시 시작합니다. 재시작한 clangd는 바뀐 파일만 다시 색인합니다.
/// </remarks>
internal sealed class SemanticNavigationService : IDisposable
{
    private const int MaxUnexpectedExits = 3;

    // 이 시간 안에 MaxUnexpectedExits번 종료하면 잠시 쉬었다가 다시 시도합니다. 예전에는 Solution을 다시 열 때까지 멈춰
    // 메모리 부족 등으로 몇 번 종료된 뒤 탐색을 전혀 쓸 수 없었습니다(2026-10-08 회사 사용 피드백).
    // 쉼은 5·10·20분으로 늘리고, 그 뒤에도 반복하면 원인이 고정된 종료(특정 TU의 메모리 부족 등)로 보고 멈춥니다.
    // 멈춘 상태는 빌드 완료·옵션 변경·Solution 다시 열기에서 풉니다. 끝없이 반복하면 회마다 PC 전체가 메모리 압박을 받습니다.
    private static readonly TimeSpan ExitWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExitPause = TimeSpan.FromMinutes(5);
    private const int MaxExitPauses = 3;
    // 한 clangd가 이만큼 정상 동작한 뒤의 종료는 이전 쉼과 무관한 것으로 보고 쉼 횟수를 되돌립니다(드문 종료 묶음이 쌓여 멈추지 않게).
    private static readonly TimeSpan ExitPauseReset = TimeSpan.FromHours(1);

    private static readonly TimeSpan MemoryCheckInterval = TimeSpan.FromSeconds(30);

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
    private readonly Func<CancellationToken, Task<CompileCommandSources>>? sourcesProvider;
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
    private DateTime unavailableRetryAt;
    private int folderRetries;
    private string? clangdPath;
    private readonly Queue<DateTime> unexpectedExits = new();
    private int exitPauses;
    private readonly Timer memoryTimer;
    private ClangdMemoryPolicy memoryPolicy;
    // 이 Solution을 연 뒤 색인 결과를 다시 읽으려고 다시 시작한 횟수입니다(ClangdMemoryPolicy.ShouldReload).
    private int reloads;
    // 요청이 탐색기를 받아 간 마지막 시각입니다. 받아 간 뒤 탐색기에 요청을 등록하기 전의 틈에 메모리 정리가 끼지 않게 합니다.
    private DateTime lastAcquireUtc;
    private int disposed;

    /// <param name="sourcesProvider">C++ 프로젝트 목록과 활성 Solution 구성을 UI thread에서 모읍니다.</param>
    public SemanticNavigationService(SolutionFileIndexService fileIndex, SemanticNavigationSettings settings,
        Func<CancellationToken, Task<CompileCommandSources>>? sourcesProvider = null)
    {
        this.fileIndex = fileIndex;
        this.settings = settings;
        this.sourcesProvider = sourcesProvider;
        cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualBoost", "Clangd");
        memoryPolicy = new ClangdMemoryPolicy(settings.MemoryLimitBytes);
        memoryTimer = new Timer(_ => OnMemoryTimer(), null, MemoryCheckInterval, MemoryCheckInterval);
    }

    /// <summary>시작·종료·색인 진행이 바뀌었습니다. 임의 스레드에서 호출됩니다.</summary>
    public event Action? StateChanged;

    /// <summary>새 clangd 탐색기가 준비되었습니다. 활성 문서를 다시 예열할 때 씁니다. 임의 스레드에서 호출됩니다.</summary>
    public event Action? NavigatorStarted;

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
            exitPauses = 0;
            if (value.MemoryLimitMegabytes != previous.MemoryLimitMegabytes) memoryPolicy = new ClangdMemoryPolicy(value.MemoryLimitBytes);
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
            unexpectedExits.Clear();
            exitPauses = 0;
            memoryPolicy = new ClangdMemoryPolicy(settings.MemoryLimitBytes);
            reloads = 0;
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
                // 빌드 전이라 쓸 수 없던 경우와 반복 종료로 멈춘 경우는 다음 요청에서 다시 시도합니다.
                unavailableReason = null;
                exitPauses = 0;
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
                var sources = await CollectSourcesAsync(CancellationToken.None).ConfigureAwait(false);
                var context = CompileContextBuilder.Prepare(solution, cacheRoot, current.Context.EngineRoot, File.Exists(compiler) ? compiler : "clang-cl.exe",
                    CancellationToken.None, sources);
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
            lastAcquireUtc = DateTime.UtcNow;
            if (navigator is { HasExited: false } current) return current;
            if (unavailableReason is not null)
            {
                if (!unavailableRetryable || DateTime.UtcNow < unavailableRetryAt) throw new SemanticNavigationUnavailableException(unavailableReason);
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
                if (current.Context.Plan is { SwitchableUnitCount: > 0 } plan)
                {
                    var mode = plan.Mode switch { UnrealPchMode.Always => "항상", UnrealPchMode.Never => "넣지 않음", _ => "자동" };
                    lines.Add($"공유 PCH: {mode} · 포함한 색인 단위 {plan.PchUnitCount:N0}/{plan.SwitchableUnitCount:N0}개");
                }

                lines.Add("clangd: " + clangdPath + $" (PID {current.ProcessId})");
                lines.Add($"메모리 정리 기준: {memoryPolicy.EffectiveLimitBytes / (1024 * 1024):N0} MB(넘으면 탐색하지 않는 동안 다시 시작)");
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
        memoryTimer.Dispose();
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
            // 종료 뒤 늦게 도착한 다시 시작(반복 종료 대기, 메모리 정리)이 새 clangd를 띄우지 않게 합니다.
            solutionPath = null;
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
            if (starting is not null || navigator is { HasExited: false } || solutionPath is null || !settings.Enabled ||
                Volatile.Read(ref disposed) != 0)
            {
                return;
            }

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
            var sources = await CollectSourcesAsync(cancellationToken).ConfigureAwait(false);
            var created = await ClangdNavigator.StartAsync(new ClangdNavigatorOptions
            {
                Sources = sources,
                ClangdPath = executable,
                CacheRoot = cacheRoot,
                SolutionPath = solution,
                EngineRoot = FindEngineRoot(solution),
                WorkerCount = options.WorkerCount,
                PchMode = options.PchMode,
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
            // 시작 전에 들어온 활성 문서 예열은 탐색기가 없어 버려졌고, 다시 시작하면 열린 문서의 분석이 사라지므로 다시 예열합니다.
            if (!created.HasExited) NavigatorStarted?.Invoke();
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
            unavailableRetryAt = DateTime.UtcNow + RetryInterval;
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
            var paused = false;
            var pause = TimeSpan.Zero;
            string? held = null;
            SourceChangeMonitor? watcher = null;
            lock (gate)
            {
                if (ReferenceEquals(navigator, source))
                {
                    navigator = null;
                    watcher = monitor;
                    monitor = null;
                    unexpected = true;
                    var now = DateTime.UtcNow;
                    if (now - source.StartedUtc >= ExitPauseReset) exitPauses = 0;
                    unexpectedExits.Enqueue(now);
                    while (unexpectedExits.Count > 0 && now - unexpectedExits.Peek() > ExitWindow) unexpectedExits.Dequeue();
                    if (unexpectedExits.Count >= MaxUnexpectedExits)
                    {
                        unexpectedExits.Clear();
                        exitPauses++;
                        if (exitPauses > MaxExitPauses)
                        {
                            unavailableReason = "clangd가 계속 비정상 종료되어 멈췄습니다. 빌드가 끝나거나 옵션을 바꾸거나 Solution을 다시 열면 다시 시도합니다. " +
                                                $"로그: {source.LogPath}";
                            unavailableRetryable = false;
                        }
                        else
                        {
                            paused = true;
                            pause = TimeSpan.FromTicks(ExitPause.Ticks << (exitPauses - 1));
                            unavailableReason = $"clangd가 {ExitWindow.TotalMinutes:N0}분 안에 {MaxUnexpectedExits}번 종료되어 {pause.TotalMinutes:N0}분 쉰 뒤 다시 시도합니다. " +
                                                $"로그: {source.LogPath}";
                            unavailableRetryable = true;
                            unavailableRetryAt = now + pause;
                        }

                        held = unavailableReason;
                    }
                }
            }

            watcher?.Dispose();
            if (unexpected)
            {
                ActivityLog.LogWarning("VisualBoost/SemanticNavigation", "clangd가 예기치 않게 종료되었습니다. 로그: " + source.LogPath);
                if (held is not null) ActivityLog.LogWarning("VisualBoost/SemanticNavigation", held);
                source.Dispose();
                bool restart;
                int observed;
                lock (gate)
                {
                    restart = (paused || unavailableReason is null) && settings.StartOnSolutionOpen;
                    observed = generation;
                }

                if (restart)
                {
                    // 남은 색인을 이어 가도록 잠시 뒤 다시 시작합니다. 짧은 시간에 반복 종료하면 더 오래 쉰 뒤 시작합니다.
                    _ = Task.Delay(paused ? pause : TimeSpan.FromSeconds(2)).ContinueWith(_ =>
                    {
                        bool same;
                        lock (gate)
                        {
                            same = observed == generation;
                            if (same && paused) unavailableReason = null;
                        }

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

    private void OnMemoryTimer()
    {
        try
        {
            CheckMemory();
        }
        catch (Exception exception)
        {
            // 타이머 스레드에서 처리되지 않은 예외는 VS 프로세스를 끝냅니다. 이 경계에서 기록하고 다음 확인으로 넘어갑니다.
            ActivityLog.LogError("VisualBoost/SemanticNavigation", "clangd 메모리 확인 중 오류: " + exception);
        }
    }

    /// <summary>
    /// clangd가 해제한 뒤에도 쥐고 있는 메모리가 정리 기준을 넘었고 색인·요청이 없는 유휴 상태면 다시 시작해 돌려받습니다
    /// (<see cref="ClangdMemoryPolicy"/>). 공유 PCH로 다시 색인한 결과를 읽어야 할 때도 같은 방식으로 다시 시작합니다.
    /// 다시 시작한 clangd는 저장된 색인을 읽어 이어 갑니다. 타이머 스레드에서 호출됩니다.
    /// </summary>
    /// <remarks>
    /// 판단과 탐색기 떼어 내기는 같은 잠금 안에서 합니다. 표본을 잰 뒤 다른 경로(설정 변경·Solution 닫기·종료)가 탐색기를 바꿨거나
    /// 새 요청이 탐색기를 받아 갔으면(<see cref="lastAcquireUtc"/>) 이번에는 정리하지 않아, 받아 간 요청이 정리에 끊기지 않습니다.
    /// </remarks>
    private void CheckMemory()
    {
        ClangdNavigator? current;
        int observed;
        DateTime acquired;
        lock (gate)
        {
            current = navigator;
            observed = generation;
            acquired = lastAcquireUtc;
        }

        if (current is not { HasExited: false } || Volatile.Read(ref disposed) != 0) return;
        long bytes;
        try
        {
            using var process = Process.GetProcessById(current.ProcessId);
            bytes = process.PrivateMemorySize64;
        }
        catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException ||
                                          exception is System.ComponentModel.Win32Exception)
        {
            // 확인하는 사이 종료된 경우입니다. 종료 처리는 Changed 알림이 맡습니다.
            return;
        }

        var now = DateTime.UtcNow;
        // 탐색기를 받아 간 요청은 탐색기에 등록하기 전이라도 최근 요청으로 봅니다.
        var lastRequest = current.LastRequestUtc > acquired ? current.LastRequestUtc : acquired;
        var sample = new ClangdMemorySample(bytes, current.Progress.Active, current.IsBusy, now - lastRequest, now - current.StartedUtc);
        var needsReload = current.NeedsReload;
        long limit;
        bool reload;
        ClangdNavigator? stopped;
        SourceChangeMonitor? watcher;
        lock (gate)
        {
            if (observed != generation || !ReferenceEquals(navigator, current) || lastAcquireUtc != acquired || Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            // 색인 결과 다시 읽기를 먼저 봅니다. 메모리 정책의 판단은 다시 시작을 정하면 상태가 바뀌므로 필요할 때만 묻습니다.
            reload = ClangdMemoryPolicy.ShouldReload(needsReload, sample, reloads);
            if (!reload && !memoryPolicy.ShouldRestart(sample))
            {
                return;
            }

            if (reload) reloads++;
            limit = memoryPolicy.EffectiveLimitBytes;
            (stopped, watcher) = DetachLocked(null);
        }

        ActivityLog.LogInformation("VisualBoost/SemanticNavigation", reload
            ? "공유 PCH를 넣어 다시 색인한 결과를 읽도록 clangd를 다시 시작합니다."
            : $"clangd 메모리 {bytes / (1024 * 1024):N0} MB가 정리 기준 {limit / (1024 * 1024):N0} MB를 넘어 유휴 상태에서 다시 시작합니다.");
        Release(stopped, watcher);
        BeginStart();
    }

    private void Stop(string? reason)
    {
        ClangdNavigator? current;
        SourceChangeMonitor? watcher;
        lock (gate) (current, watcher) = DetachLocked(reason);
        Release(current, watcher);
    }

    /// <summary>현재 탐색기와 감시를 떼어 냅니다. <see cref="gate"/> 안에서 호출하고, 돌려받은 것은 잠금 밖에서 <see cref="Release"/>합니다.</summary>
    private (ClangdNavigator? Navigator, SourceChangeMonitor? Monitor) DetachLocked(string? reason)
    {
        generation++;
        startCancellation?.Cancel();
        starting = null;
        var current = navigator;
        navigator = null;
        var watcher = monitor;
        monitor = null;
        unavailableReason = reason;
        return (current, watcher);
    }

    private void Release(ClangdNavigator? current, SourceChangeMonitor? watcher)
    {
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

    /// <summary>Solution 정보(UI thread)와 현재 VS의 빌드 도구 경로를 묶습니다.</summary>
    private async Task<CompileCommandSources> CollectSourcesAsync(CancellationToken cancellationToken)
    {
        var sources = sourcesProvider is null ? new CompileCommandSources() : await sourcesProvider(cancellationToken).ConfigureAwait(false);
        sources.MsBuildPath = BuildToolLocator.FindMsBuild();
        sources.NinjaPath = BuildToolLocator.FindNinja();
        return sources;
    }

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
