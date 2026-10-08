using System;
using System.Collections.Generic;
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

    /// <summary>파일 이름(확장자 제외)으로 파일을 찾는 조회입니다.</summary>
    public Func<string, IReadOnlyList<string>>? FindByStem { get; set; }

    /// <summary>compile_commands.json이 없을 때 명령을 얻을 빌드 도구와 Solution 구성입니다.</summary>
    public CompileCommandSources Sources { get; set; } = new();

    /// <summary>정의가 색인에 없을 때 요청 시점에 열어 볼 cpp 수입니다(엔진·프로젝트 공통).</summary>
    public int MaxDefinitionCandidates { get; set; } = 3;

    public TimeSpan CandidateTimeout { get; set; } = TimeSpan.FromSeconds(45);
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
        SourceSymbolKind? symbolKind = null, IReadOnlyDictionary<NavigationLocation, NavigationRole>? roles = null)
    {
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

    /// <summary>색인하지 않은 엔진 cpp를 요청 시점에 열어 정의를 확정했습니다.</summary>
    public bool ResolvedOnDemand { get; }

    /// <summary>결과 목록에서 이름을 색칠할 심볼 종류입니다. 목록을 보이지 않는 결과(정의 하나)나 판정하지 못하면 null입니다.</summary>
    public SourceSymbolKind? SymbolKind { get; }
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
    // Context.Commands의 파일 집합입니다. 여러 요청이 동시에 처음 만들어도 같은 내용이라 잠그지 않습니다.
    private volatile HashSet<string>? commandFiles;
    private bool touchRunning;
    private int activeRequests;
    private long lastRequestTicks = DateTime.UtcNow.Ticks;
    private int disposed;

    private ClangdNavigator(ClangdNavigatorOptions options, CompileContext context, ClangdSession session)
    {
        this.options = options;
        Context = context;
        this.session = session;
        documents = new ClangdDocumentSet(Math.Max(1, options.DocumentCapacity), session.OpenDocument, session.ChangeDocument, session.CloseDocument);
        session.ProgressChanged += () => Changed?.Invoke();
        session.Exited += _ =>
        {
            lifetime.Cancel();
            Changed?.Invoke();
        };
    }

    /// <summary>색인 진행이나 종료가 바뀌었습니다. 임의 스레드에서 호출됩니다.</summary>
    public event Action? Changed;

    public CompileContext Context { get; }

    public BackgroundIndexProgress Progress => session.Progress;

    public bool HasExited => session.HasExited;

    public int ProcessId => session.ProcessId;

    /// <summary>이 탐색기를 시작한 시각(UTC)입니다.</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>마지막 탐색 요청이 시작하거나 끝난 시각(UTC)입니다. 요청이 없었으면 시작 시각입니다.</summary>
    public DateTime LastRequestUtc => new(Interlocked.Read(ref lastRequestTicks), DateTimeKind.Utc);

    /// <summary>탐색 요청이나 저장 반영이 진행 중입니다. 메모리 정리 재시작을 미룰 때 씁니다.</summary>
    public bool IsBusy
    {
        get
        {
            lock (touchGate)
            {
                if (touchRunning) return true;
            }

            return Volatile.Read(ref activeRequests) > 0;
        }
    }

    /// <summary>참조 결과 상한입니다. 결과 수가 같으면 잘렸을 수 있습니다.</summary>
    public int ReferenceLimit => session.ReferenceLimit;

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
            File.Exists(compiler) ? compiler : "clang-cl.exe", cancellationToken, options.Sources), cancellationToken).ConfigureAwait(false);
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
        try
        {
            await navigator.StartBackgroundIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            navigator.Dispose();
            throw;
        }

        return navigator;
    }

    public bool IsOpen(string path) => documents.Contains(path);

    public async Task<NavigationResult> DefinitionAsync(NavigationQuery query, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var tracked = TrackRequest();
        SyncOpenDocuments(query);
        documents.Acquire(query.Document);
        try
        {
            var locations = await session.DefinitionAsync(query.Path, query.Line, query.Character, cancellationToken).ConfigureAwait(false);
            var symbol = default(SemanticSymbol);
            var resolved = false;
            if (MayNeedDefinitionFile(locations))
            {
                symbol = await session.SymbolInfoAsync(query.Path, query.Line, query.Character, cancellationToken).ConfigureAwait(false);
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
                symbol ??= await session.SymbolInfoAsync(query.Path, query.Line, query.Character, cancellationToken).ConfigureAwait(false);
                kind = await SymbolKindAsync(symbol, locations, cancellationToken).ConfigureAwait(false);
            }

            return new NavigationResult(locations, symbol, Progress, resolved, kind);
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
        var (result, definitions) = await ReferencesCoreAsync(query, cancellationToken).ConfigureAwait(false);
        if (definitions is null || !MayNeedDefinitionFile(definitions) || result.Symbol is not { Definition: null } symbol)
        {
            return result;
        }

        var found = await ResolveDefinitionFileAsync(query, symbol, definitions[0].Path, progress, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return result;
        }

        var (again, _) = await ReferencesCoreAsync(query, cancellationToken).ConfigureAwait(false);
        return new NavigationResult(again.Locations, again.Symbol, again.Progress, true, again.SymbolKind, again.Roles);
    }

    private async Task<(NavigationResult Result, IReadOnlyList<NavigationLocation>? Definitions)> ReferencesCoreAsync(NavigationQuery query,
        CancellationToken cancellationToken)
    {
        SyncOpenDocuments(query);
        documents.Acquire(query.Document);
        // 역할 표식 근거(선언 제외 참조, 정의·선언 이동)는 표시 보조라 본 요청 뒤에 함께 보내고, 본 결과가 온 뒤 정한 시간까지만 기다립니다.
        // clangd는 한 파일의 AST 요청을 받은 순서대로 하나씩 처리하고 이미 시작한 요청은 취소로 멈추지 않습니다. 그래서 결과 표시에 꼭 필요한
        // symbolInfo와 짧은 정의·선언 요청을 본 요청 바로 뒤에 두고, 본 요청만큼 걸릴 수 있는 선언 제외 참조는 맨 뒤에 보냅니다(2026-10-07 검토).
        // 늦거나 실패하면 그 근거 없이 정합니다. 본 요청이 실패해도 남은 보조 요청은 finally에서 취소하고 예외를 관측합니다.
        using var roleLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var auxiliary = new List<Task>(4);
        try
        {
            var all = session.ReferencesAsync(query.Path, query.Line, query.Character, true, cancellationToken);
            var symbolInfo = session.SymbolInfoAsync(query.Path, query.Line, query.Character, cancellationToken);
            auxiliary.Add(symbolInfo);
            var definition = Quietly(session.DefinitionAsync(query.Path, query.Line, query.Character, roleLimit.Token));
            auxiliary.Add(definition);
            var declaration = Quietly(session.DeclarationAsync(query.Path, query.Line, query.Character, roleLimit.Token));
            auxiliary.Add(declaration);
            var uses = Quietly(session.ReferencesAsync(query.Path, query.Line, query.Character, false, roleLimit.Token));
            auxiliary.Add(uses);
            var locations = await all.ConfigureAwait(false);
            roleLimit.CancelAfter(RoleTimeout);
            var symbol = await symbolInfo.ConfigureAwait(false);
            var kind = await SymbolKindAsync(symbol, locations, cancellationToken).ConfigureAwait(false);
            var plain = await uses.ConfigureAwait(false);
            // 결과 수 제한에 걸리면 두 참조 결과가 서로 다른 위치에서 잘려 차이가 선언 묶음이 아닙니다.
            if (plain is not null && (locations.Count >= ReferenceLimit || plain.Count >= ReferenceLimit)) plain = null;
            var definitions = await definition.ConfigureAwait(false);
            var roles = ReferenceRoles.Classify(locations, plain, symbol?.Definition, symbol?.Declaration,
                definitions, await declaration.ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
            return (new NavigationResult(locations, symbol, Progress, false, kind, roles), definitions);
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
            return await session.SymbolKindAsync(symbol, locations, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is LspRequestException || exception is LspConnectionClosedException)
        {
            return null;
        }
    }

    /// <summary>편집기에서 활성화한 문서를 미리 열어 첫 요청 전에 분석을 시작합니다.</summary>
    public void Warm(DocumentText document)
    {
        if (HasExited) return;
        try
        {
            documents.Acquire(document);
            documents.Release(document.Path);
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

    /// <summary>편집기에서 마지막 창을 닫은 문서를 clangd에서도 닫습니다(진행 중인 요청이 없을 때).</summary>
    public void Closed(string path)
    {
        if (HasExited) return;
        try
        {
            documents.TryClose(path);
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
        var probe = Path.Combine(Context.Directory, "visualboost-index-start.cpp");
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
                ? UnrealCompileCommands.Synthesize(candidate, Context.Commands, Context.OverrideDirectory)
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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                timeout.CancelAfter(options.CandidateTimeout);
                await session.WaitForDiagnosticsAsync(candidate, version, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
            {
                // 시간 안에 분석이 끝나지 않은 후보는 건너뜁니다. 열어 둔 동안 진행된 색인은 유지됩니다.
            }
            finally
            {
                documents.Release(candidate);
                documents.TryClose(candidate);
            }

            var again = await session.DefinitionAsync(query.Path, query.Line, query.Character, cancellationToken).ConfigureAwait(false);
            if (again.Count > 0 && again.Any(l => !DefinitionCandidates.IsHeader(l.Path)))
            {
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
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    await session.WaitForDiagnosticsAsync(path, version, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 분석이 오래 걸려도 다음 저장 반영을 막지 않습니다.
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

    private bool HasCommand(string path)
    {
        var commands = commandFiles;
        if (commands is null)
        {
            commands = new HashSet<string>(Context.Commands.Select(c => NormalizedFull(c.File)), StringComparer.OrdinalIgnoreCase);
            commandFiles = commands;
        }

        return commands.Contains(NormalizedFull(path));
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
