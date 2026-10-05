using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VisualBoost.Core.SemanticNavigation;

public sealed class ClangdLaunchOptions
{
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>compile_commands.json이 있는 폴더. clangd는 이 폴더 아래 .cache에 색인을 저장합니다.</summary>
    public string CompileCommandsDirectory { get; set; } = string.Empty;

    /// <summary>0이면 논리 코어 수의 1/4(최소 1)입니다. TU 하나가 수백 MiB~1.4 GiB를 쓰므로 보수적으로 잡습니다.</summary>
    public int WorkerCount { get; set; }

    /// <summary>참조 응답 상한. 결과가 상한과 같으면 잘렸을 수 있다고 표시합니다.</summary>
    public int ReferenceLimit { get; set; } = 5000;

    /// <summary>clangd stderr를 남길 파일. null이면 버립니다.</summary>
    public string? LogFilePath { get; set; }

    public static int ResolveWorkerCount(int requested) =>
        requested > 0 ? requested : Math.Max(1, Environment.ProcessorCount / 4);
}

/// <summary>clangd background index의 진행 상태입니다. 진행 알림이 한 번도 없으면 <see cref="Started"/>가 false입니다.</summary>
public readonly struct BackgroundIndexProgress
{
    public BackgroundIndexProgress(bool started, bool active, int done, int total)
    {
        Started = started;
        Active = active;
        Done = done;
        Total = total;
    }

    public bool Started { get; }

    public bool Active { get; }

    public int Done { get; }

    public int Total { get; }

    /// <summary>시작 후 마지막 작업 묶음이 끝났습니다. 새 변경이 들어오면 다시 Active가 됩니다.</summary>
    public bool Completed => Started && !Active;
}

/// <summary>clangd <c>textDocument/symbolInfo</c> 확장 응답의 일부입니다.</summary>
public sealed class SemanticSymbol
{
    public SemanticSymbol(string name, string containerName, string usr)
    {
        Name = name;
        ContainerName = containerName;
        Usr = usr;
    }

    public string Name { get; }

    /// <summary>`A::B::` 형태의 소속 이름(끝의 `::` 제외). 전역이면 빈 문자열입니다.</summary>
    public string ContainerName { get; }

    public string Usr { get; }

    public string QualifiedName => ContainerName.Length == 0 ? Name : ContainerName + "::" + Name;
}

/// <summary>
/// 수정하지 않은 clangd 프로세스 하나와의 LSP 세션입니다.
/// </summary>
/// <remarks>
/// 이 클래스가 시작한 프로세스만 종료합니다. 문서 버전은 호출자가 관리하며, 세션이 재시작되면
/// 호출자가 열린 문서를 다시 보내야 합니다. 모든 공개 메서드는 임의 스레드에서 호출할 수 있고
/// 블로킹 대기 없이 Task를 돌려줍니다. 단, <see cref="Start"/>는 프로세스를 띄우므로 UI thread에서 호출하지 않습니다.
/// </remarks>
public sealed class ClangdSession : IDisposable
{
    private const string BackgroundIndexToken = "backgroundIndexProgress";
    private static readonly string[] IsolatedEnvironment = { "INCLUDE", "LIB", "LIBPATH", "CL", "_CL_", "EXTERNAL_INCLUDE" };

    private readonly Process process;
    private readonly LspConnection connection;
    private readonly ClangdLaunchOptions options;
    private readonly object stateLock = new();
    private readonly Dictionary<string, int> diagnosticsVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DiagnosticsWaiter> diagnosticsWaiters = new();
    private readonly StreamWriter? log;
    private long logBytes;
    private BackgroundIndexProgress progress;
    private int disposed;
    private int exitRaised;

    private ClangdSession(Process process, ClangdLaunchOptions options, StreamWriter? log)
    {
        this.process = process;
        this.options = options;
        this.log = log;
        connection = new LspConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        connection.Notification += OnNotification;
        connection.Closed += OnClosed;
        connection.ServerRequestHandler = OnServerRequest;
    }

    public event Action? ProgressChanged;

    /// <summary>프로세스나 연결이 끝났습니다. 의도한 종료에도 호출됩니다.</summary>
    public event Action<Exception?>? Exited;

    public BackgroundIndexProgress Progress
    {
        get
        {
            lock (stateLock) return progress;
        }
    }

    public bool HasExited => connection.Failure is not null || SafeHasExited();

    public int ProcessId { get; private set; }

    public int ReferenceLimit => options.ReferenceLimit;

    public static ClangdSession Start(ClangdLaunchOptions options)
    {
        if (!File.Exists(options.ExecutablePath))
        {
            throw new FileNotFoundException("clangd 실행 파일이 없습니다.", options.ExecutablePath);
        }

        if (!File.Exists(Path.Combine(options.CompileCommandsDirectory, "compile_commands.json")))
        {
            throw new FileNotFoundException("compile_commands.json이 없습니다.", options.CompileCommandsDirectory);
        }

        var arguments = new[]
        {
            "--background-index",
            "--background-index-priority=low",
            "--clang-tidy=false",
            "--header-insertion=never",
            "--enable-config=false",
            "--log=error",
            "--limit-references=" + options.ReferenceLimit.ToString(CultureInfo.InvariantCulture),
            "-j=" + ClangdLaunchOptions.ResolveWorkerCount(options.WorkerCount).ToString(CultureInfo.InvariantCulture),
            "--compile-commands-dir=" + options.CompileCommandsDirectory
        };
        var info = new ProcessStartInfo(options.ExecutablePath, string.Join(" ", arguments.Select(QuoteArgument)))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = options.CompileCommandsDirectory
        };
        // VS를 개발자 프롬프트에서 띄운 경우의 INCLUDE 등이 헤더 선택을 바꾸므로 비워 결정적으로 만듭니다.
        foreach (var name in IsolatedEnvironment)
        {
            info.EnvironmentVariables.Remove(name);
        }

        StreamWriter? log = null;
        if (!string.IsNullOrEmpty(options.LogFilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.LogFilePath)!);
            log = new StreamWriter(new FileStream(options.LogFilePath!, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch
        {
            log?.Dispose();
            process.Dispose();
            throw;
        }

        var session = new ClangdSession(process, options, log) { ProcessId = process.Id };
        process.ErrorDataReceived += session.OnStandardError;
        process.Exited += (_, _) => session.OnClosed(null);
        process.BeginErrorReadLine();
        session.connection.Start();
        return session;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var capabilities = JsonValue.Object(
            ("window", JsonValue.Object(("workDoneProgress", true))),
            ("general", JsonValue.Object(("positionEncodings", JsonValue.Array("utf-16")))),
            ("textDocument", JsonValue.Object(
                ("synchronization", JsonValue.Object(("didSave", true))),
                ("publishDiagnostics", JsonValue.Object(("versionSupport", true))),
                ("definition", JsonValue.Object(("linkSupport", false))),
                ("declaration", JsonValue.Object(("linkSupport", false))),
                // clangd 확장: 참조마다 들어 있는 함수·클래스 이름(containerName)을 받습니다.
                ("references", JsonValue.Object(("container", true))))));
        await connection.RequestAsync("initialize", JsonValue.Object(
            ("processId", CurrentProcessId),
            ("rootUri", JsonValue.Null),
            ("capabilities", capabilities),
            ("initializationOptions", JsonValue.Object())), cancellationToken).ConfigureAwait(false);
        connection.Notify("initialized", JsonValue.Object());
    }

    public void OpenDocument(string path, string text, int version) =>
        connection.Notify("textDocument/didOpen", JsonValue.Object(("textDocument", JsonValue.Object(
            ("uri", DocumentUri.FromPath(path)), ("languageId", LanguageOf(path)), ("version", version), ("text", text)))));

    /// <summary>전체 내용 동기화입니다. 증분 범위 계산 오류로 서버 상태가 어긋나는 위험을 피합니다.</summary>
    public void ChangeDocument(string path, string text, int version) =>
        connection.Notify("textDocument/didChange", JsonValue.Object(
            ("textDocument", JsonValue.Object(("uri", DocumentUri.FromPath(path)), ("version", version))),
            ("contentChanges", JsonValue.Array(JsonValue.Object(("text", text)))),
            ("wantDiagnostics", true)));

    public void SaveDocument(string path) =>
        connection.Notify("textDocument/didSave", JsonValue.Object(("textDocument", JsonValue.Object(("uri", DocumentUri.FromPath(path))))));

    public void CloseDocument(string path)
    {
        lock (stateLock) diagnosticsVersions.Remove(path);
        connection.Notify("textDocument/didClose", JsonValue.Object(("textDocument", JsonValue.Object(("uri", DocumentUri.FromPath(path))))));
    }

    /// <summary>
    /// 특정 파일의 명령을 공급하거나 바꿉니다. 바뀐 파일은 background index가 다시 처리합니다.
    /// 같은 명령을 다시 보내면 무시되므로 외부 변경 반영 수단으로 쓰지 않습니다.
    /// </summary>
    public void UpdateCompileCommands(IEnumerable<CompileCommand> commands) =>
        connection.Notify("workspace/didChangeConfiguration", JsonValue.Object(("settings", JsonValue.Object(
            ("compilationDatabaseChanges", JsonValue.Object(commands.Select(c => new KeyValuePair<string, JsonValue>(c.File,
                JsonValue.Object(("workingDirectory", c.Directory), ("compilationCommand", JsonValue.Array(c.Arguments.Select(a => (JsonValue)a))))))))))));

    /// <summary>해당 문서의 진단(= AST 준비)이 <paramref name="minimumVersion"/> 이상으로 도착할 때까지 기다립니다.</summary>
    public Task WaitForDiagnosticsAsync(string path, int minimumVersion, CancellationToken cancellationToken)
    {
        lock (stateLock)
        {
            if (diagnosticsVersions.TryGetValue(path, out var version) && version >= minimumVersion)
            {
                return Task.CompletedTask;
            }

            var waiter = new DiagnosticsWaiter(path, minimumVersion);
            diagnosticsWaiters.Add(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    lock (stateLock) diagnosticsWaiters.Remove(waiter);
                    waiter.Completion.TrySetCanceled(cancellationToken);
                });
            }

            return waiter.Completion.Task;
        }
    }

    public async Task<IReadOnlyList<NavigationLocation>> DefinitionAsync(string path, int line, int character, CancellationToken cancellationToken) =>
        NavigationLocation.FromLsp(await connection.RequestAsync("textDocument/definition", Position(path, line, character), cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<NavigationLocation>> DeclarationAsync(string path, int line, int character, CancellationToken cancellationToken) =>
        NavigationLocation.FromLsp(await connection.RequestAsync("textDocument/declaration", Position(path, line, character), cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<NavigationLocation>> ReferencesAsync(string path, int line, int character, bool includeDeclaration, CancellationToken cancellationToken)
    {
        var parameters = JsonValue.Object(
            ("textDocument", JsonValue.Object(("uri", DocumentUri.FromPath(path)))),
            ("position", JsonValue.Object(("line", line), ("character", character))),
            ("context", JsonValue.Object(("includeDeclaration", includeDeclaration))));
        return NavigationLocation.FromLsp(await connection.RequestAsync("textDocument/references", parameters, cancellationToken).ConfigureAwait(false));
    }

    public async Task<SemanticSymbol?> SymbolInfoAsync(string path, int line, int character, CancellationToken cancellationToken)
    {
        var result = await connection.RequestAsync("textDocument/symbolInfo", Position(path, line, character), cancellationToken).ConfigureAwait(false);
        var first = result.Items.FirstOrDefault();
        if (first is null || first["name"].AsString() is not string name)
        {
            return null;
        }

        var container = (first["containerName"].AsString() ?? string.Empty).TrimEnd(':');
        return new SemanticSymbol(name, container, first["usr"].AsString() ?? string.Empty);
    }

    /// <summary>정상 종료를 요청하고, 시간 안에 끝나지 않으면 이 세션이 띄운 프로세스만 종료합니다.</summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        if (!HasExited)
        {
            try
            {
                using var cancellation = new CancellationTokenSource(timeout);
                await connection.RequestAsync("shutdown", null, cancellation.Token).ConfigureAwait(false);
                connection.Notify("exit", null);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException ||
                exception is LspConnectionClosedException ||
                exception is LspRequestException)
            {
                // 응답하지 않는 서버는 아래에서 종료합니다.
            }
        }

        await Task.Run(() =>
        {
            if (!SafeHasExited() && !process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                Kill();
            }
        }).ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        connection.Dispose();
        Kill();
        try
        {
            process.CancelErrorRead();
        }
        catch (InvalidOperationException)
        {
            // 이미 읽기가 끝났습니다.
        }

        process.Dispose();
        lock (stateLock)
        {
            log?.Dispose();
            foreach (var waiter in diagnosticsWaiters)
            {
                waiter.Completion.TrySetException(new LspConnectionClosedException("clangd 세션이 끝났습니다."));
            }

            diagnosticsWaiters.Clear();
        }
    }

    private static readonly int CurrentProcessId = GetCurrentProcessId();

    private static int GetCurrentProcessId()
    {
        using var current = Process.GetCurrentProcess();
        return current.Id;
    }

    private static JsonValue Position(string path, int line, int character) => JsonValue.Object(
        ("textDocument", JsonValue.Object(("uri", DocumentUri.FromPath(path)))),
        ("position", JsonValue.Object(("line", line), ("character", character))));

    private static string LanguageOf(string path) =>
        Path.GetExtension(path).Equals(".c", StringComparison.OrdinalIgnoreCase) ? "c" : "cpp";

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return argument;
        }

        // Windows 명령줄 규칙: 따옴표 앞 역슬래시는 두 배, 끝의 역슬래시도 두 배로 둡니다.
        var builder = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            builder.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', slashes * 2).Append('"');
        return builder.ToString();
    }

    private JsonValue? OnServerRequest(string method, JsonValue parameters) => method switch
    {
        "window/workDoneProgress/create" => JsonValue.Null,
        "client/registerCapability" => JsonValue.Null,
        "workspace/configuration" => JsonValue.Array(parameters["items"].Items.Select(_ => JsonValue.Null)),
        _ => null
    };

    private void OnNotification(string method, JsonValue parameters)
    {
        if (method == "$/progress" && parameters["token"].AsString() == BackgroundIndexToken)
        {
            var value = parameters["value"];
            BackgroundIndexProgress next;
            lock (stateLock)
            {
                next = value["kind"].AsString() switch
                {
                    "begin" => new BackgroundIndexProgress(true, true, 0, 0),
                    "report" => ParseReport(value["message"].AsString(), progress),
                    "end" => new BackgroundIndexProgress(true, false, progress.Total, progress.Total),
                    _ => progress
                };
                progress = next;
            }

            ProgressChanged?.Invoke();
            return;
        }

        if (method == "textDocument/publishDiagnostics" && DocumentUri.ToPath(parameters["uri"].AsString() ?? string.Empty) is string path)
        {
            // 버전 없는 진단은 열린 문서가 아니므로 0으로 취급합니다.
            var version = parameters["version"].AsInt32() ?? 0;
            List<DiagnosticsWaiter> ready;
            lock (stateLock)
            {
                diagnosticsVersions[path] = Math.Max(version, diagnosticsVersions.TryGetValue(path, out var known) ? known : int.MinValue);
                ready = diagnosticsWaiters.Where(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase) && version >= w.MinimumVersion).ToList();
                foreach (var waiter in ready) diagnosticsWaiters.Remove(waiter);
            }

            foreach (var waiter in ready) waiter.Completion.TrySetResult(true);
        }
    }

    private static BackgroundIndexProgress ParseReport(string? message, BackgroundIndexProgress previous)
    {
        // clangd는 "완료/전체" 형식의 메시지를 보냅니다. 형식이 다르면 이전 개수를 유지합니다.
        var parts = (message ?? string.Empty).Split('/');
        if (parts.Length == 2 &&
            int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var done) &&
            int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var total))
        {
            return new BackgroundIndexProgress(true, true, done, total);
        }

        return new BackgroundIndexProgress(true, true, previous.Done, previous.Total);
    }

    private void OnStandardError(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null || log is null)
        {
            return;
        }

        lock (stateLock)
        {
            // 로그가 무한히 커지지 않게 상한(8 MiB) 이후는 버립니다.
            if (Volatile.Read(ref disposed) != 0 || logBytes > 8 * 1024 * 1024) return;
            logBytes += e.Data.Length + 2;
            log.WriteLine(e.Data);
        }
    }

    private void OnClosed(Exception? exception)
    {
        // 연결 종료와 프로세스 종료 알림이 모두 오므로 한 번만 전달합니다.
        if (Interlocked.Exchange(ref exitRaised, 1) != 0)
        {
            return;
        }

        List<DiagnosticsWaiter> waiters;
        lock (stateLock)
        {
            waiters = diagnosticsWaiters.ToList();
            diagnosticsWaiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(new LspConnectionClosedException("clangd 세션이 끝났습니다.", exception));
        }

        Exited?.Invoke(exception);
    }

    private bool SafeHasExited()
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void Kill()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is System.ComponentModel.Win32Exception)
        {
            // 이미 끝났거나 종료 중입니다.
        }
    }

    private sealed class DiagnosticsWaiter
    {
        public DiagnosticsWaiter(string path, int minimumVersion)
        {
            Path = path;
            MinimumVersion = minimumVersion;
        }

        public string Path { get; }

        public int MinimumVersion { get; }

        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
