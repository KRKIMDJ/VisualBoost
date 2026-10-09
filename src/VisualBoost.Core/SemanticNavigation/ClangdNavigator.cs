using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>clangd 탐색을 쓸 수 없는 이유입니다. 호출자는 기본 탐색으로 넘어가거나 이유를 표시합니다.</summary>
public sealed class SemanticNavigationUnavailableException : Exception
{
    public SemanticNavigationUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class ClangdNavigatorOptions
{
    public string ClangdPath { get; set; } = string.Empty;

    /// <summary>Solution별 compilation database와 색인 캐시를 둘 상위 폴더입니다.</summary>
    public string CacheRoot { get; set; } = string.Empty;

    public string SolutionPath { get; set; } = string.Empty;

    /// <summary>Unreal 엔진 설치 루트. Unreal 프로젝트가 아니거나 찾지 못했으면 null.</summary>
    public string? EngineRoot { get; set; }

    /// <summary>0이면 <see cref="ClangdLaunchOptions.DefaultWorkerCount"/>로 정합니다.</summary>
    public int WorkerCount { get; set; }

    /// <summary>clangd에 동시에 열어 둘 문서 수입니다. 문서마다 preamble·AST 메모리를 씁니다.</summary>
    /// <remarks>
    /// 테스트 전용 UE 5.8 샘플에서 문서 하나를 열 때마다 private 메모리가 120~520 MB(평균 약 350 MB) 늘어 8개면 약 2.8 GB였습니다
    /// (2026-10-08 측정). 활성 문서와 최근에 찾은 문서 두 개만 유지하고, 그보다 오래된 문서는 다시 찾을 때 분석합니다(Unreal 파일 약 3.6초).
    /// </remarks>
    public int DocumentCapacity { get; set; } = 3;

    /// <summary>엔진 정의 후보를 고를 이름 인덱스 조회. 없으면 헤더와 이름이 같은 cpp만 봅니다.</summary>
    public Func<string, IReadOnlyList<SourceSymbolLocation>>? FindSymbols { get; set; }

    /// <summary>
    /// 이름 인덱스가 분석을 마쳤는지입니다. 색인 단위의 보충 헤더는 모듈에 기록되므로 마친 뒤에만 고릅니다(덜 찬 색인에서는 후보가 하나여도
    /// 실제로 유일하지 않을 수 있음). 없으면 늘 마친 것으로 봅니다.
    /// </summary>
    public Func<bool>? SymbolsReady { get; set; }

    /// <summary>
    /// <see cref="SymbolsReady"/>를 기다리는 상한입니다. 넘으면 지금까지의 이름 인덱스로 검사하고, 그 세션에서는 다시 기다리지 않습니다.
    /// 기다리는 동안 검사 대상 모듈의 단위 전환과 메모리 정리·다시 읽기가 미뤄지므로 끝없이 기다리지 않습니다.
    /// </summary>
    public TimeSpan SymbolsWaitLimit { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>파일 이름(확장자 제외)으로 파일을 찾는 조회입니다.</summary>
    public Func<string, IReadOnlyList<string>>? FindByStem { get; set; }

    /// <summary>compile_commands.json이 없을 때 명령을 얻을 빌드 도구와 Solution 구성입니다.</summary>
    public CompileCommandSources Sources { get; set; } = new();

    /// <summary>정의가 색인에 없을 때 요청 시점에 열어 볼 cpp 수입니다(엔진·프로젝트 공통).</summary>
    public int MaxDefinitionCandidates { get; set; } = 3;

    public TimeSpan CandidateTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Unreal 공유 PCH 헤더를 분석 명령에 넣는 방식입니다.</summary>
    public UnrealPchMode PchMode { get; set; } = UnrealPchMode.Auto;

    /// <summary>색인 실패를 모아 공유 PCH로 바꾸기 전에 기다리는 시간입니다. 실패마다 database를 다시 쓰지 않게 묶습니다.</summary>
    public TimeSpan PchSwitchDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 색인이 끝나기 전에 연 문서와 관련된 TU(그 문서와 include한 헤더의 같은 이름 cpp)를 색인 대기열 앞으로 올립니다
    /// (<see cref="IndexQueuePriority"/>). 끄면 clangd의 무작위 순서를 따릅니다.
    /// </summary>
    public bool PrioritizeOpenDocuments { get; set; } = true;
}

/// <summary>요청 위치와 그때의 편집기 내용입니다. 좌표는 0기반 줄과 UTF-16 문자 위치입니다.</summary>
public sealed class NavigationQuery
{
    public NavigationQuery(DocumentText document, int line, int character, IReadOnlyList<DocumentText>? openDocuments = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Line = line;
        Character = character;
        OpenDocuments = openDocuments ?? Array.Empty<DocumentText>();
    }

    public DocumentText Document { get; }

    public string Path => Document.Path;

    public int Line { get; }

    public int Character { get; }

    /// <summary>편집기에 열린 다른 문서. clangd에 이미 열린 문서만 갱신하며 나머지의 내용은 만들지 않습니다.</summary>
    public IReadOnlyList<DocumentText> OpenDocuments { get; }
}

public sealed class NavigationResult
{
    public NavigationResult(IReadOnlyList<NavigationLocation> locations, SemanticSymbol? symbol, BackgroundIndexProgress progress, bool resolvedOnDemand,
        SourceSymbolKind? symbolKind = null, IReadOnlyDictionary<NavigationLocation, NavigationRole>? roles = null, bool limited = false,
        int uncheckedDefinitionFiles = 0, bool resolvedFromEngine = false)
    {
        ResolvedFromEngine = resolvedOnDemand && resolvedFromEngine;
        Limited = limited;
        UncheckedDefinitionFiles = uncheckedDefinitionFiles;
        Locations = locations;
        Symbol = symbol;
        Progress = progress;
        ResolvedOnDemand = resolvedOnDemand;
        SymbolKind = symbolKind;
        Roles = roles ?? new Dictionary<NavigationLocation, NavigationRole>();
    }

    /// <summary>참조 위치별 역할(정의·선언)입니다. 근거가 확실한 위치만 들어 있습니다(<see cref="ReferenceRoles"/>).</summary>
    public IReadOnlyDictionary<NavigationLocation, NavigationRole> Roles { get; }

    public NavigationRole RoleOf(NavigationLocation location) => Roles.TryGetValue(location, out var role) ? role : NavigationRole.None;

    public IReadOnlyList<NavigationLocation> Locations { get; }

    public SemanticSymbol? Symbol { get; }

    /// <summary>요청 시점의 색인 진행. 완료 전이면 결과가 불완전할 수 있습니다.</summary>
    public BackgroundIndexProgress Progress { get; }

    /// <summary>색인에 없던 정의 파일(엔진 cpp, 아직 색인하지 않았거나 명령이 없던 프로젝트 cpp)을 요청 시점에 열어 정의를 확정했습니다.</summary>
    public bool ResolvedOnDemand { get; }

    /// <summary>요청 시점에 연 정의 파일이 엔진 cpp입니다(색인 범위 밖). 아니면 아직 색인하지 않았거나 명령이 없던 프로젝트 파일입니다.</summary>
    public bool ResolvedFromEngine { get; }

    /// <summary>결과 목록에서 이름을 색칠할 심볼 종류입니다. 목록을 보이지 않는 결과(정의 하나)나 판정하지 못하면 null입니다.</summary>
    public SourceSymbolKind? SymbolKind { get; }

    /// <summary>참조가 상한(<see cref="ClangdNavigator.ReferenceLimit"/>)에 걸려 일부만 받았습니다.</summary>
    public bool Limited { get; }

    /// <summary>
    /// 모듈 API 매크로를 정의한 다른 모듈 정의 헤더 중 요청당 상한을 넘어 확인하지 않은 수입니다. 0보다 크면 그 모듈들의 사용처가 빠졌을 수
    /// 있습니다(2026-10-09 검토 46).
    /// </summary>
    public int UncheckedDefinitionFiles { get; }

    /// <summary>위치 목록만 바꾸고 나머지 정보는 그대로 둔 결과입니다(정렬·거르기 뒤).</summary>
    public NavigationResult WithLocations(IReadOnlyList<NavigationLocation> locations) =>
        new(locations, Symbol, Progress, ResolvedOnDemand, SymbolKind, Roles, Limited, UncheckedDefinitionFiles, ResolvedFromEngine);

    /// <summary>
    /// 참조를 요청 파일에서만 찾은 결과입니다. clangd는 네임스페이스 참조를 색인하지 않아(clangd 22.1 확인) 다른 파일의 사용은 돌려주지 않습니다.
    /// </summary>
    public bool CurrentFileOnly => Symbol is { } symbol && IsNamespace(symbol.Usr);

    /// <summary>clang USR의 마지막 구성 요소가 네임스페이스(<c>@N@이름</c>, 익명 <c>@aN</c>)인지 봅니다.</summary>
    public static bool IsNamespace(string usr) => System.Text.RegularExpressions.Regex.IsMatch(usr, "(@N@[^@]+|@aN)$");
}

/// <summary>
/// Solution 하나의 clangd 세션과 문서 동기화, 정의·참조 요청을 묶습니다. VS SDK에 의존하지 않습니다.
/// </summary>
/// <remarks>
/// 범위 결정: 프로젝트 TU는 background index로 전부 색인하고, 엔진은 프로젝트 TU가 포함한 헤더까지만 색인합니다.
/// 엔진 cpp에만 있는 정의는 요청 시점에 후보 cpp에 근사 명령을 공급하고 열어 확정합니다. 프로젝트 정의가 아직 색인에 없을 때
/// (첫 색인 중, 명령이 없던 파일)도 같은 방식으로 후보 cpp를 열어 확정합니다. 확정한 파일은 background index에 남아 다음 요청부터 바로 찾습니다.
/// 최신성: 편집기에서 저장한 문서는 clangd에 열려 있으면 내용과 저장을 알리고, 아니면 저장된 내용으로 열었다가
/// 분석이 끝나면 닫습니다. clangd는 파일 감시 통지만으로는 닫힌 파일을 다시 색인하지 않기 때문입니다.
/// 편집기 밖에서 바뀐 파일(<see cref="Reload"/>)도 디스크 내용으로 같은 방식을 씁니다.
/// </remarks>
public sealed class ClangdNavigator : IDisposable
{
    /// <summary>색칠용 종류 조회의 자체 상한입니다. 이미 받은 결과를 보이는 시간을 이 이상 늦추지 않습니다.</summary>
    private static readonly TimeSpan SymbolKindTimeout = TimeSpan.FromSeconds(1);

    // 역할 표식 근거를 본 참조 결과 뒤에 기다리는 상한입니다. 종류 판정(SymbolKindTimeout)과 같은 구간에 겹쳐 결과 표시를 크게 늦추지 않습니다.
    private static readonly TimeSpan RoleTimeout = TimeSpan.FromSeconds(1);

    private readonly ClangdNavigatorOptions options;
    private readonly ClangdSession session;
    private readonly ClangdDocumentSet documents;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object touchGate = new();
    private readonly Queue<string> touchOrder = new();
    private readonly Dictionary<string, DocumentText> touchTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> attemptedCandidates = new(StringComparer.OrdinalIgnoreCase);
    // 근사 명령으로 연 소스의 기억입니다(Unreal만). 다음 clangd가 그 색인 파일을 읽게 합니다.
    private readonly DefinitionSourceStore? definitionSources;
    // 이 clangd에서 색인 대기열 앞으로 올린 표시, 자신을 올리고 아직 색인을 기다리는 연 문서, 그 문서들이 색인된 뒤 올릴 관련 TU 표시입니다.
    // clangd가 올린 표시를 기억하므로 한 번씩만 보냅니다. raisedTags 잠금으로 함께 보호합니다.
    private readonly HashSet<string> raisedTags = new(StringComparer.Ordinal);
    private readonly HashSet<string> awaitingOwnIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> pendingRelatedTags = new();
    // Unreal 외 문맥의 이름별 TU 표시입니다. 처음 쓸 때 만들며 같은 내용이라 잠그지 않습니다.
    private volatile ILookup<string, string>? commandTags;
    private readonly ClangdIndexShards shards;
    // Context.Commands의 파일별 명령입니다. 여러 요청이 동시에 처음 만들어도 같은 내용이라 잠그지 않습니다.
    private volatile Dictionary<string, CompileCommand>? commandsByFile;
    // 명령을 덮어쓰기로 준 문서입니다. ClangdDocumentSet이 잠금 안에서 여는 콜백에서만 쓰므로 그 잠금으로 보호됩니다.
    private readonly HashSet<string> documentCommandsSent = new(StringComparer.OrdinalIgnoreCase);
    // 공유 PCH 전환 상태입니다. pchGate로 보호합니다.
    private readonly object pchGate = new();
    // PCH 없이 명령을 주고 첫 진단을 아직 보지 않은 문서입니다.
    private readonly HashSet<string> pchPending = new(StringComparer.OrdinalIgnoreCase);
    // 분석 오류로 보충 헤더나 PCH를 넣어 다시 분석하는 문서와 그 분석의 진단 도착, 명령 보내기(결과가 참이면 보충 단계)입니다.
    private readonly Dictionary<string, Task> pchReparses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<bool>> pchSwitches = new(StringComparer.OrdinalIgnoreCase);
    // 문서에 보낸 명령의 보충 헤더와 이 세션에서 문서마다 보충한 횟수입니다(IncludeSupplements).
    private readonly Dictionary<string, IReadOnlyList<string>> documentSupplements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> documentSupplementRounds = new(StringComparer.OrdinalIgnoreCase);
    // 문서 분석에서 고른 보충 헤더입니다(이 세션의 그 문서에만 넣음). 편집 중 코드의 include 누락이나 덜 찬 이름 색인으로 잘못 고른 헤더가
    // 모듈 전체와 다음 세션에 남지 않게 모듈 학습은 디스크 내용을 검사하는 색인 단위에서만 합니다(피드백 검토 65). pchGate로 보호합니다.
    private readonly Dictionary<string, IReadOnlyList<string>> documentOwnSupplements = new(StringComparer.OrdinalIgnoreCase);
    // 색인 실패를 알린 TU 중 아직 PCH 전환을 하지 않은 것입니다.
    private readonly HashSet<string> failedUnits = new(StringComparer.OrdinalIgnoreCase);
    private bool pchFlushScheduled;
    // 이름 인덱스 대기 상한에 한 번 닿았는지입니다. 닿은 뒤로는 이 세션에서 더 기다리지 않습니다. 묶음마다 상한을 새로 세면 실패가 이어지는
    // 첫 색인에서 바쁨 상태가 묶음 수만큼 길어졌습니다(피드백 검토 71). 실패 묶음 작업은 pchFlushScheduled로 한 번에 하나만 돌고, 표식을
    // 내리고 올리는 pchGate가 작업 사이의 순서를 보장하므로 따로 잠그지 않습니다.
    private bool symbolsWaitExpired;
    private int pchUnitIndexed;
    private int indexedUnits;
    // 색인 완료 줄을 하나라도 읽었는지(색인 시작 문서 포함), 로그 형식 확인을 시작했는지, 형식이 맞지 않는다고 판단했는지입니다.
    private int indexedLineSeen;
    private int logFormatChecking;
    private int logFormatUnreadable;
    private bool touchRunning;
    private int activeRequests;
    private long lastRequestTicks = DateTime.UtcNow.Ticks;
    private int disposed;

    private ClangdNavigator(ClangdNavigatorOptions options, CompileContext context, ClangdSession session)
    {
        this.options = options;
        Context = context;
        this.session = session;
        shards = new ClangdIndexShards(Path.Combine(context.Directory, ".cache", "clangd", "index"));
        definitionSources = context.Kind == CompileContextKind.Unreal ? new DefinitionSourceStore(context.Directory) : null;
        documents = new ClangdDocumentSet(Math.Max(1, options.DocumentCapacity), OpenDocument, session.ChangeDocument, session.CloseDocument);
        session.ProgressChanged += () =>
        {
            if (Progress.Completed && Interlocked.Exchange(ref logFormatChecking, 1) == 0) _ = Task.Run(CheckLogFormatAsync);
            Changed?.Invoke();
        };
        var autoPch = context.Plan is { Mode: UnrealPchMode.Auto };
        if (autoPch)
        {
            session.IndexFailed += OnIndexFailed;
            session.DiagnosticsPublished += OnDiagnostics;
        }

        session.TranslationUnitIndexed += path =>
        {
            Volatile.Write(ref indexedLineSeen, 1);
            if (string.Equals(Path.GetFileName(path), CompileContext.IndexStartFileName, StringComparison.OrdinalIgnoreCase) || IsQueueMarker(path)) return;
            OwnIndexed(path);
            Interlocked.Increment(ref indexedUnits);
            if (autoPch && UnrealIndexPlan.IsSwitchedWrapper(path)) Volatile.Write(ref pchUnitIndexed, 1);
        };

        session.Exited += _ =>
        {
            lifetime.Cancel();
            Changed?.Invoke();
        };
    }

    /// <summary>색인 진행이나 종료가 바뀌었습니다. 임의 스레드에서 호출됩니다.</summary>
    public event Action? Changed;

    /// <summary>
    /// 요청은 계속했지만 보조 단계(참조 보완, 문서 근사 명령)가 실패했습니다. 결과가 덜 정확할 수 있다는 진단용 알림이며 임의 스레드에서
    /// 호출됩니다(2026-10-09 검토 41·44).
    /// </summary>
    public event Action<string>? AuxiliaryFailed;

    public CompileContext Context { get; }

    public BackgroundIndexProgress Progress => session.Progress;

    public bool HasExited => session.HasExited;

    public int ProcessId => session.ProcessId;

    /// <summary>이 탐색기를 시작한 시각(UTC)입니다.</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>마지막 탐색 요청이 시작하거나 끝난 시각(UTC)입니다. 요청이 없었으면 시작 시각입니다.</summary>
    public DateTime LastRequestUtc => new(Interlocked.Read(ref lastRequestTicks), DateTimeKind.Utc);

    /// <summary>
    /// 탐색 요청, 저장 반영, 공유 PCH 전환(실패 모으기·명령 보내기)이 진행 중입니다. 메모리 정리·다시 읽기 재시작을 미룰 때 씁니다.
    /// 전환 중에 다시 시작하면 그 세션의 전환이 끝나지 않고 다음 세션에서 다시 색인하게 됩니다.
    /// </summary>
    public bool IsBusy
    {
        get
        {
            lock (touchGate)
            {
                if (touchRunning) return true;
            }

            lock (pchGate)
            {
                if (pchFlushScheduled || pchSwitches.Values.Any(sent => !sent.IsCompleted)) return true;
            }

            return Volatile.Read(ref activeRequests) > 0;
        }
    }

    /// <summary>참조 결과 상한입니다. 결과 수가 같으면 잘렸을 수 있습니다.</summary>
    public int ReferenceLimit => session.ReferenceLimit;

    /// <summary>
    /// 공유 PCH를 넣어 다시 색인한 결과를 쓰려면 clangd를 다시 시작해야 합니다. 호출자는 탐색·색인이 멈춘 동안 다시 시작합니다.
    /// </summary>
    /// <remarks>
    /// clangd는 내용이 같은 파일을 다시 색인하면 이전 색인에 오류가 있었어도 디스크의 색인 파일만 새로 쓰고 메모리의 이전 결과는 그대로
    /// 씁니다(clangd 22.1, 2026-10-09 확인: 같은 세션에서는 PCH로 다시 색인한 구성원의 참조가 빠지고 다시 시작하면 보임). 그래서 이 세션에서
    /// PCH 합성 TU를 색인했고 색인이 끝났으며 남은 전환이 없으면 참입니다. 다시 시작한 clangd는 새 색인 파일을 읽으므로 다시 색인하지 않습니다.
    /// </remarks>
    public bool NeedsReload
    {
        get
        {
            if (Volatile.Read(ref pchUnitIndexed) == 0 || !Progress.Completed) return false;
            lock (pchGate) return !pchFlushScheduled;
        }
    }

    /// <summary>
    /// 이 clangd가 background index로 색인한 TU 수입니다(색인 시작 문서 제외). 색인하며 쓴 메모리는 clangd가 운영체제에 돌려주지 않으므로,
    /// 색인한 세션은 끝난 뒤 다시 시작해 돌려받습니다(<see cref="ClangdMemoryPolicy.ShouldReclaimAfterIndex"/>).
    /// </summary>
    public int IndexedUnits => Volatile.Read(ref indexedUnits);

    /// <summary>이 clangd가 공유 PCH 합성 TU를 색인했습니다. 다시 읽기 재시작이 되풀이되는지 판단할 때 씁니다.</summary>
    public bool PchUnitsIndexed => Volatile.Read(ref pchUnitIndexed) != 0;

    /// <summary>
    /// clangd가 색인 파일을 새로 썼는데 색인 완료 로그 줄을 하나도 읽지 못했습니다. clangd 버전이 바뀌어 로그 형식이 달라졌을 수 있으며,
    /// 그동안 공유 PCH 자동 전환과 색인 뒤 메모리 정리가 동작하지 않습니다(연 문서의 PCH 전환은 진단 알림이라 계속 동작).
    /// </summary>
    /// <remarks>
    /// 진척 알림만으로는 판단하지 않습니다. 저장된 색인을 읽기만 하는 재시작 세션도 진척을 알리고 완료 줄이 없어(2026-10-09 확인) 잘못
    /// 판단하기 때문입니다. 첫 색인이 끝난 뒤 한 번, 이 세션이 시작한 뒤 쓴 색인 파일이 있는지로 실제 색인 여부를 봅니다.
    /// </remarks>
    public bool IndexLogUnreadable => Volatile.Read(ref logFormatUnreadable) != 0;

    public string LogPath => Path.Combine(Context.Directory, "clangd.log");

    /// <summary>컴파일 문맥을 준비하고 clangd를 시작합니다. 문맥이 없으면 <see cref="SemanticNavigationUnavailableException"/>입니다.</summary>
    public static async Task<ClangdNavigator> StartAsync(ClangdNavigatorOptions options, CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ClangdPath))
        {
            throw new SemanticNavigationUnavailableException("clangd를 찾지 못했습니다. Visual Studio의 C++ Clang 도구 구성 요소를 설치하거나 옵션에서 경로를 지정하세요.");
        }

        var compiler = Path.Combine(Path.GetDirectoryName(options.ClangdPath)!, "clang-cl.exe");
        var context = await Task.Run(() => CompileContextBuilder.Prepare(options.SolutionPath, options.CacheRoot, options.EngineRoot,
            File.Exists(compiler) ? compiler : "clang-cl.exe", cancellationToken, options.Sources, options.PchMode, supplements: options.FindSymbols is not null),
            cancellationToken).ConfigureAwait(false);
        if (!context.IsAvailable)
        {
            throw new SemanticNavigationUnavailableException(context.Reason ?? "컴파일 명령이 없습니다.");
        }

        ClangdSession session;
        try
        {
            session = ClangdSession.Start(new ClangdLaunchOptions
            {
                ExecutablePath = options.ClangdPath,
                CompileCommandsDirectory = context.Directory,
                WorkerCount = options.WorkerCount,
                LogFilePath = Path.Combine(context.Directory, "clangd.log"),
                Paths = context.Paths
            });
        }
        catch (Exception exception) when (exception is IOException || exception is System.ComponentModel.Win32Exception)
        {
            throw new SemanticNavigationUnavailableException("clangd를 시작하지 못했습니다: " + exception.Message, exception);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await session.InitializeAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException || exception is LspConnectionClosedException || exception is LspRequestException)
        {
            session.Dispose();
            if (cancellationToken.IsCancellationRequested) throw;
            throw new SemanticNavigationUnavailableException("clangd 초기화에 실패했습니다: " + exception.Message, exception);
        }

        var navigator = new ClangdNavigator(options, context, session);
        // 엔진 파일 근사 명령에 쓸 모듈 위치를 미리 훑어 첫 요청이 기다리지 않게 합니다.
        if (context.ModuleGraph is { } graph) Observe(Task.Run(graph.Prepare));
        try
        {
            await navigator.StartBackgroundIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            navigator.Dispose();
            throw;
        }

        if (navigator.definitionSources is not null) Observe(Task.Run(navigator.RestoreDefinitionSources));
        return navigator;
    }

    public bool IsOpen(string path) => documents.Contains(path);

    /// <summary>clangd에 열린 문서의 최근 진단 중 오류 요약입니다. 오류가 없거나 열리지 않았으면 null입니다.</summary>
    public DocumentErrors? ErrorsOf(string path) => session.ErrorsOf(path);

    public async Task<NavigationResult> DefinitionAsync(NavigationQuery query, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var tracked = TrackRequest();
        SyncOpenDocuments(query);
        var opened = documents.Acquire(query.Document);
        try
        {
            // 임대를 받은 뒤에는 어떤 예외에도 finally가 풀도록 try 안에서 합니다(2026-10-09 검토 64).
            PrioritizeEditorDocument(query.Document);
            await AwaitPchCheckAsync(query.Path, opened, progress, cancellationToken).ConfigureAwait(false);
            var locations = await session.DefinitionAsync(query.Path, query.Line, query.Character, cancellationToken).ConfigureAwait(false);
            var symbol = default(SemanticSymbol);
            var resolved = false;
            if (MayNeedDefinitionFile(locations))
            {
                symbol = await session.SymbolInfoAsync(query.Path, query.Line, query.Character, cancellationToken, Spelled(query)).ConfigureAwait(false);
                // 요청 파일의 AST가 정의를 알면(헤더의 인라인 정의 등) 다른 파일을 열지 않습니다.
                if (symbol is { Definition: null })
                {
                    var found = await ResolveDefinitionFileAsync(query, symbol, locations[0].Path, progress, cancellationToken).ConfigureAwait(false);
                    if (found is not null)
                    {
                        locations = found;
                        resolved = true;
                    }
                }
            }

            var kind = default(SourceSymbolKind?);
            if (locations.Count > 1)
            {
                // 후보 목록을 보일 때만 종류를 찾습니다. 하나면 바로 이동하므로 추가 요청을 하지 않습니다.
                symbol ??= await session.SymbolInfoAsync(query.Path, query.Line, query.Character, cancellationToken, Spelled(query)).ConfigureAwait(false);
                kind = await SymbolKindAsync(symbol, locations, cancellationToken).ConfigureAwait(false);
            }

            return new NavigationResult(locations, symbol, Progress, resolved, kind, resolvedFromEngine: resolved && locations.Any(l => IsEngine(l.Path)));
        }
        finally
        {
            documents.Release(query.Path);
        }
    }

    /// <summary>
    /// 참조를 찾습니다. 함수 정의가 색인에 없으면(첫 색인 중이거나 명령이 없던 파일) 정의 파일을 요청 시점에 열어 확정한 뒤 다시 찾습니다.
    /// </summary>
    /// <remarks>
    /// 정의 파일을 편집기에서 열어 두지 않으면 정의·참조가 빠지던 문제(2026-10-08 회사 사용 피드백)에 대응합니다.
    /// 판단 근거는 같은 요청에서 함께 받은 정의 이동 결과이며, 그 결과가 늦었으면(역할 상한 초과) 열지 않습니다.
    /// </remarks>
    public async Task<NavigationResult> ReferencesAsync(NavigationQuery query, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        using var tracked = TrackRequest();
        var (result, definitions) = await ReferencesCoreAsync(query, progress, cancellationToken).ConfigureAwait(false);
        // 보조 요청 중 clangd가 종료했으면(ReferencesCoreAsync) 받은 결과만 돌려줍니다.
        if (definitions is null || HasExited || !MayNeedDefinitionFile(definitions) || result.Symbol is not { Definition: null } symbol)
        {
            return result;
        }

        // 확정 뒤 요청 문서에 정의를 다시 물으므로, 후보 파일을 여는 동안 요청 문서가 열린 문서 상한으로 닫히지 않게 붙잡아 둡니다
        // (2026-10-09 정확도 시험: 닫힌 문서에 요청해 참조 탐색 전체가 실패).
        documents.Acquire(query.Document);
        IReadOnlyList<NavigationLocation>? found;
        try
        {
            found = await ResolveDefinitionFileAsync(query, symbol, definitions[0].Path, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            documents.Release(query.Path);
        }

        if (found is null)
        {
            return result;
        }

        var (again, _) = await ReferencesCoreAsync(query, progress, cancellationToken).ConfigureAwait(false);
        return new NavigationResult(again.Locations, again.Symbol, again.Progress, true, again.SymbolKind, again.Roles, again.Limited, again.UncheckedDefinitionFiles,
            found.Any(l => IsEngine(l.Path)));
    }

    private async Task<(NavigationResult Result, IReadOnlyList<NavigationLocation>? Definitions)> ReferencesCoreAsync(NavigationQuery query,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        SyncOpenDocuments(query);
        var opened = documents.Acquire(query.Document);
        // 역할 표식 근거(선언 제외 참조, 정의·선언 이동)는 표시 보조라 본 요청 뒤에 함께 보내고, 본 결과가 온 뒤 정한 시간까지만 기다립니다.
        // clangd는 한 파일의 AST 요청을 받은 순서대로 하나씩 처리하고 이미 시작한 요청은 취소로 멈추지 않습니다. 그래서 결과 표시에 꼭 필요한
        // symbolInfo와 짧은 정의·선언 요청을 본 요청 바로 뒤에 두고, 본 요청만큼 걸릴 수 있는 선언 제외 참조는 맨 뒤에 보냅니다(2026-10-07 검토).
        // 늦거나 실패하면 그 근거 없이 정합니다. 본 요청이 실패해도 남은 보조 요청은 finally에서 취소하고 예외를 관측합니다.
        using var roleLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var auxiliary = new List<Task>(4);
        try
        {
            PrioritizeEditorDocument(query.Document);
            await AwaitPchCheckAsync(query.Path, opened, progress, cancellationToken).ConfigureAwait(false);
            var all = session.ReferencesCountedAsync(query.Path, query.Line, query.Character, true, cancellationToken);
            var symbolInfo = session.SymbolInfoAsync(query.Path, query.Line, query.Character, cancellationToken, Spelled(query));
            auxiliary.Add(symbolInfo);
            var definition = Quietly(session.DefinitionAsync(query.Path, query.Line, query.Character, roleLimit.Token));
            auxiliary.Add(definition);
            var declaration = Quietly(session.DeclarationAsync(query.Path, query.Line, query.Character, roleLimit.Token));
            auxiliary.Add(declaration);
            var uses = Quietly(session.ReferencesAsync(query.Path, query.Line, query.Character, false, roleLimit.Token));
            auxiliary.Add(uses);
            var (locations, rawCount) = await all.ConfigureAwait(false);
            // clangd는 상한까지 색인에 물은 뒤 일부를 버립니다: 요청 문서 위치(AST 결과를 먼저 넣음)와, 열린 문서의 위치(열린 문서의 참조는
            // 열린 AST 쪽 색인에서 따로 받음, clangd MergedIndex). 그래서 상한에 걸려도 결과가 상한보다 열린 문서의 참조 수만큼 적을 수 있고,
            // LSP 응답에는 잘렸다는 표시가 없습니다(2026-10-09 정확도 시험: 상한 5000에 4995개·4459개). 열린 문서의 참조 수만큼 여유를 두고
            // 판단합니다. 실제 개수가 상한 바로 아래면 잘리지 않았어도 표시될 수 있습니다.
            var inOpen = locations.Count(l => documents.Contains(l.Path));
            var limited = rawCount + inOpen >= ReferenceLimit;
            roleLimit.CancelAfter(RoleTimeout);
            // clangd 22.1.3은 일부 매크로 호출 위에서 symbolInfo를 받으면 종료합니다(같은 위치의 본 참조 요청은 응답함, Unreal 엔진 헤더의
            // 매크로 인수로 넘긴 매크로를 바로 #undef한 호출, 2026-10-09 정확도 시험). 본 결과는 이미 받았으므로 그 뒤 보조 요청 중 연결이 끊기면
            // 그때까지 정리한 결과를 돌려줍니다. 종료한 clangd는 다음 요청에서 다시 시작합니다.
            SemanticSymbol? symbol = null;
            var sites = new List<SiteReferences>();
            var uncheckedDefinitions = 0;
            var lost = false;
            try
            {
                symbol = await symbolInfo.ConfigureAwait(false);
                if (symbol is not null)
                {
                    locations = await WithoutRelatedSymbolsAsync(query, symbol, locations, cancellationToken).ConfigureAwait(false);
                    if (!limited)
                    {
                        if (await DeclarationSiteReferencesAsync(query, symbol, locations, progress, cancellationToken).ConfigureAwait(false) is { } site)
                        {
                            sites.Add(site);
                        }

                        var (others, skipped) = await OtherDefinitionReferencesAsync(symbol, progress, cancellationToken).ConfigureAwait(false);
                        sites.AddRange(others);
                        uncheckedDefinitions = skipped;
                        if (sites.Count > 0)
                        {
                            // 다른 위치에서 찾은 결과에도 clangd가 더한 기반·재정의 함수 위치가 들어 있으므로 새로 생긴 위치만 같은 기준으로 거릅니다
                            // (2026-10-09 독립 표본: 선언 파일에서 다시 찾은 결과의 기반 클래스 선언이 남았음).
                            var known = new HashSet<NavigationLocation>(locations);
                            var added = await WithoutRelatedSymbolsAsync(query, symbol,
                                sites.SelectMany(s => s.Locations).Distinct().Where(l => !known.Contains(l)).ToArray(), cancellationToken).ConfigureAwait(false);
                            locations = locations.Concat(added)
                                .OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Line).ThenBy(l => l.Character).ToArray();
                            limited = sites.Any(s => s.Limited);
                        }
                    }

                    if (!limited)
                    {
                        locations = await WithQualifierUsesAsync(query, symbol, locations, cancellationToken).ConfigureAwait(false);
                        locations = await WithMacroArgumentUsesAsync(query, symbol, locations, cancellationToken).ConfigureAwait(false);
                        locations = await WithUnresolvedCallUsesAsync(query, symbol, locations, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (LspConnectionClosedException)
            {
                lost = true;
            }
            catch (LspRequestException exception)
            {
                // 보조 요청 하나가 clangd 오류로 끝나도 본 결과는 이미 받았으므로 그때까지 정리한 결과를 돌려줍니다(2026-10-09 검토 41).
                AuxiliaryFailed?.Invoke($"참조 보완 단계에서 clangd 요청이 실패해 받은 결과만 돌려줍니다: {exception.Message}");
            }

            var kind = lost ? null : await SymbolKindAsync(symbol, locations, cancellationToken).ConfigureAwait(false);
            var plain = await uses.ConfigureAwait(false);
            // 다른 위치에서 더 찾았으면 요청 위치의 선언 제외 결과도 같은 이유로 빠진 곳이 있으므로 그 위치들의 것과 합칩니다.
            if (sites.Count > 0) plain = plain is null || sites.Any(s => s.Uses is null) ? null : plain.Concat(sites.SelectMany(s => s.Uses!)).Distinct().ToArray();
            // 결과 수 제한에 걸리면 두 참조 결과가 서로 다른 위치에서 잘려 차이가 선언 묶음이 아닙니다.
            if (plain is not null && (limited || plain.Count >= ReferenceLimit)) plain = null;
            var definitions = await definition.ConfigureAwait(false);
            var roles = ReferenceRoles.Classify(locations, plain, symbol?.Definition, symbol?.Declaration,
                definitions, await declaration.ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
            return (new NavigationResult(locations, symbol, Progress, false, kind, roles, limited, uncheckedDefinitions), definitions);
        }
        finally
        {
            roleLimit.Cancel();
            // 본 요청이 먼저 실패하면 기다리지 않은 요청이 남습니다. 그 예외를 관측해 미관측 작업 예외로 남기지 않습니다.
            foreach (var task in auxiliary)
            {
                _ = task.ContinueWith(done => _ = done.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            documents.Release(query.Path);
        }
    }

    /// <summary>
    /// clangd가 가상 함수의 참조에 더하는 다른 심볼의 위치를 뺍니다. clangd는 가상 함수를 찾으면 그 함수가 재정의한 기반 함수의 모든 참조
    /// (기반 선언, <c>Super::F()</c> 호출, 기반 포인터로 부른 곳)와 재정의한 함수들의 선언·정의를 함께 돌려주고 끄는 옵션이 없습니다
    /// (clangd 22.1 XRefs.cpp). 참조 목록은 찾은 그 함수만 보이므로(2026-10-09 정확도 시험) 위치마다 실제 심볼을 확인합니다.
    /// </summary>
    /// <remarks>
    /// 요청 문서 안의 위치는 열린 AST에 symbolInfo로 묻고, 다른 파일은 clangd 색인 파일의 그 위치 참조 기록을 봅니다(<see cref="ClangdIndexShards"/>).
    /// 그 위치에 다른 심볼의 기록만 있을 때만 빼고, 기록이 없거나 색인 파일을 믿을 수 없으면(원본이 더 새로움, 형식이 다름) 남깁니다.
    /// 가상 함수가 아니면 clangd가 더하는 위치가 없으므로 확인하지 않습니다.
    /// </remarks>
    private async Task<IReadOnlyList<NavigationLocation>> WithoutRelatedSymbolsAsync(NavigationQuery query, SemanticSymbol symbol,
        IReadOnlyList<NavigationLocation> locations, CancellationToken cancellationToken)
    {
        if (symbol.Ids.Count == 0 || locations.Count == 0 || !IsVirtualFunction(symbol)) return locations;
        var targets = new HashSet<string>(symbol.Ids, StringComparer.OrdinalIgnoreCase);
        var dropped = new HashSet<NavigationLocation>();
        foreach (var group in locations.GroupBy(l => l.Path, StringComparer.OrdinalIgnoreCase))
        {
            // clangd에 열린 다른 문서는 저장하지 않은 내용으로 분석되어 참조 위치가 디스크 기준 색인 파일과 어긋날 수 있으므로 요청 문서처럼
            // 열린 AST에 묻습니다(2026-10-09 검토 40). 확인하는 동안 닫히지 않게 사용 순서를 바꾸지 않고 임대합니다. 보낸 버전의 분석이 아직
            // 끝나지 않았으면 묻는 동안 분석을 기다리게 되므로 색인 파일로 확인합니다(검토 53).
            var current = string.Equals(group.Key, query.Path, StringComparison.OrdinalIgnoreCase);
            var leased = !current && LeaseAnalyzed(group.Key) is not null;
            if (current || leased)
            {
                try
                {
                    foreach (var location in group)
                    {
                        var at = await session.SymbolInfoAsync(location.Path, location.Line, location.Character, cancellationToken).ConfigureAwait(false);
                        if (at is { Ids.Count: > 0 } && !at.Ids.Any(targets.Contains)) dropped.Add(location);
                    }
                }
                finally
                {
                    if (leased) documents.Release(group.Key, touch: false);
                }

                continue;
            }

            var indexed = await Task.Run(() => shards.ReferencesIn(Context.Paths.ToReal(group.Key)), cancellationToken).ConfigureAwait(false);
            var byStart = indexed?.ToLookup(r => (r.Line, r.Character));
            string? text = null;
            foreach (var location in group)
            {
                var here = byStart?[(location.Line, location.Character)].ToArray();
                if (here is { Length: > 0 })
                {
                    if (!here.Any(r => targets.Contains(r.SymbolId))) dropped.Add(location);
                    continue;
                }

                text ??= SourceLinePreview.ReadText(group.Key) ?? string.Empty;
                if (IsOtherClassVirtualDeclaration(symbol, location, SourceLinePreview.LineAt(text, location.Line))) dropped.Add(location);
            }
        }

        return dropped.Count == 0 ? locations : locations.Where(l => !dropped.Contains(l)).ToArray();
    }

    /// <summary>
    /// 색인 파일로 확인하지 못한 위치(그 파일 기록이 아직 없음)가 다른 클래스의 가상 함수 선언인지 봅니다. clangd 색인 파일은 방금 색인한
    /// TU의 헤더 기록을 색인 뒤 조금 늦게 쓰므로, 바로 묻으면 기반 선언이 남았습니다(2026-10-09 정확도 시험). clangd가 참조마다 주는 container
    /// (선언은 그 선언이 든 클래스, 재정의 선언은 <c>클래스::함수</c>)가 찾은 함수의 클래스와 다르고, 그 줄이 이름 뒤에 <c>(</c>가 오는
    /// <c>virtual</c>·<c>override</c>·<c>final</c> 선언이며 이름 앞에 한정자·멤버 접근이 없을 때만 참입니다.
    /// </summary>
    /// <remarks>
    /// 클래스는 마지막 이름만 템플릿 인수를 빼고 비교합니다. 네임스페이스 표기(익명 네임스페이스 등)가 출처마다 달라 찾은 함수 자신의 선언을
    /// 빼는 일이 없게 하려는 것이며, 이름이 같은 다른 네임스페이스의 클래스는 남깁니다. container가 없으면 남깁니다.
    /// </remarks>
    public static bool IsOtherClassVirtualDeclaration(SemanticSymbol symbol, NavigationLocation location, string line)
    {
        if (location.Container is not { } container || symbol.ContainerName.Length == 0 || symbol.Name.Length == 0) return false;
        if (container.EndsWith("::" + symbol.Name, StringComparison.Ordinal)) container = container.Substring(0, container.Length - symbol.Name.Length - 2);
        if (string.Equals(LastClassName(container), LastClassName(symbol.ContainerName), StringComparison.Ordinal)) return false;
        var start = location.Character;
        if (start < 0 || start + symbol.Name.Length > line.Length || string.CompareOrdinal(line, start, symbol.Name, 0, symbol.Name.Length) != 0) return false;
        var after = line.Substring(start + symbol.Name.Length).TrimStart();
        var before = line.Substring(0, start).TrimEnd();
        // 선언 머리에서는 이름 앞에 반환형만 옵니다. 한 줄 inline 본문·기본 인수 안의 호출(`virtual void Foo() override { Bar(); }`)은 이름 앞에
        // 여는 괄호·중괄호·문장 끝·대입이 있으므로 선언으로 보지 않습니다(2026-10-09 검토 47).
        if (!after.StartsWith("(", StringComparison.Ordinal) || before.EndsWith("::", StringComparison.Ordinal) ||
            before.EndsWith(".", StringComparison.Ordinal) || before.EndsWith("->", StringComparison.Ordinal) || before.IndexOfAny(new[] { '{', '}', ';', '(', '=' }) >= 0)
        {
            return false;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(line, @"\b(virtual|override|final)\b");
    }

    /// <summary><c>ns::TFoo&lt;T&gt;</c>에서 <c>TFoo</c>처럼 마지막 이름을 템플릿 인수 없이 돌려줍니다.</summary>
    private static string LastClassName(string qualified)
    {
        var name = qualified.TrimEnd(':');
        var depth = 0;
        var plain = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch == '<') depth++;
            else if (ch == '>' && depth > 0) depth--;
            else if (depth == 0) plain.Append(ch);
        }

        var text = plain.ToString();
        var separator = text.LastIndexOf("::", StringComparison.Ordinal);
        return separator < 0 ? text : text.Substring(separator + 2);
    }

    /// <summary>요청 위치가 아닌 다른 위치(선언, 다른 정의)에서 같은 심볼을 찾은 참조 결과입니다.</summary>
    private sealed class SiteReferences
    {
        public SiteReferences(IReadOnlyList<NavigationLocation> locations, bool limited, IReadOnlyList<NavigationLocation>? uses)
        {
            Locations = locations;
            Limited = limited;
            Uses = uses;
        }

        /// <summary>그 위치에서 찾은 선언 포함 참조입니다.</summary>
        public IReadOnlyList<NavigationLocation> Locations { get; }

        /// <summary>그 위치의 결과가 결과 수 상한에 걸렸습니다.</summary>
        public bool Limited { get; }

        /// <summary>그 위치의 선언 제외 참조 결과입니다. 받지 못했으면 null입니다.</summary>
        public IReadOnlyList<NavigationLocation>? Uses { get; }
    }

    /// <summary>한 요청에서 다른 모듈 정의 헤더를 열어 확인하는 최대 수입니다. 정의 헤더마다 문서 열기와 참조 요청 하나가 듭니다.</summary>
    private const int MaxDefinitionFiles = 64;

    private IReadOnlyList<string>? definitionFiles;

    /// <summary>
    /// Unreal 정의 헤더(<see cref="GeneratedDefinitionMacros"/>)에 정의된 매크로면, 같은 이름을 정의한 다른 모듈의 정의 헤더마다 그
    /// <c>#define</c> 위치에서 참조를 찾습니다. 정의 헤더 목록은 database 명령이 강제 include하는 파일입니다. 정의 헤더는 매크로 정의뿐이므로
    /// 컴파일러와 파일만 둔 명령으로 엽니다(명령이 없으면 clangd가 가까운 TU 명령을 빌려 공유 PCH까지 분석). 결과 상한에 걸리면 멈춥니다.
    /// 상한은 그 매크로를 실제로 정의한 헤더에만 적용하고(글자로 먼저 거름), 넘어 확인하지 못한 수를 함께 돌려줍니다(2026-10-09 검토 46).
    /// </summary>
    private async Task<(IReadOnlyList<SiteReferences> Sites, int Unchecked)> OtherDefinitionReferencesAsync(SemanticSymbol symbol, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (Context.Kind != CompileContextKind.Unreal || GeneratedDefinitionMacros.NameOf(symbol.Usr) is not { } name) return (Array.Empty<SiteReferences>(), 0);
        var own = symbol.PrimaryDeclaration?.Path;
        var defining = (definitionFiles ??= GeneratedDefinitionMacros.FilesIn(Context.Commands))
            .Where(f => own is null || !string.Equals(Path.GetFullPath(f), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase))
            .Select(f => (File: f, Define: SourceLinePreview.ReadText(f) is { } text ? GeneratedDefinitionMacros.DefineOf(text, name) : null))
            .Where(f => f.Define is not null)
            .ToArray();
        var found = new List<SiteReferences>();
        var visited = 0;
        foreach (var (file, define) in defining.Take(MaxDefinitionFiles))
        {
            visited++;
            var site = await ReferencesAtAsync(file, define!.Value.Line, define.Value.Character, name, at => GeneratedDefinitionMacros.Same(at.Usr, symbol.Usr),
                DefinitionsCommand(file), "다른 모듈 정의에서 참조 확인 중", progress, cancellationToken).ConfigureAwait(false);
            if (site is null) continue;
            found.Add(site);
            if (site.Limited) return (found, 0);
        }

        return (found, defining.Length - visited);
    }

    /// <summary>정의 헤더를 열 때 쓰는 명령입니다: database 첫 명령의 컴파일러(와 드라이버 모드), C++ 지정, 파일.</summary>
    private CompileCommand? DefinitionsCommand(string path)
    {
        if (Context.Commands.FirstOrDefault()?.Arguments is not { Count: > 0 } sample) return null;
        var arguments = new List<string> { sample[0] };
        if (sample.Count > 1 && sample[1].StartsWith("--driver-mode=", StringComparison.Ordinal)) arguments.Add(sample[1]);
        arguments.Add(arguments.Contains("--driver-mode=cl") ? "/TP" : "-xc++");
        arguments.Add(path);
        return new CompileCommand(Path.GetDirectoryName(path)!, path, arguments);
    }

    /// <summary>
    /// 다른 파일의 한 위치에서 참조를 찾습니다. 닫혀 있던 파일은 열었다가 닫습니다(<paramref name="command"/>가 있으면 그 명령으로 엶).
    /// 그 위치의 대표 심볼이 <paramref name="accept"/>를 만족하지 않거나 파일을 읽지 못하면 null입니다.
    /// </summary>
    private async Task<SiteReferences?> ReferencesAtAsync(string path, int line, int character, string name, Func<SemanticSymbol, bool> accept,
        CompileCommand? command, string stage, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        int? version = null;
        if (documents.AcquireIfOpen(path) is null)
        {
            if (SourceLinePreview.ReadText(path) is not { } text) return null;
            progress?.Report($"{stage}: {Path.GetFileName(path)}");
            if (command is not null) session.UpdateCompileCommands(new[] { command });
            version = documents.Acquire(new DocumentText(path, text));
        }

        try
        {
            if (version is { } opened) await AwaitPchCheckAsync(path, opened, progress, cancellationToken).ConfigureAwait(false);
            var at = await session.SymbolInfoAsync(path, line, character, cancellationToken, name).ConfigureAwait(false);
            if (at is null || !accept(at)) return null;
            var uses = Quietly(session.ReferencesAsync(path, line, character, false, cancellationToken));
            var (more, rawCount) = await session.ReferencesCountedAsync(path, line, character, true, cancellationToken).ConfigureAwait(false);
            var inOpen = more.Count(l => documents.Contains(l.Path));
            return new SiteReferences(more, rawCount + inOpen >= ReferenceLimit, await uses.ConfigureAwait(false));
        }
        finally
        {
            documents.Release(path);
            if (version is not null) documents.TryClose(path);
        }
    }

    /// <summary>
    /// 결과가 요청 문서 안에만 있고 대표 선언이 다른 파일에 있으면, 선언 위치에서 다시 찾아 합칩니다. 사용 위치에서 clangd가 이름을 그 위치의
    /// 재선언으로 잡고 그 재선언의 심볼 ID로 색인을 묻는데, 색인은 다른 ID로 기록해 다른 파일의 참조가 모두 빠지는 경우가 있습니다
    /// (<c>using</c> 별칭을 <c>typedef</c>로 다시 선언, 예: Unreal <c>FTransform</c>. clangd 22.1 확인, 2026-10-09 정확도 시험). 선언 위치에서
    /// 물으면 색인과 같은 ID로 찾습니다.
    /// </summary>
    /// <remarks>
    /// 다른 파일의 결과가 하나라도 있으면 색인 조회가 된 것이므로 보지 않습니다. 네임스페이스는 색인에 참조가 없어 원래 요청 파일만 찾으므로
    /// 보지 않습니다(<see cref="NavigationResult.CurrentFileOnly"/>). 선언 위치의 대표 심볼이 같은 USR일 때만 합치며, 닫혀 있던 선언 파일은
    /// 열었다가 닫습니다. 보완하지 않았으면 null입니다.
    /// </remarks>
    private Task<SiteReferences?> DeclarationSiteReferencesAsync(NavigationQuery query, SemanticSymbol symbol,
        IReadOnlyList<NavigationLocation> locations, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (symbol.PrimaryDeclaration is not { } declaration || symbol.Usr.Length == 0 || NavigationResult.IsNamespace(symbol.Usr) ||
            string.Equals(declaration.Path, query.Path, StringComparison.OrdinalIgnoreCase) ||
            locations.Any(l => !string.Equals(l.Path, query.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult<SiteReferences?>(null);
        }

        return ReferencesAtAsync(declaration.Path, declaration.Line, declaration.Character, symbol.Name,
            at => string.Equals(at.Usr, symbol.Usr, StringComparison.Ordinal), null, "선언 파일에서 참조 확인 중", progress, cancellationToken);
    }

    /// <summary>
    /// 선언 줄(선언 머리에서 <c>;</c>·<c>{</c>까지, 최대 다섯 줄)에 <c>virtual</c>·<c>override</c>·<c>final</c>이 있으면 가상 함수로 봅니다.
    /// 선언 위치를 모르면(심볼이 여럿) 거짓입니다.
    /// </summary>
    private static bool IsVirtualFunction(SemanticSymbol symbol)
    {
        if (symbol.Declaration is not { } declaration || symbol.ContainerName.Length == 0) return false;
        var text = SourceLinePreview.ReadText(declaration.Path);
        if (text is null) return false;
        var head = new System.Text.StringBuilder();
        for (var line = declaration.Line; line < declaration.Line + 5; line++)
        {
            var current = SourceLinePreview.LineAt(text, line);
            head.Append(' ').Append(current);
            if (current.IndexOf(';') >= 0 || current.IndexOf('{') >= 0) break;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(head.ToString(), @"\b(virtual|override|final)\b");
    }

    /// <summary>요청 문서에서 한정자 후보를 확인하는 최대 개수입니다. 후보마다 symbolInfo 요청 하나가 듭니다.</summary>
    private const int MaxQualifierChecks = 300;

    /// <summary>
    /// 요청 문서에서 clangd가 빠뜨린 한정자 사용(<c>이름::</c>)을 더합니다. clang 22 색인은 템플릿 인수가 붙은 이름 앞의 한정자
    /// (<c>std::vector&lt;int&gt;</c>의 <c>std</c>, <c>O::I&lt;int&gt;</c>의 <c>O</c>)를 참조로 남기지 않습니다(clangd 22.1 확인, 2026-10-09 정확도 시험).
    /// 요청 문서는 AST가 있으므로 결과에 없는 <c>이름::</c> 글자 위치마다 symbolInfo로 같은 심볼인지 확인합니다. 다른 파일은 색인에도 기록이
    /// 없어 보완하지 않습니다.
    /// </summary>
    private async Task<IReadOnlyList<NavigationLocation>> WithQualifierUsesAsync(NavigationQuery query, SemanticSymbol symbol,
        IReadOnlyList<NavigationLocation> locations, CancellationToken cancellationToken)
    {
        var name = symbol.Name;
        if (symbol.Usr.Length == 0 || symbol.Usr.Contains("@macro@") || name.Length == 0 || char.IsDigit(name[0]) || !name.All(IsWordChar)) return locations;
        var found = new HashSet<(int, int)>(locations.Where(l => string.Equals(l.Path, query.Path, StringComparison.OrdinalIgnoreCase)).Select(l => (l.Line, l.Character)));
        var added = new List<NavigationLocation>();
        var checks = 0;
        foreach (var (line, character) in QualifierCandidates(query.Document.Text, symbol.Name))
        {
            if (found.Contains((line, character))) continue;
            if (++checks > MaxQualifierChecks) break;
            var at = await session.SymbolInfoAsync(query.Path, line, character, cancellationToken, symbol.Name).ConfigureAwait(false);
            if (at is not null && at.Usr == symbol.Usr) added.Add(new NavigationLocation(query.Path, line, character, line, character + symbol.Name.Length));
        }

        if (added.Count == 0) return locations;
        return locations.Concat(added).OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Line).ThenBy(l => l.Character).ToArray();
    }

    /// <summary>
    /// 이미 열려 있고 보낸 버전의 분석을 마친 문서만 사용 순서를 바꾸지 않고 임대해 그 내용을 돌려줍니다. 아니면 null이며 임대하지 않습니다.
    /// 호출자는 <c>Release(path, touch: false)</c>로 풀어야 합니다.
    /// </summary>
    private string? LeaseAnalyzed(string path)
    {
        var text = documents.AcquireIfOpen(path, out var version, touch: false);
        if (text is null) return null;
        if (session.HasDiagnostics(path, version)) return text;
        documents.Release(path, touch: false);
        return null;
    }

    /// <summary>
    /// 오버로드를 정하지 못한 위치에서 찾았으면(<see cref="SemanticSymbol.OverloadIds"/>) clangd에 열린 문서에서 빠진 같은 이름 위치를 더합니다.
    /// clangd는 그 위치의 후보 함수 모두의 참조를 색인에서 찾지만, 열린 문서는 열린 AST의 참조로 대신하고 AST 참조에는 정해지지 않은 호출이
    /// 없어 누른 위치조차 결과에 없었습니다(2026-10-09 독립 표본: 템플릿 안의 <c>Forward&lt;Args&gt;(args)</c>, 색인 파일에는 기록 있음).
    /// 열린 문서(요청 문서 먼저)의 이름 위치마다 symbolInfo로 후보 중 하나를 가리키는지 확인합니다. 닫힌 파일은 색인 결과에 이미 들어 있습니다.
    /// </summary>
    private async Task<IReadOnlyList<NavigationLocation>> WithUnresolvedCallUsesAsync(NavigationQuery query, SemanticSymbol symbol,
        IReadOnlyList<NavigationLocation> locations, CancellationToken cancellationToken)
    {
        if (symbol.OverloadIds.Count < 2) return locations;
        var name = symbol.Name;
        var candidates = new HashSet<string>(symbol.OverloadIds, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<(string, int, int)>(locations.Select(l => (l.Path.ToUpperInvariant(), l.Line, l.Character)));
        var added = new List<NavigationLocation>();
        var checks = 0;
        var others = documents.OpenPaths.Where(p => !string.Equals(p, query.Path, StringComparison.OrdinalIgnoreCase));
        foreach (var path in new[] { query.Path }.Concat(others))
        {
            if (checks > MaxQualifierChecks) break;
            cancellationToken.ThrowIfCancellationRequested();
            var current = string.Equals(path, query.Path, StringComparison.OrdinalIgnoreCase);
            // 요청 문서는 이 요청이 이미 붙잡고 있습니다. 다른 문서는 분석을 마친 것만 사용 순서를 바꾸지 않고 임대해 훑습니다(검토 53·55).
            var text = current ? query.Document.Text : LeaseAnalyzed(path);
            if (text is null) continue;
            try
            {
                foreach (var word in CodeWords.Find(text, name))
                {
                    if (word.Directive || found.Contains((path.ToUpperInvariant(), word.Line, word.Character))) continue;
                    if (++checks > MaxQualifierChecks) break;
                    var at = await session.SymbolInfoAsync(path, word.Line, word.Character, cancellationToken, name).ConfigureAwait(false);
                    if (at is not null && at.Ids.Any(candidates.Contains)) added.Add(new NavigationLocation(path, word.Line, word.Character, word.Line, word.Character + name.Length));
                }
            }
            finally
            {
                if (!current) documents.Release(path, touch: false);
            }
        }

        if (added.Count == 0) return locations;
        return locations.Concat(added).OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Line).ThenBy(l => l.Character).ToArray();
    }

    /// <summary>
    /// 매크로 참조에 clangd가 빠뜨린, 다른 매크로의 인수 안에서 쓴 위치를 더합니다. clangd는 매크로 이름의 위치가 매크로 위치(macro ID)이면
    /// 기록하지 않는데, <c>##__VA_ARGS__</c>로 넘긴 인수처럼 미리 펼치지 않는 인수 안의 매크로는 바깥 매크로를 펼친 뒤 다시 훑을 때 펼쳐져
    /// 그렇게 됩니다(예: <c>UE_LOG(…, TEXT("%s"), Cond ? TEXT("a") : TEXT("b"))</c>의 뒤 두 TEXT, 2026-10-09 정확도 시험).
    /// </summary>
    /// <remarks>
    /// clangd에 열린 문서(요청 문서 포함)는 결과에 없는 이름 위치를 symbolInfo로 확인합니다. 닫힌 파일은 결과에 이미 나온 파일만 보며,
    /// 색인 파일에 그 매크로 기록이 있고(그 파일 문맥에서 같은 매크로가 정의됨) 이름이 놓인 조건부 구역에 다른 참조 기록이 있으면(활성 구역)
    /// 더합니다. 결과에 한 번도 나오지 않은 파일은 찾지 못합니다.
    /// </remarks>
    private async Task<IReadOnlyList<NavigationLocation>> WithMacroArgumentUsesAsync(NavigationQuery query, SemanticSymbol symbol,
        IReadOnlyList<NavigationLocation> locations, CancellationToken cancellationToken)
    {
        if (!symbol.Usr.Contains("@macro@") || symbol.Id.Length == 0 || locations.Count == 0) return locations;
        var name = symbol.Name;
        var found = new HashSet<(string, int, int)>(locations.Select(l => (l.Path.ToUpperInvariant(), l.Line, l.Character)));
        var added = new List<NavigationLocation>();
        var checks = 0;
        bool? functionLike = null;
        foreach (var path in new[] { query.Path }.Concat(locations.Select(l => l.Path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 분석을 마친 열린 문서만 AST에 묻고, 분석 중인 문서는 닫힌 파일처럼 색인 파일로 봅니다(검토 53).
            var current = string.Equals(path, query.Path, StringComparison.OrdinalIgnoreCase);
            var openText = current ? documents.AcquireIfOpen(path, out _, touch: false) : LeaseAnalyzed(path);
            if (openText is not null)
            {
                try
                {
                    foreach (var word in CodeWords.Find(openText, name))
                    {
                        if (word.Directive || found.Contains((path.ToUpperInvariant(), word.Line, word.Character))) continue;
                        if (++checks > MaxQualifierChecks) break;
                        var at = await session.SymbolInfoAsync(path, word.Line, word.Character, cancellationToken, name).ConfigureAwait(false);
                        if (at is not null && at.Usr == symbol.Usr) added.Add(new NavigationLocation(path, word.Line, word.Character, word.Line, word.Character + name.Length));
                    }
                }
                finally
                {
                    documents.Release(path, touch: false);
                }

                continue;
            }

            var text = SourceLinePreview.ReadText(path);
            if (text is null) continue;
            // 함수형 매크로는 이름 뒤에 '('가 와야 펼쳐지고, #undef 뒤의 같은 이름은 그 매크로가 아닙니다(2026-10-09 검토 48).
            functionLike ??= IsFunctionLikeMacro(symbol);
            var undefined = UndefinedLines(text, name);
            var words = CodeWords.Find(text, name)
                .Where(w => !w.Directive && !found.Contains((path.ToUpperInvariant(), w.Line, w.Character)) && !undefined(w.Line) &&
                            (functionLike != true || FollowedByParenthesis(SourceLinePreview.LineAt(text, w.Line), w.Character + name.Length)))
                .ToArray();
            if (words.Length == 0) continue;
            var indexed = await Task.Run(() => shards.ReferencesIn(Context.Paths.ToReal(path)), cancellationToken).ConfigureAwait(false);
            if (indexed is null || !indexed.Any(r => r.SymbolId == symbol.Id)) continue;
            var referencedLines = indexed.Select(r => r.Line).Distinct().OrderBy(l => l).ToArray();
            var conditionals = CodeWords.ConditionalLines(text);
            foreach (var word in words)
            {
                var before = -1;
                var after = int.MaxValue;
                foreach (var line in conditionals)
                {
                    if (line < word.Line) before = line;
                    else if (line > word.Line)
                    {
                        after = line;
                        break;
                    }
                }

                if (referencedLines.Any(l => l > before && l < after)) added.Add(new NavigationLocation(path, word.Line, word.Character, word.Line, word.Character + name.Length));
            }
        }

        if (added.Count == 0) return locations;
        return locations.Concat(added).OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Line).ThenBy(l => l.Character).ToArray();
    }

    /// <summary>매크로 정의 줄이 <c>#define 이름(</c>(이름과 괄호 사이 공백 없음)이면 함수형입니다. 정의를 읽지 못하면 null입니다.</summary>
    private static bool? IsFunctionLikeMacro(SemanticSymbol symbol)
    {
        if ((symbol.PrimaryDeclaration ?? symbol.Declaration) is not { } declaration || SourceLinePreview.ReadText(declaration.Path) is not { } text) return null;
        var line = SourceLinePreview.LineAt(text, declaration.Line);
        var end = declaration.Character + symbol.Name.Length;
        if (end > line.Length || string.CompareOrdinal(line, declaration.Character, symbol.Name, 0, symbol.Name.Length) != 0) return null;
        return end < line.Length && line[end] == '(';
    }

    private static bool FollowedByParenthesis(string line, int index)
    {
        while (index < line.Length && (line[index] == ' ' || line[index] == '\t')) index++;
        return index < line.Length && line[index] == '(';
    }

    /// <summary>그 줄이 <c>#undef 이름</c> 뒤이고 다시 <c>#define 이름</c>하기 전인지 알려 주는 함수입니다.</summary>
    public static Func<int, bool> UndefinedLines(string text, string name)
    {
        var directive = new System.Text.RegularExpressions.Regex(@"^[ \t]*#[ \t]*(?<kind>undef|define)[ \t]+" + System.Text.RegularExpressions.Regex.Escape(name) + @"\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var changes = new List<(int Line, bool Undefined)>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = directive.Match(lines[i]);
            if (match.Success) changes.Add((i, match.Groups["kind"].Value == "undef"));
        }

        if (!changes.Any(c => c.Undefined)) return _ => false;
        return line =>
        {
            var state = false;
            foreach (var change in changes)
            {
                if (change.Line >= line) break;
                state = change.Undefined;
            }

            return state;
        };
    }

    /// <summary>
    /// 글자에서 <c>이름</c> 바로 뒤에 (템플릿 인수를 건너뛰고) <c>::</c>가 오는 위치입니다. 주석·문자열 안도 후보가 되지만 symbolInfo 확인에서 걸러집니다.
    /// </summary>
    public static IEnumerable<(int Line, int Character)> QualifierCandidates(string text, string name)
    {
        var lines = text.Split('\n');
        for (var line = 0; line < lines.Length; line++)
        {
            var current = lines[line];
            var index = 0;
            while ((index = current.IndexOf(name, index, StringComparison.Ordinal)) >= 0)
            {
                var start = index;
                index += name.Length;
                if (start > 0 && IsWordChar(current[start - 1]) || index < current.Length && IsWordChar(current[index])) continue;
                var after = index;
                while (after < current.Length && current[after] == ' ') after++;
                if (after < current.Length && current[after] == '<')
                {
                    var depth = 0;
                    for (; after < current.Length; after++)
                    {
                        if (current[after] == '<') depth++;
                        else if (current[after] == '>' && --depth == 0)
                        {
                            after++;
                            break;
                        }
                    }

                    while (after < current.Length && current[after] == ' ') after++;
                }

                if (after + 1 < current.Length && current[after] == ':' && current[after + 1] == ':') yield return (line, start);
            }
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string? Spelled(NavigationQuery query) => IdentifierAt(query.Document.Text, query.Line, query.Character);

    /// <summary>위치(식별자 안이나 바로 뒤)에 쓰인 식별자입니다. 식별자가 아니면 null입니다. 위치는 0기반 줄과 UTF-16 문자 위치입니다.</summary>
    public static string? IdentifierAt(string text, int line, int character)
    {
        var lineText = SourceLinePreview.LineAt(text, line);
        if (character < 0 || character > lineText.Length) return null;
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        var start = character;
        while (start > 0 && IsWord(lineText[start - 1])) start--;
        var end = character;
        while (end < lineText.Length && IsWord(lineText[end])) end++;
        return end == start || char.IsDigit(lineText[start]) ? null : lineText.Substring(start, end - start);
    }

    /// <summary>표시 보조 요청의 결과입니다. 시간 상한·취소·요청 거절·연결 끊김은 null(근거 없음)로 둡니다.</summary>
    private static async Task<IReadOnlyList<NavigationLocation>?> Quietly(Task<IReadOnlyList<NavigationLocation>> request)
    {
        try
        {
            return await request.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is LspRequestException || exception is LspConnectionClosedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 결과 색칠용 종류입니다. 표시 보조일 뿐이므로 자체 상한 초과·요청 거절·연결 끊김은 종류 없음으로 두고 이미 받은 결과를 그대로 돌려줍니다.
    /// 호출자의 취소만 전파합니다.
    /// </summary>
    private async Task<SourceSymbolKind?> SymbolKindAsync(SemanticSymbol? symbol, IReadOnlyList<NavigationLocation> locations, CancellationToken cancellationToken)
    {
        if (symbol is null) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(SymbolKindTimeout);
        try
        {
            return await session.SymbolKindAsync(symbol, locations, limit.Token).ConfigureAwait(false) ?? KindFromUsr(symbol.Usr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return KindFromUsr(symbol.Usr);
        }
        catch (Exception exception) when (exception is LspRequestException || exception is LspConnectionClosedException)
        {
            return KindFromUsr(symbol.Usr);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex RecordUsr = new(
        @"@(?<kind>S|U|E|ST>[^@]*|SP>[^@]*)@[A-Za-z_][A-Za-z0-9_]*(?:>[^@]*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>workspace/symbol</c>로 종류를 정하지 못했을 때(1초 상한, 방금 연 파일의 심볼이 색인에 아직 없음) clang USR의 마지막 구성 요소로
    /// 클래스·구조체(<c>@S@</c>, 클래스 템플릿 <c>@ST&gt;</c>, 부분 특수화 <c>@SP&gt;</c>. USR로는 class와 struct를 구분할 수 없어 <see cref="SourceSymbolKind.Type"/>),
    /// 공용체(<c>@U@</c>), 열거형(<c>@E@</c>)만 알아봅니다. 함수·필드 USR은 매개변수 형식 등에 <c>@S@…</c>가 들어갈 수 있으므로 먼저 뺍니다.
    /// 종류가 늦어 생성자·소멸자 이름 거름(타입일 때만 적용)이 빠지던 문제(2026-10-09 정확도 시험)에 대응합니다.
    /// </summary>
    public static SourceSymbolKind? KindFromUsr(string usr)
    {
        if (usr.Contains("@F@") || usr.Contains("@FT@") || usr.Contains("@FI@") || usr.Contains("@macro@")) return null;
        var match = RecordUsr.Match(usr);
        if (!match.Success) return null;
        return match.Groups["kind"].Value switch
        {
            "U" => SourceSymbolKind.Union,
            "E" => SourceSymbolKind.Enum,
            _ => SourceSymbolKind.Type
        };
    }

    /// <summary>편집기에서 활성화한 문서를 미리 열어 첫 요청 전에 분석을 시작합니다.</summary>
    public void Warm(DocumentText document)
    {
        if (HasExited) return;
        try
        {
            documents.Acquire(document);
            documents.Release(document.Path);
            PrioritizeEditorDocument(document);
        }
        catch (LspConnectionClosedException)
        {
            // 종료는 Changed로 알려집니다.
        }
    }

    /// <summary>clangd에 열려 있는 문서면 현재 내용을 보냅니다. 열려 있지 않으면 아무것도 하지 않습니다.</summary>
    public void Update(DocumentText document)
    {
        if (HasExited) return;
        try
        {
            documents.Update(document);
        }
        catch (LspConnectionClosedException)
        {
        }
    }

    /// <summary>편집기에서 저장한 내용을 색인에 반영합니다.</summary>
    public void Saved(DocumentText document)
    {
        if (HasExited) return;
        try
        {
            if (documents.Update(document) is not null)
            {
                session.SaveDocument(document.Path);
                return;
            }
        }
        catch (LspConnectionClosedException)
        {
            return;
        }

        lock (touchGate)
        {
            if (!touchTexts.ContainsKey(document.Path)) touchOrder.Enqueue(document.Path);
            touchTexts[document.Path] = document;
            if (touchRunning) return;
            touchRunning = true;
        }

        _ = Task.Run(RunTouchesAsync);
    }

    /// <summary>
    /// 편집기 밖에서 바뀐 파일을 디스크 내용으로 다시 분석해 색인에 반영합니다.
    /// 편집기에 열린 파일은 편집기 내용이 우선이므로 호출자가 걸러야 합니다.
    /// </summary>
    public void Reload(string path)
    {
        if (HasExited) return;
        var text = SourceLinePreview.ReadText(path);
        if (text is null) return;
        try
        {
            // 엔진 후보처럼 편집기 없이 열어 둔 문서도 디스크가 기준이므로 닫고 새 내용으로 다시 엽니다.
            documents.TryClose(path);
        }
        catch (LspConnectionClosedException)
        {
            return;
        }

        Saved(new DocumentText(path, text, 0));
    }

    /// <summary>편집기에서 마지막 창을 닫은 문서를 clangd에서도 닫습니다. 진행 중인 요청이 쓰고 있으면 그 요청이 끝날 때 닫습니다.</summary>
    public void Closed(string path)
    {
        if (HasExited) return;
        try
        {
            documents.CloseWhenReleased(path);
        }
        catch (LspConnectionClosedException)
        {
        }
    }

    public async Task ShutdownAsync(TimeSpan timeout)
    {
        lifetime.Cancel();
        await session.ShutdownAsync(timeout).ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        session.Dispose();
        lifetime.Dispose();
    }

    /// <summary>
    /// clangd는 compilation database를 첫 문서 요청 때 읽고 그때 background index를 시작합니다.
    /// 편집기 문서를 기다리지 않도록 캐시 폴더의 빈 문서를 잠시 열어 색인을 시작하게 합니다(디스크에 쓰지 않음).
    /// </summary>
    private async Task StartBackgroundIndexAsync(CancellationToken cancellationToken)
    {
        var probe = Context.IndexStartPath;
        var version = documents.Acquire(new DocumentText(probe, string.Empty));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await session.WaitForDiagnosticsAsync(probe, version, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 진단이 늦어도 색인은 이미 시작했을 수 있으므로 시작 자체를 실패로 보지 않습니다.
        }
        finally
        {
            documents.Release(probe);
            documents.TryClose(probe);
        }
    }

    /// <summary>
    /// 이전 clangd가 근사 명령으로 색인한 소스(<see cref="DefinitionSourceStore"/>)에 같은 명령을 다시 줘 저장된 색인을 읽게 합니다. clangd는
    /// 명령을 받은 파일의 색인 파일을 읽고, 내용이 바뀌었을 때만 다시 색인합니다. 시작 뒤 작업 스레드에서 부릅니다.
    /// </summary>
    /// <remarks>
    /// 원본보다 새 색인 파일이 있는 파일만 보냅니다. 색인 형식 변경으로 색인을 지웠거나 엔진을 업데이트한 뒤에 모두 보내면 기억한 엔진 cpp(최대
    /// 256개)가 프로젝트 첫 색인과 같은 대기열에 섞여 색인 완료가 크게 늦어집니다(2026-10-09 검토 59). 빠진 파일은 그 정의를 찾을 때 요청 시점에
    /// 다시 분석하고 기억합니다.
    /// </remarks>
    private void RestoreDefinitionSources()
    {
        var commands = new List<CompileCommand>();
        foreach (var file in definitionSources!.Load())
        {
            if (lifetime.IsCancellationRequested) return;
            if (!shards.HasCurrentShard(Context.Paths.ToReal(file))) continue;
            try
            {
                if (ApproximateCommand(file) is { } command) commands.Add(command);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // 재정의 헤더를 쓰지 못한 파일은 이번 시작에서 건너뛰고, 그 정의를 찾을 때 요청 시점에 다시 엽니다.
            }
        }

        if (commands.Count == 0 || HasExited) return;
        try
        {
            session.UpdateCompileCommands(commands);
        }
        catch (LspConnectionClosedException)
        {
            // 종료는 Changed로 알려집니다.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 문서를 clangd에 엽니다. Unreal 프로젝트는 database에 원래 파일 대신 색인 단위 합성 TU만 있어 clangd가 다른 파일의 명령을 추정해
    /// 분석에 실패하므로(테스트 전용 UE 샘플: 정의·참조 0개), 처음 열 때 자기 명령(헤더는 소속 모듈의 명령)을 덮어쓰기로 줍니다.
    /// 색인 단위가 이미 그 파일을 담고 있어 background index는 최신 여부만 확인하고 다시 분석하지 않았습니다(같은 샘플 0.7초, 2026-10-09 측정).
    /// 공유 PCH 없이 준 문서는 첫 진단에 오류가 있으면 PCH를 넣어 다시 분석합니다(<see cref="OnDiagnostics"/>).
    /// 색인 단위에 없는 파일(엔진 파일, 명령이 없는 프로젝트 모듈 파일)은 모듈 규칙으로 만든 근사 명령을 줍니다. clangd의 추정 명령은
    /// 프로젝트 모듈의 것이라 다른 모듈 헤더를 찾지 못해 심볼을 잃었습니다(2026-10-09 정확도 시험: 엔진 파일 다수).
    /// </summary>
    private void OpenDocument(string path, string text, int version)
    {
        if (Context.Plan is { } plan && documentCommandsSent.Add(path))
        {
            IReadOnlyList<string>? own;
            lock (pchGate) own = documentOwnSupplements.TryGetValue(path, out var found) ? found : null;
            if (plan.DocumentCommand(path, extra: own) is { } choice)
            {
                if (choice.WithoutPch && plan.Mode == UnrealPchMode.Auto)
                {
                    lock (pchGate)
                    {
                        pchPending.Add(path);
                        documentSupplements[path] = choice.Supplements;
                    }
                }

                session.UpdateCompileCommands(new[] { choice.Command });
            }
            else if (TryApproximateCommand(path) is { } command)
            {
                session.UpdateCompileCommands(new[] { command });
                // 근사 명령을 받은 소스는 clangd가 색인하므로 다음 clangd도 그 색인을 읽게 기억합니다. 열기 콜백은 문서 집합 잠금 안이라 파일
                // 쓰기는 작업 스레드에서 합니다.
                if (definitionSources is { } store && DefinitionCandidates.IsSource(path)) Observe(Task.Run(() => store.Record(path)));
            }
        }

        session.OpenDocument(path, text, version);
    }

    /// <summary>색인 대기열 앞당기기에 여는 빈 헤더의 폴더입니다(<see cref="IndexQueuePriority"/>).</summary>
    private string QueueMarkerDirectory => Path.Combine(Context.Directory, "queue");

    private bool IsQueueMarker(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        return directory is not null && string.Equals(directory.TrimEnd('\\', '/'), Path.GetFullPath(QueueMarkerDirectory).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 편집기 문서(예열한 초점 문서, 요청 문서)를 연 뒤 색인이 끝나지 않았으면 그 문서와 관련된 TU를 clangd 색인 대기열 앞으로 올립니다. 요청 중
    /// 잠시 여는 선언·정의 헤더와 정의 후보 cpp는 사용자가 보는 문서가 아니고 수가 많아(모듈 정의 헤더 최대 64개) 올리지 않습니다(2026-10-09 검토
    /// 60). 측정이 보인 것처럼 적게 올려야 효과가 있습니다.
    /// </summary>
    private void PrioritizeEditorDocument(DocumentText document)
    {
        if (!options.PrioritizeOpenDocuments || Progress.Completed || HasExited) return;
        RaiseIndexPriority(document.Path, document.Text);
    }

    /// <summary>
    /// 연 문서와 관련된 TU를 clangd 색인 대기열 앞으로 올립니다. 표시와 같은 이름의 빈 헤더를 잠시 열면 clangd가 그 이름의 TU를 올립니다
    /// (<see cref="IndexQueuePriority"/>). 표시만 정하고 파일 쓰기와 알림은 작업 스레드에서 합니다.
    /// </summary>
    /// <remarks>
    /// clangd의 앞당김은 한 단계뿐이라 함께 올린 작업 사이의 순서는 정해지지 않습니다. 관련 TU까지 한꺼번에 올리면 연 문서 자신(include한 헤더
    /// 전체가 함께 색인됨)이 그 뒤로 밀려, 실제 Unreal 프로젝트(TU 82개, 작업 2개)에서 문서 3개를 열자 색인까지 9·30·41초였습니다(2026-10-09,
    /// 앞당기지 않으면 11·35·137초). 문서마다 자신이 색인된 뒤 그 관련 TU를 올리면 먼저 끝난 문서의 관련 TU가 다른 연 문서와 다시 겹쳤습니다
    /// (9·64·83초). 그래서 연 문서 자신을 먼저 올리고, include한 헤더의 같은 이름 cpp TU는 자신을 올린 연 문서가 모두 색인되면 올립니다
    /// (같은 측정 5·8·7초, 공유 PCH가 필요한 문서 3개는 25·41·43초이며 앞당기지 않으면 12·69·210초). 이미 최신이라 다시 색인하지 않는 문서는
    /// 완료 알림이 없으므로 문서마다 <see cref="RelatedTagDelay"/>까지만 기다립니다.
    /// </remarks>
    /// <remarks>
    /// 빈 헤더에는 컴파일러만 있는 명령을 덮어쓰기로 줍니다. 명령이 없으면 clangd가 가까운 TU의 명령(공유 PCH 포함)을 빌려 빈 헤더에도 큰 헤더
    /// 분석을 합니다(색인 시작 문서와 같은 이유). 덮어쓰기로 받은 빈 헤더도 clangd가 색인하지만 내용이 없어 바로 끝나며, 색인 단위 수에는
    /// 넣지 않습니다.
    /// </remarks>
    private void RaiseIndexPriority(string path, string text)
    {
        Func<string, IEnumerable<string>> related = Context.Plan is { } plan
            ? stem => plan.QueueTags(stem, path)
            : stem => (commandTags ??= IndexQueuePriority.CommandTags(Context.Commands))[stem];
        var all = IndexQueuePriority.TagsFor(path, text, related);
        var key = Path.GetFullPath(path);
        string[] own;
        string[] released;
        lock (raisedTags)
        {
            own = all.Take(1).Where(raisedTags.Add).ToArray();
            if (own.Length > 0) awaitingOwnIndex.Add(key);
            pendingRelatedTags.AddRange(all.Skip(1).Where(t => !raisedTags.Contains(t) && !pendingRelatedTags.Contains(t)));
            released = awaitingOwnIndex.Count == 0 ? TakeRelatedTagsLocked() : Array.Empty<string>();
        }

        var tags = own.Concat(released).ToArray();
        if (tags.Length > 0) Observe(Task.Run(() => SendQueueMarkers(tags)));
        if (own.Length > 0) Observe(Task.Delay(RelatedTagDelay, lifetime.Token).ContinueWith(_ => OwnIndexed(key), TaskScheduler.Default));
    }

    /// <summary>연 문서 자신의 색인을 기다리는 최대 시간입니다. 지나면 그 문서는 색인된 것으로 보고 관련 TU를 올릴지 정합니다.</summary>
    private static readonly TimeSpan RelatedTagDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 자신을 올린 연 문서가 색인되었거나 기다릴 시간이 지났습니다. 기다리는 연 문서가 더 없으면 모아 둔 관련 TU를 올립니다. TU 색인 완료마다
    /// 불리므로 기다리는 문서가 없으면 바로 돌아갑니다.
    /// </summary>
    private void OwnIndexed(string path)
    {
        string[] tags;
        lock (raisedTags)
        {
            if (awaitingOwnIndex.Count == 0 || !awaitingOwnIndex.Remove(Path.GetFullPath(path)) || awaitingOwnIndex.Count > 0) return;
            tags = TakeRelatedTagsLocked();
        }

        if (tags.Length > 0 && !lifetime.IsCancellationRequested) Observe(Task.Run(() => SendQueueMarkers(tags)));
    }

    private string[] TakeRelatedTagsLocked()
    {
        var tags = pendingRelatedTags.Where(raisedTags.Add).ToArray();
        pendingRelatedTags.Clear();
        return tags;
    }

    private void SendQueueMarkers(IReadOnlyList<string> tags)
    {
        if (HasExited || Context.Commands.Count == 0) return;
        var markers = new List<string>(tags.Count);
        try
        {
            Directory.CreateDirectory(QueueMarkerDirectory);
            foreach (var tag in tags)
            {
                var marker = Path.Combine(QueueMarkerDirectory, tag + ".h");
                if (!File.Exists(marker)) File.WriteAllText(marker, string.Empty);
                markers.Add(marker);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException)
        {
            // 순서만 바꾸는 보조 기능이라 쓰지 못한 표시는 건너뜁니다. 같은 clangd에서는 다시 시도하지 않습니다.
            if (AuxiliaryFailed is { } handler) handler("색인 대기열 앞당기기 파일을 쓰지 못했습니다: " + exception.Message);
        }

        if (markers.Count == 0) return;
        var sample = Context.Commands[0].Arguments;
        var head = new List<string> { sample[0] };
        if (sample.Count > 1 && sample[1].StartsWith("--driver-mode=", StringComparison.Ordinal)) head.Add(sample[1]);
        var directory = Context.Directory.Replace('\\', '/');
        try
        {
            session.UpdateCompileCommands(markers.Select(m => new CompileCommand(directory, m, head.Concat(new[] { m }).ToArray())).ToArray());
            foreach (var marker in markers)
            {
                session.OpenDocument(marker, string.Empty, 1);
                session.CloseDocument(marker);
            }
        }
        catch (LspConnectionClosedException)
        {
            // 종료는 Changed로 알려집니다.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 근사 명령을 만들되 캐시 폴더 쓰기(재정의 헤더, 생성 소스 대체 파일) 실패는 명령 없이 문서를 열고 알립니다. 문서 열기는 요청과 문서 알림
    /// 큐에서 부르므로 여기서 실패가 새면 요청 전체가 실패하거나 알림 처리가 멈췄습니다(2026-10-09 검토 44). 다음에 열 때 다시 시도합니다.
    /// </summary>
    private CompileCommand? TryApproximateCommand(string path)
    {
        try
        {
            return ApproximateCommand(path);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            documentCommandsSent.Remove(path);
            // 문서 집합 잠금 안(열기 콜백)이므로 구독자의 기록이 잠금을 오래 쥐지 않게 다른 스레드에서 알립니다(검토 58).
            var message = $"근사 컴파일 명령을 만들지 못해 clangd 추정 명령으로 엽니다({Path.GetFileName(path)}): {exception.Message}";
            if (AuxiliaryFailed is { } handler) _ = Task.Run(() => handler(message));
            return null;
        }
    }

    /// <summary>
    /// database 명령이 없는 Unreal C++ 파일의 근사 명령입니다(<see cref="UnrealCompileCommands.Synthesize"/>). 엔진 파일은 include를 스스로
    /// 갖추므로 공유 PCH를 빼고, 프로젝트 파일은 공유 PCH를 남깁니다. 만들 수 없으면 null입니다.
    /// </summary>
    private CompileCommand? ApproximateCommand(string path)
    {
        if (Context.Kind != CompileContextKind.Unreal || HasCommand(path) ||
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(Context.IndexStartPath), StringComparison.OrdinalIgnoreCase) ||
            !DefinitionCandidates.IsSource(path) && !DefinitionCandidates.IsHeader(path))
        {
            return null;
        }

        var engine = IsEngine(path);
        return UnrealCompileCommands.Synthesize(path, Context.Commands, Context.OverrideDirectory, sharedPrecompiledHeader: !engine, graph: Context.ModuleGraph);
    }

    /// <summary>
    /// PCH 없이 명령을 준 문서의 첫 진단에 오류가 있으면 PCH를 넣은 명령으로 바꿉니다. clangd는 열린 문서의 명령이 바뀌면 다시 분석합니다.
    /// LSP 읽기 스레드에서 호출되므로 명령은 작업 스레드에서 보냅니다(<see cref="ClangdSession.DiagnosticsPublished"/>). 요청은 보내기를 기다린
    /// 뒤 나가므로(<see cref="AwaitPchCheckAsync"/>) clangd가 다시 분석한 결과로 답합니다.
    /// </summary>
    /// <remarks>
    /// 같은 묶음의 앞 구성원이 포함한 헤더에 기대는 파일은 묶음 색인은 통과해도 혼자 열면 실패할 수 있어 묶음 판단과 따로 봅니다.
    /// 오류 원인을 가리지 않고 빌드 명령(PCH 포함)으로 돌아가며, 문서마다 세션당 한 번만 봅니다. 오류가 없어도 조건식에 정의되지 않은 매크로를
    /// 썼으면(PCH가 정의하던 매크로라 그 구역이 비활성으로 분석됨, <see cref="ConditionMacros"/>) 같은 방식으로 돌아갑니다.
    /// </remarks>
    private void OnDiagnostics(string path, DocumentErrors? errors, bool undefinedConditionMacros)
    {
        // 대기 목록에서 빼는 것과 전환 등록을 한 잠금 안에서 해, 그 사이에 들어온 요청이 전환 전 분석으로 답받지 않게 합니다(피드백 검토 36).
        // 잠금 순서는 pchGate → 계획·세션 잠금이며, 세션은 자기 잠금을 쥔 채 이 콜백을 부르지 않습니다.
        lock (pchGate)
        {
            if (!pchPending.Remove(path) || errors is null && !undefinedConditionMacros) return;
            if (Context.Plan is not { } plan) return;
            // 다시 분석한 진단을 기다릴 수 있게 대기를 먼저 등록하고 명령을 바꿉니다. 보충 헤더 고르기는 파일을 읽으므로 작업 스레드에서 합니다.
            var reparsed = session.WaitForNextDiagnosticsAsync(path, lifetime.Token);
            Observe(reparsed);
            pchReparses[path] = reparsed;
            pchSwitches[path] = Task.Run(() => SwitchDocument(plan, path));
        }
    }

    /// <summary>
    /// 분석 오류가 난 PCH 없는 문서를 보충 헤더(<see cref="IncludeSupplements"/>)나 공유 PCH를 넣은 명령으로 다시 분석하게 합니다. 보충 단계면
    /// 참입니다. 보충 단계는 다시 분석한 진단도 판단해, 그래도 오류면 더 배운 헤더로 한 번 더 보충하거나 PCH로 바꿉니다.
    /// </summary>
    private bool SwitchDocument(UnrealIndexPlan plan, string path)
    {
        try
        {
            if (TrySupplementDocument(plan, path)) return true;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ObjectDisposedException)
        {
            // 보충 헤더를 고르지 못해도(후보 헤더 읽기, 솔루션을 닫는 중의 이름 인덱스) PCH로 다시 분석합니다. 그대로 새면 요청이 실패하고
            // 문서가 오류 난 분석에 남았습니다(피드백 검토 66).
            if (AuxiliaryFailed is { } handler) handler($"필요한 헤더를 고르지 못해 공유 PCH로 분석합니다({Path.GetFileName(path)}): {exception.Message}");
        }

        if (plan.DocumentCommand(path, pch: true) is { } choice)
        {
            SendSwitched(new[] { choice.Command });
            ReopenQuietly(path);
        }

        return false;
    }

    /// <remarks>
    /// 이 문서의 이름 위치는 clangd에 보낸 내용(저장하지 않은 편집 포함)으로, include한 헤더의 위치는 디스크 내용으로 읽습니다. 고른 헤더는
    /// 이 세션의 이 문서에만 넣고 모듈에는 배우지 않습니다(<see cref="documentOwnSupplements"/>). 색인 단위가 배운 모듈 보충 헤더가 이 문서
    /// 명령에 아직 없으면 이름을 찾지 못해도 그 헤더로 다시 분석합니다.
    /// </remarks>
    private bool TrySupplementDocument(UnrealIndexPlan plan, string path)
    {
        if (!plan.SupplementsEnabled || options.FindSymbols is not { } find || lifetime.IsCancellationRequested) return false;
        lock (pchGate)
        {
            var rounds = documentSupplementRounds.TryGetValue(path, out var done) ? done : 0;
            if (rounds >= MaxDocumentSupplementRounds) return false;
            documentSupplementRounds[path] = rounds + 1;
        }

        var sentText = documents.SentText(path);
        var names = IncludeSupplements.Names(session.MissingNamesOf(path),
            file => sentText is not null && string.Equals(file, path, StringComparison.OrdinalIgnoreCase) ? sentText : SourceLinePreview.ReadText(file));
        var found = names.Count == 0 ? Array.Empty<string>() : IncludeSupplements.Resolve(names, find, plan.IncludeDirectoriesOf(path), SourceLinePreview.ReadText);
        IReadOnlyList<string> own;
        lock (pchGate)
        {
            own = documentOwnSupplements.TryGetValue(path, out var known)
                ? known.Concat(found.Where(h => !known.Contains(h, StringComparer.OrdinalIgnoreCase))).ToArray()
                : found;
            documentOwnSupplements[path] = own;
        }

        if (plan.DocumentCommand(path, pch: false, extra: own) is not { WithoutPch: true } choice) return false;
        lock (pchGate)
        {
            var sent = documentSupplements.TryGetValue(path, out var previous) ? previous : Array.Empty<string>();
            if (!choice.Supplements.Any(h => !sent.Contains(h, StringComparer.OrdinalIgnoreCase))) return false;
            documentSupplements[path] = choice.Supplements;
            pchPending.Add(path);
        }

        SendSwitched(new[] { choice.Command });
        ReopenQuietly(path);
        return true;
    }

    /// <summary>문서 하나를 보충 헤더로 다시 분석하는 세션당 최대 횟수입니다. 넘으면 PCH로 바꿉니다.</summary>
    private const int MaxDocumentSupplementRounds = 2;

    /// <summary>
    /// 요청 전에 그 문서의 PCH 판단을 기다립니다. 판단할 문서가 아니면 바로 돌아갑니다. 보충 헤더나 PCH를 넣어 다시 분석하게 되면 진행 문구를
    /// 알리고 명령을 보낸 뒤 돌아가므로, 이어서 보내는 요청은 clangd가 다시 분석한 뒤 답합니다. 보충 단계는 다시 분석한 결과로 다시 판단하므로
    /// 그 판단까지 기다립니다.
    /// </summary>
    private async Task AwaitPchCheckAsync(string path, int version, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Task<bool>? awaited = null;
        for (var round = 0; round <= MaxDocumentSupplementRounds; round++)
        {
            bool pending;
            Task<bool>? sent;
            Task? reparsed;
            lock (pchGate)
            {
                pending = pchPending.Contains(path);
                pchSwitches.TryGetValue(path, out sent);
                pchReparses.TryGetValue(path, out reparsed);
            }

            if (pending)
            {
                var before = sent;
                await WaitForAnalysisAsync(path, version, options.CandidateTimeout, cancellationToken, reparse: false).ConfigureAwait(false);
                lock (pchGate)
                {
                    // 시간 안에 분석이 끝나지 않았으면 더 기다리지 않고 요청합니다.
                    if (pchPending.Contains(path)) return;
                    pchSwitches.TryGetValue(path, out sent);
                }

                // 분석에 오류가 없어 새 전환이 없습니다.
                if (ReferenceEquals(sent, before)) return;
            }
            else if (sent is not null && sent.IsCompleted && reparsed is not { IsCompleted: false })
            {
                // 진행 중인 전환이 없습니다. 이미 보낸 전환의 다시 분석은 clangd가 요청보다 먼저 처리합니다.
                return;
            }

            if (sent is null || ReferenceEquals(sent, awaited)) return;
            var supplemented = await sent.ConfigureAwait(false);
            progress?.Report(supplemented ? "필요한 헤더를 넣어 다시 분석하는 중…" : "공유 PCH를 넣어 다시 분석하는 중…");
            if (!supplemented) return;
            awaited = sent;
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(done => _ = done.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// 문서의 진단(= 분석 완료)을 기다립니다. <paramref name="reparse"/>가 참이고 첫 진단 뒤 PCH를 넣어 다시 분석하게 되었으면 그 진단도
    /// 기다립니다. 문서를 닫기 전에 분석 결과를 색인에 남겨야 하는 후보 확인·저장 반영에서 씁니다. 시간 상한을 넘으면 기다리지 않고 돌아갑니다.
    /// </summary>
    private async Task WaitForAnalysisAsync(string path, int version, TimeSpan limit, CancellationToken cancellationToken, bool reparse = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(limit);
        try
        {
            await session.WaitForDiagnosticsAsync(path, version, timeout.Token).ConfigureAwait(false);
            if (!reparse) return;
            Task? reparsed;
            lock (pchGate)
            {
                pchReparses.TryGetValue(path, out reparsed);
            }

            if (reparsed is { IsCompleted: false })
            {
                await Task.WhenAny(reparsed, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            // 시간 안에 분석이 끝나지 않아도 열어 둔 동안 진행된 분석은 유지됩니다.
        }
    }

    /// <summary>
    /// background index가 분석 오류를 알린 TU를 모읍니다. stderr 읽기 스레드에서 호출되므로 파일 작업은 잠시 뒤 작업 스레드에서 묶어 합니다.
    /// </summary>
    private void OnIndexFailed(string translationUnit)
    {
        // 판단은 바로 남깁니다. 모아서 전환하기 전에 다시 시작해도 다음 세션이 PCH로 색인합니다(clangd는 오류가 있던 같은 내용의 TU를
        // 다시 색인하지 않아, 판단을 잃으면 그 단위가 계속 PCH 없이 남음).
        Context.Plan!.RecordFailure(translationUnit);
        lock (pchGate)
        {
            failedUnits.Add(translationUnit);
            if (pchFlushScheduled) return;
            pchFlushScheduled = true;
        }

        _ = Task.Run(FlushFailedUnitsAsync);
    }

    /// <remarks>
    /// 배경 작업의 경계입니다. 예외로 끝나면 바쁨 표식을 내리고 알립니다. 표식이 남으면 메모리 정리·다시 읽기·이후 단위 전환이 세션 끝까지
    /// 멈췄습니다(피드백 검토 66). 남은 실패는 다음 실패 알림이 다시 예약합니다. 종료 중이면 판단은 실패를 받을 때 기록했으므로 다음 세션이
    /// 이어 갑니다.
    /// </remarks>
    private async Task FlushFailedUnitsAsync()
    {
        var finished = false;
        try
        {
            await FlushFailedUnitsCoreAsync().ConfigureAwait(false);
            finished = true;
        }
        catch (Exception exception) when (exception is OperationCanceledException || exception is ObjectDisposedException ||
                                          exception is LspConnectionClosedException)
        {
            // 종료 중입니다.
        }
        catch (Exception exception)
        {
            AuxiliaryFailed?.Invoke("분석 오류가 난 색인 단위를 바꾸지 못했습니다: " + exception.Message);
        }
        finally
        {
            // 정상 종료는 안에서 실패 목록과 함께 내립니다(그 사이 들어온 실패가 새 작업을 예약할 수 있어 여기서 다시 내리지 않음).
            if (!finished)
            {
                lock (pchGate) pchFlushScheduled = false;
            }
        }
    }

    /// <remarks>
    /// 헤더 보충을 하면 바꾸기 전에 실패한 합성 TU를 컴파일러로 검사해 모르는 이름을 보고 모듈의 보충 헤더를 배웁니다(<see cref="UnrealIndexPlan.ProbeTargets"/>).
    /// 배운 헤더는 모듈에 기록되므로 이름 인덱스가 분석을 마칠 때까지 기다리되(<see cref="ClangdNavigatorOptions.SymbolsReady"/>), 상한
    /// (<see cref="ClangdNavigatorOptions.SymbolsWaitLimit"/>)을 넘으면 지금 색인으로 검사합니다. 덜 찬 색인으로 틀린 헤더를 배워도 그 단위는
    /// 다음 단계에서 다시 검사하거나 PCH로 가므로 결과는 같고 비용만 듭니다. 검사 대상과 다른 모듈의 단위는 기다리기 전에 바꿉니다(피드백 검토 69).
    /// 검사하는 동안 들어온 실패는 다음 차례에 함께 처리하며, 그동안은 바쁨으로 보여 다시 시작을 미룹니다.
    /// </remarks>
    private async Task FlushFailedUnitsCoreAsync()
    {
        var plan = Context.Plan!;
        while (true)
        {
            await Task.Delay(options.PchSwitchDelay, lifetime.Token).ConfigureAwait(false);
            string[] failed;
            lock (pchGate)
            {
                failed = failedUnits.ToArray();
                failedUnits.Clear();
            }

            var targets = plan.ProbeTargets(failed);
            if (targets.Count > 0)
            {
                var (now, afterProbe) = plan.SplitByProbe(failed, targets);
                if (now.Count > 0) SendSwitched(plan.SwitchFailed(now));
                failed = afterProbe.ToArray();
                var waited = Stopwatch.StartNew();
                while (!symbolsWaitExpired && options.SymbolsReady is { } ready && !ready())
                {
                    if (waited.Elapsed >= options.SymbolsWaitLimit)
                    {
                        symbolsWaitExpired = true;
                        break;
                    }

                    await Task.Delay(SymbolsPollInterval, lifetime.Token).ConfigureAwait(false);
                }
            }

            foreach (var target in targets)
            {
                var headers = await ProbeSupplementsAsync(plan, target).ConfigureAwait(false);
                // 취소로 끝난 검사를 '배울 것 없음'으로 보면 PCH로 바꿔 기록하므로, 종료 중이면 바꾸지 않고 끝냅니다.
                lifetime.Token.ThrowIfCancellationRequested();
                plan.Learn(target, headers);
            }

            SendSwitched(plan.SwitchFailed(failed));
            lock (pchGate)
            {
                if (failedUnits.Count == 0)
                {
                    pchFlushScheduled = false;
                    return;
                }
            }
        }
    }

    /// <summary>이름 인덱스 분석이 끝났는지 다시 보는 간격입니다.</summary>
    private static readonly TimeSpan SymbolsPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>합성 TU 검사를 기다리는 상한입니다. 넘으면 배우지 않고 다음 단계(PCH)로 넘어갑니다. 묶음 단위는 구성원 수만큼 걸립니다.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 분석 오류가 난 합성 TU를 clang-cl로 검사해 모르는 이름을 보고 보충 헤더를 고릅니다(<see cref="CompilerProbe"/>). background index의
    /// 실패 알림에는 원인이 없어서입니다. 한 번에 하나만 검사하며(호출자가 차례로 부름), 색인 단위 하나만큼(Unreal 약 1 GB·6초) 들고 끝나면
    /// 돌려줍니다. 검사하지 못하면(컴파일러 없음·시간 초과) 빈 목록입니다. 종료 중의 예외는 호출자 경계에서 받습니다.
    /// </summary>
    private async Task<IReadOnlyList<string>> ProbeSupplementsAsync(UnrealIndexPlan plan, string wrapper)
    {
        if (options.FindSymbols is not { } find || plan.CommandOfWrapper(wrapper) is not { } command) return Array.Empty<string>();
        var names = await CompilerProbe.MissingNamesAsync(command, ProbeTimeout, lifetime.Token).ConfigureAwait(false);
        return names is not { Count: > 0 }
            ? Array.Empty<string>()
            : IncludeSupplements.Resolve(names, find, IncludeSupplements.IncludeDirectories(command), SourceLinePreview.ReadText);
    }

    /// <summary>
    /// 색인이 끝날 때 로그 형식이 맞는지 봅니다(<see cref="IndexLogUnreadable"/>). 이 세션이 색인 파일을 쓰지 않았으면 판단을 미루고 다음 색인
    /// 완료 때 다시 봅니다. 한 번 판단하면 더 보지 않습니다.
    /// </summary>
    private async Task CheckLogFormatAsync()
    {
        var concluded = false;
        try
        {
            // 마지막 TU의 색인 파일 쓰기와 로그 줄이 완료 알림보다 조금 늦을 수 있습니다.
            await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token).ConfigureAwait(false);
            if (!IndexWrittenSince(Path.Combine(Context.Directory, ".cache", "clangd", "index"), StartedUtc)) return;
            concluded = true;
            if (Volatile.Read(ref indexedLineSeen) != 0) return;
            Volatile.Write(ref logFormatUnreadable, 1);
            Changed?.Invoke();
        }
        catch (Exception exception) when (exception is OperationCanceledException || exception is ObjectDisposedException)
        {
            concluded = true;
        }
        finally
        {
            if (!concluded) Volatile.Write(ref logFormatChecking, 0);
        }
    }

    /// <summary>
    /// 색인 폴더에 <paramref name="sinceUtc"/> 뒤에 쓴 색인 파일(<c>*.idx</c>)이 있는지 봅니다. clangd는 시작할 때마다 같은 폴더의
    /// <c>.gitignore</c>를 다시 쓰므로 색인 파일만 봅니다. 읽지 못하면 없다고 봅니다.
    /// </summary>
    public static bool IndexWrittenSince(string directory, DateTime sinceUtc)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.idx"))
            {
                if (file.LastWriteTimeUtc > sinceUtc) return true;
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
        }

        return false;
    }

    private void SendSwitched(IReadOnlyList<CompileCommand> commands)
    {
        if (commands.Count == 0 || lifetime.IsCancellationRequested) return;
        try
        {
            session.UpdateCompileCommands(commands);
        }
        catch (Exception exception) when (exception is LspConnectionClosedException || exception is ObjectDisposedException)
        {
            // 종료되면 다음 시작 때 판단 기록으로 같은 명령을 씁니다.
        }
    }

    /// <summary>
    /// 명령을 바꾼 열린 문서를 다시 엽니다(<see cref="ClangdDocumentSet.Reopen"/>). 명령만 바꾸면 clangd가 이전 preamble의 분석으로 먼저 답해
    /// 공유 PCH를 넣은 뒤의 요청도 PCH 없는 분석으로 답받았습니다.
    /// </summary>
    private void ReopenQuietly(string path)
    {
        if (lifetime.IsCancellationRequested) return;
        try
        {
            documents.Reopen(path);
        }
        catch (Exception exception) when (exception is LspConnectionClosedException || exception is ObjectDisposedException)
        {
            // 종료되면 다음 시작 때 판단 기록으로 같은 명령을 씁니다.
        }
    }

    private void SyncOpenDocuments(NavigationQuery query)
    {
        foreach (var document in query.OpenDocuments)
        {
            if (!string.Equals(document.Path, query.Path, StringComparison.OrdinalIgnoreCase))
            {
                documents.Update(document);
            }
        }
    }

    /// <summary>
    /// 정의 이동 결과가 헤더의 함수 선언 하나뿐이라 정의가 색인에 없을 수 있는지 봅니다.
    /// </summary>
    /// <remarks>
    /// 엔진 헤더는 엔진 cpp를 색인하지 않는 범위 결정 때문이고(근사 명령이 필요해 Unreal만), 프로젝트 헤더는 첫 색인이 끝나지 않았거나
    /// 정의 파일에 명령이 없던 경우입니다. 타입·별칭·매크로 줄은 헤더가 곧 정의이므로, 순수 가상·삭제 함수와 생성 코드에 본문이 있는
    /// Unreal 이벤트는 소스 cpp에 정의가 없으므로 제외합니다.
    /// </remarks>
    private bool MayNeedDefinitionFile(IReadOnlyList<NavigationLocation> locations)
    {
        if (locations.Count != 1)
        {
            return false;
        }

        var location = locations[0];
        if (!DefinitionCandidates.IsHeader(location.Path) || IsEngine(location.Path) && Context.Kind != CompileContextKind.Unreal)
        {
            return false;
        }

        var text = SourceLinePreview.ReadText(location.Path);
        return text is not null && !DefinitionCandidates.LooksLikeTypeOrMacro(SourceLinePreview.LineAt(text, location.Line)) &&
               !DefinitionCandidates.LooksLikeNoSourceBody(text, location.Line);
    }

    /// <summary>
    /// 정의가 있을 만한 cpp(이름 인덱스의 같은 소속 함수 → 헤더와 같은 이름 cpp, 최대 <see cref="ClangdNavigatorOptions.MaxDefinitionCandidates"/>개)를
    /// 차례로 clangd에 열어 정의를 확정합니다. 결과는 clangd가 다시 돌려준 위치만 쓰고 이름으로 추측한 위치는 쓰지 않습니다.
    /// </summary>
    private async Task<IReadOnlyList<NavigationLocation>?> ResolveDefinitionFileAsync(NavigationQuery query, SemanticSymbol symbol, string header,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var symbols = options.FindSymbols?.Invoke(symbol.Name) ?? Array.Empty<SourceSymbolLocation>();
        // 이름 인덱스는 Solution을 연 직후 비어 있을 수 있으므로 소속 모듈 폴더의 같은 이름 cpp를 함께 봅니다.
        var stems = (options.FindByStem?.Invoke(Path.GetFileNameWithoutExtension(header)) ?? Array.Empty<string>())
            .Concat(SameNameSourcesInModule(header));
        var candidates = DefinitionCandidates.Select(symbol.Name, symbol.ContainerName, header, symbols, stems, options.MaxDefinitionCandidates)
            .Where(c => !IsEngine(c) || Context.Kind == CompileContextKind.Unreal)
            .ToArray();
        for (var i = 0; i < candidates.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[i];
            var engine = IsEngine(candidate);
            // 첫 색인이 끝났고 database에 명령이 있는 파일은 이미 색인되었으므로 열어도 새 정보가 없습니다.
            // 정의가 원래 소스에 없는 함수(외부 라이브러리 선언 등)에서 요청마다 후보 분석 시간을 쓰지 않게 합니다.
            if (Progress.Completed && HasCommand(candidate)) continue;
            lock (attemptedCandidates)
            {
                // 한 번 연 후보는 background index에 남으므로 내용이 바뀌기 전에는 다시 열어도 새 정보가 없습니다.
                if (!attemptedCandidates.Add(candidate + "|" + SafeWriteTicks(candidate))) continue;
            }

            // 엔진 cpp와 명령이 없는 Unreal 프로젝트 파일은 근사 명령을 줍니다. 명령이 있는 파일은 database의 명령을 그대로 씁니다.
            // 그 밖(명령 없는 일반 프로젝트 파일)은 clangd가 가까운 파일의 명령으로 추정합니다.
            var command = engine || Context.Kind == CompileContextKind.Unreal && !HasCommand(candidate)
                ? UnrealCompileCommands.Synthesize(candidate, Context.Commands, Context.OverrideDirectory, sharedPrecompiledHeader: !engine, graph: Context.ModuleGraph)
                : null;
            var text = engine && command is null ? null : SourceLinePreview.ReadText(candidate);
            if (text is null)
            {
                continue;
            }

            progress?.Report($"{(engine ? "엔진 정의" : "정의 파일")} 확인 중({i + 1}/{candidates.Length}): {Path.GetFileName(candidate)}");
            if (command is not null) session.UpdateCompileCommands(new[] { command });
            var version = documents.Acquire(new DocumentText(candidate, text));
            try
            {
                // 시간 안에 분석이 끝나지 않은 후보는 건너뜁니다. 열어 둔 동안 진행된 색인은 유지됩니다.
                await WaitForAnalysisAsync(candidate, version, options.CandidateTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                documents.Release(candidate);
                documents.TryClose(candidate);
            }

            var again = await session.DefinitionAsync(query.Path, query.Line, query.Character, cancellationToken).ConfigureAwait(false);
            if (again.Count > 0 && again.Any(l => !DefinitionCandidates.IsHeader(l.Path)))
            {
                if (command is not null) definitionSources?.Record(candidate);
                return again;
            }
        }

        return null;
    }

    private async Task RunTouchesAsync()
    {
        while (true)
        {
            string path;
            DocumentText document;
            lock (touchGate)
            {
                if (touchOrder.Count == 0 || lifetime.IsCancellationRequested)
                {
                    touchOrder.Clear();
                    touchTexts.Clear();
                    touchRunning = false;
                    return;
                }

                path = touchOrder.Dequeue();
                document = touchTexts[path];
                touchTexts.Remove(path);
            }

            try
            {
                var version = documents.Acquire(document);
                try
                {
                    // 분석이 오래 걸려도 다음 저장 반영을 막지 않습니다.
                    await WaitForAnalysisAsync(path, version, TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 종료 중입니다.
                }
                finally
                {
                    documents.Release(path);
                    documents.TryClose(path);
                }
            }
            catch (LspConnectionClosedException)
            {
                // 종료되면 위 반복에서 대기열을 비웁니다.
            }
            catch (ObjectDisposedException)
            {
                lock (touchGate)
                {
                    touchRunning = false;
                }

                return;
            }
        }
    }

    private static IReadOnlyList<string> SameNameSourcesInModule(string header)
    {
        if (UnrealCompileCommands.OwningModule(header) is not (string directory, _))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(directory, Path.GetFileNameWithoutExtension(header) + ".cpp", SearchOption.AllDirectories).Take(8).ToArray();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private RequestScope TrackRequest()
    {
        Interlocked.Increment(ref activeRequests);
        Interlocked.Exchange(ref lastRequestTicks, DateTime.UtcNow.Ticks);
        return new RequestScope(this);
    }

    private readonly struct RequestScope : IDisposable
    {
        private readonly ClangdNavigator owner;

        public RequestScope(ClangdNavigator owner) => this.owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref owner.lastRequestTicks, DateTime.UtcNow.Ticks);
            Interlocked.Decrement(ref owner.activeRequests);
        }
    }

    private bool IsEngine(string path) => Context.EngineRoot is not null && IsUnder(path, Context.EngineRoot);

    private bool HasCommand(string path) => CommandOf(path) is not null;

    private CompileCommand? CommandOf(string path)
    {
        var commands = commandsByFile;
        if (commands is null)
        {
            commands = new Dictionary<string, CompileCommand>(StringComparer.OrdinalIgnoreCase);
            foreach (var command in Context.Commands)
            {
                commands[NormalizedFull(command.File)] = command;
            }

            commandsByFile = commands;
        }

        return commands.TryGetValue(NormalizedFull(path), out var found) ? found : null;
    }

    private static string NormalizedFull(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private static long SafeWriteTicks(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        var prefix = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/') + "/";
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
