using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>잠잠해진 뒤 한 번에 전달하는 C++ 소스 변경 묶음입니다.</summary>
public sealed class SourceChangeBatch
{
    public SourceChangeBatch(IReadOnlyList<string> paths, bool requiresRestart)
    {
        Paths = paths;
        RequiresRestart = requiresRestart;
    }

    /// <summary>내용이 바뀌었거나 새로 생긴 C++ 소스입니다.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>
    /// 소스·폴더가 사라졌거나 감시 버퍼가 넘쳤습니다. 색인에서 지울 방법이 재시작뿐이라 다시 시작해야 합니다.
    /// </summary>
    public bool RequiresRestart { get; }
}

/// <summary>
/// 편집기 밖에서 바뀐 C++ 소스(브랜치 전환, 외부 도구 등)를 감시해 잠잠해진 뒤 묶어서 알립니다.
/// </summary>
/// <remarks>
/// clangd는 파일 감시 통지만으로 닫힌 파일을 다시 색인하지 않으므로 호출자가 내용을 열었다 닫거나 다시 시작해야 합니다.
/// 임시 파일로 쓴 뒤 바꿔치기하는 저장은 삭제·이름 변경 알림이 함께 오므로, 삭제는 알릴 때 실제로 없는지 다시 확인합니다.
/// 빌드 산출물·캐시 폴더는 무시합니다. 콜백은 타이머 스레드에서 호출되며 예외를 던지면 안 됩니다.
/// </remarks>
public sealed class SourceChangeMonitor : IDisposable
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".h", ".hh", ".hpp", ".hxx", ".inl", ".ipp", ".c", ".cc", ".cpp", ".cxx"
    };

    private static readonly string[] IgnoredSegments =
    {
        "Intermediate", "Binaries", "Saved", "DerivedDataCache", "CMakeFiles", ".vs", ".git", "node_modules"
    };

    private readonly object gate = new();
    private readonly FileSystemWatcher watcher;
    private readonly Timer timer;
    private readonly TimeSpan quiet;
    private readonly TimeSpan maxDelay;
    private readonly Action<SourceChangeBatch> callback;
    private readonly HashSet<string> changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);
    private long pendingSince;
    private bool overflow;
    private int disposed;

    /// <param name="root">감시할 폴더(하위 포함). 없으면 <see cref="ArgumentException"/>입니다.</param>
    /// <param name="quiet">마지막 변경 뒤 이만큼 잠잠하면 알립니다. 변경이 계속돼도 이 값의 5배를 넘겨 미루지 않습니다.</param>
    public SourceChangeMonitor(string root, TimeSpan quiet, Action<SourceChangeBatch> changed)
    {
        this.quiet = quiet;
        maxDelay = TimeSpan.FromTicks(quiet.Ticks * 5);
        callback = changed ?? throw new ArgumentNullException(nameof(changed));
        timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024
        };
        watcher.Changed += (_, e) => Record(e.FullPath, false);
        watcher.Created += (_, e) => Record(e.FullPath, false);
        watcher.Deleted += (_, e) => Record(e.FullPath, true);
        watcher.Renamed += (_, e) =>
        {
            Record(e.OldFullPath, true);
            Record(e.FullPath, false);
        };
        watcher.Error += (_, _) => MarkOverflow();
        watcher.EnableRaisingEvents = true;
    }

    /// <summary>C++ 소스 확장자이고 무시할 폴더 아래가 아닌 경로입니다.</summary>
    public static bool IsSourcePath(string path) => SourceExtensions.Contains(Path.GetExtension(path)) && !IsIgnored(path);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        timer.Dispose();
    }

    private static bool IsIgnored(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => IgnoredSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private void Record(string path, bool gone)
    {
        // 폴더를 지우거나 옮기면 안의 파일은 따로 알리지 않습니다. 확장자 없는 경로의 삭제는 폴더로 봅니다.
        var folder = gone && Path.GetExtension(path).Length == 0;
        if (!(folder ? !IsIgnored(path) : IsSourcePath(path))) return;
        lock (gate)
        {
            if (gone) removed.Add(path);
            else changed.Add(path);
            if (pendingSince == 0) pendingSince = Stopwatch.GetTimestamp();
        }

        Schedule();
    }

    private void MarkOverflow()
    {
        lock (gate)
        {
            overflow = true;
            if (pendingSince == 0) pendingSince = Stopwatch.GetTimestamp();
        }

        Schedule();
    }

    private void Schedule()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        TimeSpan wait;
        lock (gate)
        {
            // 브랜치 전환처럼 이어지는 변경을 한 번에 처리하도록 잠잠해질 때까지 미루되, 계속 바뀌어도 상한을 넘기지 않습니다.
            var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - pendingSince) / (double)Stopwatch.Frequency);
            var remaining = maxDelay - elapsed;
            wait = remaining < quiet ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : quiet;
        }

        try
        {
            timer.Change(wait, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Flush()
    {
        SourceChangeBatch batch;
        lock (gate)
        {
            if (changed.Count == 0 && removed.Count == 0 && !overflow) return;
            var restart = overflow;
            foreach (var path in removed)
            {
                // 임시 파일 교체 저장이면 이미 같은 경로에 다시 있습니다.
                if (File.Exists(path)) changed.Add(path);
                else restart = true;
            }

            batch = new SourceChangeBatch(changed.Where(File.Exists).ToArray(), restart);
            changed.Clear();
            removed.Clear();
            overflow = false;
            pendingSince = 0;
        }

        if (Volatile.Read(ref disposed) == 0 && (batch.Paths.Count > 0 || batch.RequiresRestart)) callback(batch);
    }
}
