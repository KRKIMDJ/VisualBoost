using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.SemanticNavigation;

public sealed class ClangdLaunchOptions
{
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>compile_commands.json이 있는 폴더. clangd는 이 폴더 아래 .cache에 색인을 저장합니다.</summary>
    public string CompileCommandsDirectory { get; set; } = string.Empty;

    /// <summary>0이면 <see cref="DefaultWorkerCount"/>로 정합니다.</summary>
    public int WorkerCount { get; set; }

    /// <summary>참조 응답 상한. 결과가 상한과 같으면 잘렸을 수 있다고 표시합니다.</summary>
    public int ReferenceLimit { get; set; } = 5000;

    /// <summary>clangd stderr를 남길 파일. null이면 버립니다.</summary>
    public string? LogFilePath { get; set; }

    /// <summary>문서 URI·명령은 실제 경로로 보내고 받은 위치·진단은 연 경로로 되돌리는 대응입니다.</summary>
    public PathAliases Paths { get; set; } = PathAliases.None;

    public static int ResolveWorkerCount(int requested) =>
        requested > 0 ? requested : DefaultWorkerCount(Environment.ProcessorCount, PhysicalMemory.TotalBytes());

    /// <summary>
    /// 작업 2개를 쓰되, 논리 코어가 8개 미만이거나 VS·빌드 몫 8 GiB를 남기고 작업 하나에 2.5 GiB를 잡을 메모리가 없으면 1개를 씁니다.
    /// </summary>
    /// <remarks>
    /// Unreal unity 묶음 색인과 자동 공유 PCH(0.45.0) 뒤 테스트 전용 UE 5.8 샘플(TU 305개, 16스레드·64 GB PC)의 첫 색인은 작업 1·2·4·6개에서
    /// 36·27·21·20초, 최고 메모리는 0.7·1.2·2.4·3.6 GB였습니다(2026-10-09). 작업 2개를 넘기면 빨라지는 몫보다 메모리가 크게 늘고, 색인 중
    /// 메모리와 발열이 크다는 회사 사용 피드백(2026-10-08)과 색인 최고 2 GB 목표에 맞춰 2개를 기본으로 합니다. 더 빠른 첫 색인이 필요하면
    /// 옵션으로 늘립니다. 공유 PCH를 넣는 단위는 작업 하나에 약 2.2 GB가 들어 작업당 2.5 GiB 가정을 유지합니다.
    /// </remarks>
    public static int DefaultWorkerCount(int processors, long memoryBytes)
    {
        var byCores = processors >= 8 ? 2 : 1;
        if (memoryBytes <= 0) return byCores;
        var gib = memoryBytes / (1024d * 1024 * 1024);
        var byMemory = (int)Math.Floor((gib - 8) / 2.5);
        return Math.Max(1, Math.Min(byCores, byMemory));
    }
}

/// <summary>물리 메모리 크기를 읽습니다. Windows가 아니거나 읽지 못하면 0입니다.</summary>
internal static class PhysicalMemory
{
    public static long TotalBytes()
    {
        try
        {
            var status = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemoryStatus)) };
            return GlobalMemoryStatusEx(ref status) ? (long)Math.Min(status.TotalPhysical, long.MaxValue) : 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException || exception is EntryPointNotFoundException)
        {
            return 0;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

/// <summary>
/// 열린 문서의 최근 진단 중 오류 요약입니다. 탐색 결과가 비었을 때 심볼이 정말 없는지, 컴파일 문맥이 깨져(필요한 헤더 누락 등)
/// 분석하지 못했는지 사용자가 구분하도록 첫 오류를 함께 보입니다.
/// </summary>
public sealed class DocumentErrors
{
    public DocumentErrors(int count, int firstLine, string firstMessage)
    {
        Count = count;
        FirstLine = firstLine;
        FirstMessage = firstMessage;
    }

    public int Count { get; }

    /// <summary>문서에서 가장 앞에 있는 오류의 줄(0부터)입니다. 뒤 오류는 앞 오류에서 번진 경우가 많습니다.</summary>
    public int FirstLine { get; }

    /// <summary>그 오류 메시지의 첫 줄입니다.</summary>
    public string FirstMessage { get; }

    /// <summary>
    /// 조건식(<c>#if</c>)에 정의되지 않은 매크로를 썼다는 진단이 있는지 봅니다. clangd는 경고 옵션이 있는 진단의 코드를 옵션 이름(<c>-Wundef</c>)으로,
    /// 오류로 올린 경우(<c>-Werror=undef</c>)도 같은 이름으로 보냅니다(clangd 22.1 확인). 옵션 없이 진단 이름만 보내는 경우를 위해
    /// <c>pp_undef_identifier</c>도 받습니다.
    /// </summary>
    public static bool HasUndefinedConditionMacro(IReadOnlyList<JsonValue> diagnostics) =>
        diagnostics.Any(d => d["code"].AsString() is "-Wundef" or "pp_undef_identifier");

    /// <summary>JSON 진단 배열에서 오류(severity 1)만 요약합니다. 오류가 없으면 null입니다.</summary>
    public static DocumentErrors? From(IReadOnlyList<JsonValue> diagnostics)
    {
        var errors = diagnostics.Where(d => d["severity"].AsInt32() == 1).ToArray();
        if (errors.Length == 0) return null;
        var first = errors.OrderBy(d => d["range"]["start"]["line"].AsInt32() ?? int.MaxValue).First();
        var message = (first["message"].AsString() ?? string.Empty).Split('\n')[0].Trim();
        return new DocumentErrors(errors.Length, first["range"]["start"]["line"].AsInt32() ?? 0, message);
    }
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
    public SemanticSymbol(string name, string containerName, string usr, NavigationLocation? declaration = null, NavigationLocation? definition = null,
        IReadOnlyList<string>? ids = null, IReadOnlyList<string>? usrs = null, string? id = null, NavigationLocation? primaryDeclaration = null,
        IReadOnlyList<string>? overloadIds = null)
    {
        Id = id ?? string.Empty;
        OverloadIds = overloadIds ?? Array.Empty<string>();
        PrimaryDeclaration = primaryDeclaration ?? declaration;
        Name = name;
        ContainerName = containerName;
        Usr = usr;
        Declaration = declaration;
        Definition = definition;
        Ids = ids ?? Array.Empty<string>();
        Usrs = usrs ?? (usr.Length > 0 ? new[] { usr } : Array.Empty<string>());
    }

    /// <summary>
    /// 응답의 모든 항목의 심볼 ID(대문자 16진수)입니다. clangd는 별칭의 대상·using 선언의 대상·위치의 매크로도 함께 돌려줍니다.
    /// </summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary>응답의 모든 항목의 USR입니다(응답 순서).</summary>
    public IReadOnlyList<string> Usrs { get; }

    /// <summary>대표 항목의 심볼 ID(대문자 16진수)입니다. clangd 색인 파일의 심볼 ID와 같은 표기이며, 없으면 빈 문자열입니다.</summary>
    public string Id { get; }

    /// <summary>
    /// 오버로드를 정하지 못한 위치(템플릿 안의 의존 호출 <c>Forward&lt;Args&gt;(args)</c>, 여러 오버로드를 가리키는 using 선언)에서
    /// symbolInfo가 함께 돌려준 같은 이름 함수 후보들의 ID입니다. 후보가 하나뿐이면 빈 목록입니다.
    /// </summary>
    public IReadOnlyList<string> OverloadIds { get; }

    /// <summary>요청 파일 AST가 아는 대표 선언 위치(<c>declarationRange</c>)입니다. 응답에 없거나 심볼이 여럿이면 null입니다.</summary>
    public NavigationLocation? Declaration { get; }

    /// <summary>
    /// 대표 항목의 선언 위치(<c>declarationRange</c>)입니다. <see cref="Declaration"/>과 달리 심볼이 여럿이어도 대표 항목의 것을 씁니다.
    /// 응답에 없으면 null입니다.
    /// </summary>
    public NavigationLocation? PrimaryDeclaration { get; }

    /// <summary>요청 파일 AST가 아는 정의 위치(<c>definitionRange</c>)입니다. 정의가 다른 번역 단위에만 있으면 null입니다.</summary>
    public NavigationLocation? Definition { get; }

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
    internal static readonly string[] IsolatedEnvironment = { "INCLUDE", "LIB", "LIBPATH", "CL", "_CL_", "EXTERNAL_INCLUDE" };

    private readonly Process process;
    private readonly LspConnection connection;
    private readonly ClangdLaunchOptions options;
    private readonly object stateLock = new();
    private readonly Dictionary<string, int> diagnosticsVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DocumentErrors> documentErrors = new(StringComparer.OrdinalIgnoreCase);
    // 열린 문서의 최근 진단 중 모르는 이름의 위치입니다(공유 PCH 대신 헤더 보충, IncludeSupplements).
    private readonly Dictionary<string, IReadOnlyList<DiagnosticNameSite>> missingNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DiagnosticsWaiter> diagnosticsWaiters = new();
    // 연 문서입니다. 닫을 때 clangd가 보내는 빈 진단을 DiagnosticsPublished로 알리지 않으려고 둡니다.
    private readonly HashSet<string> openDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly StreamWriter? log;
    private long logBytes;
    private bool keepingMessage = true;
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

    /// <summary>
    /// background index가 분석 오류가 있는 TU를 색인했습니다(그 TU의 색인은 불완전할 수 있음). 인수는 연 경로 기준 TU 경로입니다.
    /// clangd stderr 읽기 스레드에서 호출되므로 오래 걸리는 일을 하지 않습니다.
    /// </summary>
    public event Action<string>? IndexFailed;

    /// <summary>
    /// background index가 TU 하나의 색인을 마쳤습니다. 인수는 연 경로 기준 TU 경로입니다. 분석 오류가 있었으면 <see cref="IndexFailed"/>가
    /// 먼저 호출됩니다. clangd stderr 읽기 스레드에서 호출되므로 오래 걸리는 일을 하지 않습니다.
    /// </summary>
    public event Action<string>? TranslationUnitIndexed;

    /// <summary>
    /// 열린 문서의 최신 진단이 도착했습니다. 인수는 연 경로, 오류 요약(없으면 null), 조건식에 정의되지 않은 매크로를 쓴 경고(<c>-Wundef</c>)가
    /// 있는지입니다. 그 버전을 기다리는 호출자보다 먼저 호출됩니다.
    /// LSP 읽기 스레드에서 호출되므로 잠금을 오래 잡거나 clangd에 쓰지 않습니다. 쓰기는 동기식이라 clangd 출력이 차 있으면 서로 기다립니다.
    /// </summary>
    public event Action<string, DocumentErrors?, bool>? DiagnosticsPublished;

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
            // 색인 실패(분석 오류) 알림이 info 수준이라 받습니다. 파일에는 오류와 색인 실패만 남깁니다(OnStandardError).
            "--log=info",
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
                // 관련 정보를 따로 받으면 include한 헤더에서 난 오류의 실제 위치를 메시지 해석 없이 얻습니다(IncludeSupplements).
                ("publishDiagnostics", JsonValue.Object(("versionSupport", true), ("relatedInformation", true))),
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

    public void OpenDocument(string path, string text, int version)
    {
        lock (stateLock) openDocuments.Add(path);
        connection.Notify("textDocument/didOpen", JsonValue.Object(("textDocument", JsonValue.Object(
            ("uri", ToUri(path)), ("languageId", LanguageOf(path)), ("version", version), ("text", text)))));
    }

    /// <summary>전체 내용 동기화입니다. 증분 범위 계산 오류로 서버 상태가 어긋나는 위험을 피합니다.</summary>
    public void ChangeDocument(string path, string text, int version) =>
        connection.Notify("textDocument/didChange", JsonValue.Object(
            ("textDocument", JsonValue.Object(("uri", ToUri(path)), ("version", version))),
            ("contentChanges", JsonValue.Array(JsonValue.Object(("text", text)))),
            ("wantDiagnostics", true)));

    public void SaveDocument(string path) =>
        connection.Notify("textDocument/didSave", JsonValue.Object(("textDocument", JsonValue.Object(("uri", ToUri(path))))));

    /// <summary>열린 문서의 최근 진단 중 오류 요약입니다. 오류가 없거나 진단 전이면 null입니다.</summary>
    public DocumentErrors? ErrorsOf(string path)
    {
        lock (stateLock) return documentErrors.TryGetValue(path, out var errors) ? errors : null;
    }

    /// <summary>열린 문서의 최근 진단 중 모르는 이름(미선언·불완전 타입·정의되지 않은 조건 매크로)의 위치입니다.</summary>
    public IReadOnlyList<DiagnosticNameSite> MissingNamesOf(string path)
    {
        lock (stateLock) return missingNames.TryGetValue(path, out var sites) ? sites : Array.Empty<DiagnosticNameSite>();
    }

    public void CloseDocument(string path)
    {
        lock (stateLock)
        {
            diagnosticsVersions.Remove(path);
            documentErrors.Remove(path);
            missingNames.Remove(path);
            openDocuments.Remove(path);
        }

        connection.Notify("textDocument/didClose", JsonValue.Object(("textDocument", JsonValue.Object(("uri", ToUri(path))))));
    }

    /// <summary>
    /// 특정 파일의 명령을 공급하거나 바꿉니다. 바뀐 파일은 background index가 다시 처리하고, 열린 문서면 clangd가 다시 분석합니다.
    /// 같은 명령을 다시 보내면 무시되므로 외부 변경 반영 수단으로 쓰지 않습니다.
    /// </summary>
    /// <remarks>
    /// 파일 키는 Windows 경로 형식(역슬래시)으로 보냅니다. clangd는 명령 저장소에서는 경로를 정규화하지만, 열린 문서를 다시 분석할지는
    /// 받은 키 문자열이 문서 URI에서 얻은 경로와 같은지로 정합니다(clangd 22.1 확인: 슬래시 키는 열린 문서를 다시 분석하지 않음).
    /// 문서 URI와 같은 경로 문자열(대소문자 포함)에서 만든 명령을 줘야 합니다.
    /// </remarks>
    public void UpdateCompileCommands(IEnumerable<CompileCommand> commands) =>
        connection.Notify("workspace/didChangeConfiguration", JsonValue.Object(("settings", JsonValue.Object(
            ("compilationDatabaseChanges", JsonValue.Object(options.Paths.ToReal(commands.ToArray()).Select(c => new KeyValuePair<string, JsonValue>(
                Path.GetFullPath(c.File),
                JsonValue.Object(("workingDirectory", c.Directory), ("compilationCommand", JsonValue.Array(c.Arguments.Select(a => (JsonValue)a))))))))))));

    /// <summary>
    /// 그 문서의 진단(= AST 준비)을 <paramref name="version"/> 이상으로 이미 받았는지 봅니다. 분석이 끝나지 않은 문서에 보낸 요청은 분석이
    /// 끝날 때까지 기다리므로(Unreal 문서 9~13초) 다른 문서를 확인하는 보조 요청 전에 씁니다.
    /// </summary>
    public bool HasDiagnostics(string path, int version)
    {
        lock (stateLock) return diagnosticsVersions.TryGetValue(path, out var received) && received >= version;
    }

    /// <summary>해당 문서의 진단(= AST 준비)이 <paramref name="minimumVersion"/> 이상으로 도착할 때까지 기다립니다.</summary>
    public Task WaitForDiagnosticsAsync(string path, int minimumVersion, CancellationToken cancellationToken)
    {
        lock (stateLock)
        {
            if (diagnosticsVersions.TryGetValue(path, out var version) && version >= minimumVersion)
            {
                return Task.CompletedTask;
            }

            return AddWaiter(path, minimumVersion, cancellationToken);
        }
    }

    /// <summary>
    /// 지금 이후 도착하는 그 문서의 다음 진단을 기다립니다. 내용 변경 없이 명령만 바꿔 다시 분석할 때는 진단 버전이 같아
    /// <see cref="WaitForDiagnosticsAsync"/>로 구분할 수 없어 씁니다. 명령을 바꾸기 전에 불러야 합니다.
    /// </summary>
    public Task WaitForNextDiagnosticsAsync(string path, CancellationToken cancellationToken)
    {
        lock (stateLock) return AddWaiter(path, int.MinValue, cancellationToken);
    }

    /// <summary>진단 대기자를 등록합니다. stateLock 안에서 호출합니다.</summary>
    private Task AddWaiter(string path, int minimumVersion, CancellationToken cancellationToken)
    {
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

    public async Task<IReadOnlyList<NavigationLocation>> DefinitionAsync(string path, int line, int character, CancellationToken cancellationToken) =>
        options.Paths.ToGiven(NavigationLocation.FromLsp(await connection.RequestAsync("textDocument/definition", Position(path, line, character), cancellationToken).ConfigureAwait(false)));

    public async Task<IReadOnlyList<NavigationLocation>> DeclarationAsync(string path, int line, int character, CancellationToken cancellationToken) =>
        options.Paths.ToGiven(NavigationLocation.FromLsp(await connection.RequestAsync("textDocument/declaration", Position(path, line, character), cancellationToken).ConfigureAwait(false)));

    public async Task<IReadOnlyList<NavigationLocation>> ReferencesAsync(string path, int line, int character, bool includeDeclaration, CancellationToken cancellationToken) =>
        (await ReferencesCountedAsync(path, line, character, includeDeclaration, cancellationToken).ConfigureAwait(false)).Locations;

    /// <summary>
    /// 참조와 clangd가 보낸 원래 항목 수입니다. 같은 위치가 겹쳐 오면 결과에서 하나로 합치므로, 결과가 상한에 걸렸는지는 원래 항목 수로
    /// 판단해야 합니다(<see cref="ReferenceLimit"/>).
    /// </summary>
    public async Task<(IReadOnlyList<NavigationLocation> Locations, int RawCount)> ReferencesCountedAsync(string path, int line, int character, bool includeDeclaration,
        CancellationToken cancellationToken)
    {
        var parameters = JsonValue.Object(
            ("textDocument", JsonValue.Object(("uri", ToUri(path)))),
            ("position", JsonValue.Object(("line", line), ("character", character))),
            ("context", JsonValue.Object(("includeDeclaration", includeDeclaration))));
        var result = await connection.RequestAsync("textDocument/references", parameters, cancellationToken).ConfigureAwait(false);
        return (options.Paths.ToGiven(NavigationLocation.FromLsp(result)), result.Items.Count);
    }

    /// <param name="spelled">요청 위치에 쓰인 식별자입니다. 주면 그 이름의 항목을 대표로 씁니다.</param>
    public async Task<SemanticSymbol?> SymbolInfoAsync(string path, int line, int character, CancellationToken cancellationToken, string? spelled = null)
    {
        var result = await connection.RequestAsync("textDocument/symbolInfo", Position(path, line, character), cancellationToken).ConfigureAwait(false);
        // 대표 항목은 요청 위치에 쓰인 이름의 항목입니다. symbolInfo는 별칭이 가리키는 원래 선언(예: TTuple, typedef 사슬의 앞 typedef)을
        // 별칭보다 앞에, 매크로를 맨 뒤에 두지만, clangd는 그 위치의 정의·참조를 쓰인 이름의 심볼(별칭, 매크로)로 찾습니다(2026-10-09 정확도
        // 시험: 대표 이름이 달라 결과가 모두 빠짐). 같은 이름이 여럿이면(typedef A::X X) 마지막이 쓰인 선언입니다(clangd FindTarget의 보고
        // 순서). 이름을 모르면 매크로 항목, 그다음 첫 항목입니다.
        var first = (spelled is null ? null : result.Items.LastOrDefault(i => string.Equals(i["name"].AsString(), spelled, StringComparison.Ordinal)))
                    ?? result.Items.LastOrDefault(i => i["usr"].AsString()?.Contains("@macro@") == true) ?? result.Items.FirstOrDefault();
        if (first is null || first["name"].AsString() is not string name)
        {
            return null;
        }

        var container = (first["containerName"].AsString() ?? string.Empty).TrimEnd(':');
        // 선언·정의 범위는 역할 표식 근거라 심볼 하나로 정해질 때만 씁니다.
        NavigationLocation? Range(string field) =>
            result.Items.Count == 1 ? options.Paths.ToGiven(NavigationLocation.FromLsp(first[field])).FirstOrDefault() : null;
        var ids = result.Items.Select(i => i["id"].AsString()).Where(i => !string.IsNullOrEmpty(i)).Select(i => i!.ToUpperInvariant()).Distinct().ToArray();
        var usrs = result.Items.Select(i => i["usr"].AsString()).Where(u => !string.IsNullOrEmpty(u)).Select(u => u!).Distinct().ToArray();
        // 같은 이름의 함수 항목이 여럿이면 clang이 오버로드를 정하지 못한 위치입니다. 별칭과 원래 선언(typedef A::X X)처럼 함수가 아닌 같은 이름은
        // 후보로 보지 않습니다.
        var sameName = result.Items.Where(i => string.Equals(i["name"].AsString(), name, StringComparison.Ordinal)).ToArray();
        var overloads = sameName.Length > 1 && sameName.All(i => IsFunctionUsr(i["usr"].AsString(), name))
            ? sameName.Select(i => i["id"].AsString()).Where(i => !string.IsNullOrEmpty(i)).Select(i => i!.ToUpperInvariant()).Distinct().ToArray()
            : null;
        return new SemanticSymbol(name, container, first["usr"].AsString() ?? string.Empty, Range("declarationRange"), Range("definitionRange"), ids, usrs,
            first["id"].AsString()?.ToUpperInvariant(), options.Paths.ToGiven(NavigationLocation.FromLsp(first["declarationRange"])).FirstOrDefault(),
            overloads is { Length: > 1 } ? overloads : null);
    }

    /// <summary>
    /// clang USR이 이름이 <paramref name="name"/>인 함수(<c>@F@이름#</c>)나 함수 템플릿(<c>@FT@&gt;…이름#</c>)의 것인지 봅니다. 그 함수 안의
    /// 지역 변수(<c>@F@함수#@x</c>)는 이름이 달라 맞지 않고, 필드(<c>@FI@</c>)·매크로도 아닙니다.
    /// </summary>
    public static bool IsFunctionUsr(string? usr, string name)
    {
        if (usr is not { Length: > 0 } || name.Length == 0 || usr.Contains("@macro@")) return false;
        var escaped = Regex.Escape(name);
        return Regex.IsMatch(usr, "@F@" + escaped + "#", RegexOptions.CultureInvariant) ||
               Regex.IsMatch(usr, "@FT@>[^@]*" + escaped + "#", RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 심볼 종류를 clangd 색인(<c>workspace/symbol</c>)에서 찾습니다. 같은 이름의 다른 심볼·오버로드와 섞이지 않게,
    /// 이 심볼의 선언·정의로 이미 받은 위치(<paramref name="known"/>)와 위치가 같은 항목만 씁니다.
    /// 색인에 없는 지역 변수·매개변수·템플릿 인수이거나 일치하는 항목이 없으면 null입니다.
    /// </summary>
    public async Task<SourceSymbolKind?> SymbolKindAsync(SemanticSymbol symbol, IReadOnlyCollection<NavigationLocation> known, CancellationToken cancellationToken)
    {
        if (known.Count == 0) return null;
        var result = await connection.RequestAsync("workspace/symbol", JsonValue.Object(("query", symbol.QualifiedName)), cancellationToken).ConfigureAwait(false);
        var positions = new HashSet<NavigationLocation>(known);
        var self = ScopeKey(symbol.QualifiedName);
        foreach (var item in result.Items)
        {
            // 클래스의 참조에는 생성자·소멸자 이름 위치가 들어 있고 생성자도 같은 이름으로 검색되므로, 이 심볼 안에 든 항목은 이 심볼이 아닙니다
            // (2026-10-09 정확도 시험: 클래스를 함수로 판정해 생성자 이름 제외가 빠짐).
            if (string.Equals(ScopeKey(item["containerName"].AsString() ?? string.Empty), self, StringComparison.Ordinal)) continue;
            var location = options.Paths.ToGiven(NavigationLocation.FromLsp(item["location"]));
            if (location.Count == 1 && positions.Contains(location[0])) return SymbolKindOf(item["kind"].AsInt32());
        }

        return null;
    }

    /// <summary>소속 이름 비교용 표기입니다. 템플릿 인수와 익명 네임스페이스는 출처(색인·AST)마다 표기가 달라 뺍니다.</summary>
    private static string ScopeKey(string scope)
    {
        var plain = new StringBuilder(scope.Length);
        var depth = 0;
        foreach (var ch in scope.Replace("(anonymous namespace)::", string.Empty))
        {
            if (ch == '<') depth++;
            else if (ch == '>' && depth > 0) depth--;
            else if (depth == 0) plain.Append(ch);
        }

        return plain.ToString().Trim(':');
    }

    /// <summary>
    /// LSP SymbolKind를 VisualBoost 색상 분류로 바꿉니다. clangd는 매크로를 String(15), 공용체·별칭을 Class(5)로 보냅니다.
    /// 색을 정할 수 없는 종류(File·Module 등)는 null입니다.
    /// </summary>
    public static SourceSymbolKind? SymbolKindOf(int? lspKind) => lspKind switch
    {
        3 or 4 => SourceSymbolKind.Namespace,
        5 or 11 => SourceSymbolKind.Class,
        6 or 9 or 12 or 25 => SourceSymbolKind.Function,
        7 or 8 or 13 or 14 or 22 => SourceSymbolKind.Variable,
        10 => SourceSymbolKind.Enum,
        15 => SourceSymbolKind.Macro,
        23 => SourceSymbolKind.Struct,
        26 => SourceSymbolKind.Type,
        _ => null,
    };

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

    /// <summary>clangd에 보내는 문서 URI입니다. 링크를 거쳐 연 작업 영역이면 실제 경로로 바꿉니다(<see cref="PathAliases"/>).</summary>
    private string ToUri(string path) => DocumentUri.FromPath(options.Paths.ToReal(path));

    private JsonValue Position(string path, int line, int character) => JsonValue.Object(
        ("textDocument", JsonValue.Object(("uri", ToUri(path)))),
        ("position", JsonValue.Object(("line", line), ("character", character))));

    private static string LanguageOf(string path) =>
        Path.GetExtension(path).Equals(".c", StringComparison.OrdinalIgnoreCase) ? "c" : "cpp";

    internal static string QuoteArgument(string argument)
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

        if (method == "textDocument/publishDiagnostics" && DocumentUri.ToPath(parameters["uri"].AsString() ?? string.Empty) is string received)
        {
            // 문서는 실제 경로로 열었으므로 대기자와 같은 연 경로로 되돌려 비교합니다.
            var path = options.Paths.ToGiven(received);
            // 버전 없는 진단은 닫은 문서의 진단을 지우는 알림입니다. 닫고 바로 다시 연 문서(ClangdDocumentSet.Reopen)에서 이 알림이 새 분석의
            // 진단처럼 대기를 끝내거나 오류 요약을 지우지 않게 버립니다. 닫은 문서의 상태는 CloseDocument에서 이미 지웠습니다.
            if (parameters["version"].AsInt32() is not int version) return;
            var errors = DocumentErrors.From(parameters["diagnostics"].Items);
            var undefinedMacros = DocumentErrors.HasUndefinedConditionMacro(parameters["diagnostics"].Items);
            var sites = IncludeSupplements.Sites(parameters["diagnostics"].Items, path, uri => DocumentUri.ToPath(uri) is string real ? options.Paths.ToGiven(real) : null);
            List<DiagnosticsWaiter> ready;
            bool publish;
            lock (stateLock)
            {
                var known = diagnosticsVersions.TryGetValue(path, out var previous) ? previous : int.MinValue;
                var latest = version >= known;
                publish = latest && openDocuments.Contains(path);
                if (latest)
                {
                    // 늦게 도착한 이전 버전의 진단으로 최신 오류 요약을 덮지 않습니다.
                    if (errors is null) documentErrors.Remove(path);
                    else documentErrors[path] = errors;
                    if (sites.Count == 0) missingNames.Remove(path);
                    else missingNames[path] = sites;
                }

                diagnosticsVersions[path] = Math.Max(version, known);
                ready = diagnosticsWaiters.Where(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase) && version >= w.MinimumVersion).ToList();
                foreach (var waiter in ready) diagnosticsWaiters.Remove(waiter);
            }

            if (publish) DiagnosticsPublished?.Invoke(path, errors, undefinedMacros);
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
        if (e.Data is null)
        {
            return;
        }

        var line = e.Data;
        var failed = ParseIndexFailure(line);
        if (failed is not null)
        {
            IndexFailed?.Invoke(options.Paths.ToGiven(failed));
        }
        else if (ParseIndexedTranslationUnit(line) is string indexed)
        {
            TranslationUnitIndexed?.Invoke(options.Paths.ToGiven(indexed));
        }

        if (log is null)
        {
            return;
        }

        lock (stateLock)
        {
            if (!ShouldLog(line, failed is not null)) return;
            // 로그가 무한히 커지지 않게 상한(8 MiB) 이후는 버립니다.
            if (Volatile.Read(ref disposed) != 0 || logBytes > 8 * 1024 * 1024) return;
            logBytes += line.Length + 2;
            log.WriteLine(line);
        }
    }

    /// <summary>
    /// 파일에 남길 줄인지 정합니다. stateLock 안에서 호출합니다. info 수준은 요청마다 줄이 생겨 오류와 색인 실패만 남기고, 수준 표시 없는
    /// 이어지는 줄은 앞 메시지를 따릅니다. 비정상 종료 때의 스택 덤프는 수준 표시 없이 오므로 그 시작 줄부터 남깁니다.
    /// </summary>
    private bool ShouldLog(string line, bool indexFailure)
    {
        if (line.Length > 1 && line[1] == '[' && line[0] is 'E' or 'I' or 'V' or 'D')
        {
            keepingMessage = line[0] == 'E' || indexFailure;
        }
        else if (line.StartsWith("PLEASE submit", StringComparison.Ordinal) || line.StartsWith("Stack dump", StringComparison.Ordinal) ||
                 line.StartsWith("Exception Code", StringComparison.Ordinal))
        {
            keepingMessage = true;
        }

        return keepingMessage;
    }

    private const string FailurePrefix = "] Failed to compile ";
    private const string FailureSuffix = ", index may be incomplete";

    /// <summary>
    /// background index의 분석 오류 줄(<c>I[시각] Failed to compile 경로, index may be incomplete</c>)에서 TU 경로를 꺼냅니다.
    /// clangd가 TU에 컴파일할 수 없는 오류가 있을 때 info 수준으로 남깁니다(clangd 22.1 확인). 형식이 다르면 null입니다.
    /// </summary>
    public static string? ParseIndexFailure(string line)
    {
        var start = line.IndexOf(FailurePrefix, StringComparison.Ordinal);
        if (start < 0 || start > 32 || !line.EndsWith(FailureSuffix, StringComparison.Ordinal)) return null;
        start += FailurePrefix.Length;
        var length = line.Length - FailureSuffix.Length - start;
        return length > 0 ? line.Substring(start, length) : null;
    }

    private const string IndexedPrefix = "] Indexed ";

    /// <summary>
    /// TU 하나의 색인 완료 줄(<c>I[시각] Indexed 경로 (n symbols, n refs, n files)</c>)에서 TU 경로를 꺼냅니다. 표준 라이브러리 요약 줄이나
    /// 형식이 다르면 null입니다. 경로에 괄호가 있을 수 있어 마지막 괄호 묶음을 통계로 봅니다.
    /// </summary>
    public static string? ParseIndexedTranslationUnit(string line)
    {
        var start = line.IndexOf(IndexedPrefix, StringComparison.Ordinal);
        if (start < 0 || start > 32 || !line.EndsWith(" files)", StringComparison.Ordinal)) return null;
        start += IndexedPrefix.Length;
        var end = line.LastIndexOf(" (", StringComparison.Ordinal);
        return end > start ? line.Substring(start, end - start) : null;
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
