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

    /// <summary>0이면 논리 코어의 1/4입니다.</summary>
    public int WorkerCount { get; set; }

    /// <summary>clangd에 동시에 열어 둘 문서 수입니다. 문서마다 preamble·AST 메모리를 씁니다.</summary>
    public int DocumentCapacity { get; set; } = 8;

    /// <summary>엔진 정의 후보를 고를 이름 인덱스 조회. 없으면 헤더와 이름이 같은 cpp만 봅니다.</summary>
    public Func<string, IReadOnlyList<SourceSymbolLocation>>? FindSymbols { get; set; }

    /// <summary>파일 이름(확장자 제외)으로 파일을 찾는 조회입니다.</summary>
    public Func<string, IReadOnlyList<string>>? FindByStem { get; set; }

    public int MaxEngineCandidates { get; set; } = 3;

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
    public NavigationResult(IReadOnlyList<NavigationLocation> locations, SemanticSymbol? symbol, BackgroundIndexProgress progress, bool resolvedOnDemand)
    {
        Locations = locations;
        Symbol = symbol;
        Progress = progress;
        ResolvedOnDemand = resolvedOnDemand;
    }

    public IReadOnlyList<NavigationLocation> Locations { get; }

    public SemanticSymbol? Symbol { get; }

    /// <summary>요청 시점의 색인 진행. 완료 전이면 결과가 불완전할 수 있습니다.</summary>
    public BackgroundIndexProgress Progress { get; }

    /// <summary>색인하지 않은 엔진 cpp를 요청 시점에 열어 정의를 확정했습니다.</summary>
    public bool ResolvedOnDemand { get; }
}

/// <summary>
/// Solution 하나의 clangd 세션과 문서 동기화, 정의·참조 요청을 묶습니다. VS SDK에 의존하지 않습니다.
/// </summary>
/// <remarks>
/// 범위 결정: 프로젝트 TU는 background index로 전부 색인하고, 엔진은 프로젝트 TU가 포함한 헤더까지만 색인합니다.
/// 엔진 cpp에만 있는 정의는 요청 시점에 후보 cpp에 근사 명령을 공급하고 열어 확정합니다. 확정한 파일은
/// background index에 남아 다음 요청부터 바로 찾습니다.
/// 최신성: 편집기에서 저장한 문서는 clangd에 열려 있으면 내용과 저장을 알리고, 아니면 저장된 내용으로 열었다가
/// 분석이 끝나면 닫습니다. clangd는 파일 감시 통지만으로는 닫힌 파일을 다시 색인하지 않기 때문입니다.
/// 편집기 밖에서 바뀐 파일(<see cref="Reload"/>)도 디스크 내용으로 같은 방식을 씁니다.
/// </remarks>
public sealed class ClangdNavigator : IDisposable
{
    private readonly ClangdNavigatorOptions options;
    private readonly ClangdSession session;
    private readonly ClangdDocumentSet documents;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object touchGate = new();
    private readonly Queue<string> touchOrder = new();
    private readonly Dictionary<string, DocumentText> touchTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> attemptedCandidates = new(StringComparer.OrdinalIgnoreCase);
    private bool touchRunning;
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
            File.Exists(compiler) ? compiler : "clang-cl.exe", cancellationToken), cancellationToken).ConfigureAwait(false);
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
                LogFilePath = Path.Combine(context.Directory, "clangd.log")
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
        SyncOpenDocuments(query);
        documents.Acquire(query.Document);
        try
        {
            var locations = await session.DefinitionAsync(query.Path, query.Line, query.Character, cancellationToken).ConfigureAwait(false);
            var symbol = default(SemanticSymbol);
            var resolved = false;
            if (NeedsEngineDefinition(locations))
            {
                symbol = await session.SymbolInfoAsync(query.Path, query.Line, query.Character, cancellationToken).ConfigureAwait(false);
                if (symbol is not null)
                {
                    var found = await ResolveEngineDefinitionAsync(query, symbol, locations[0].Path, progress, cancellationToken).ConfigureAwait(false);
                    if (found is not null)
                    {
                        locations = found;
                        resolved = true;
                    }
                }
            }

            return new NavigationResult(locations, symbol, Progress, resolved);
        }
        finally
        {
            documents.Release(query.Path);
        }
    }

    public async Task<NavigationResult> ReferencesAsync(NavigationQuery query, CancellationToken cancellationToken)
    {
        SyncOpenDocuments(query);
        documents.Acquire(query.Document);
        try
        {
            var locations = await session.ReferencesAsync(query.Path, query.Line, query.Character, true, cancellationToken).ConfigureAwait(false);
            var symbol = await session.SymbolInfoAsync(query.Path, query.Line, query.Character, cancellationToken).ConfigureAwait(false);
            return new NavigationResult(locations, symbol, Progress, false);
        }
        finally
        {
            documents.Release(query.Path);
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

    private bool NeedsEngineDefinition(IReadOnlyList<NavigationLocation> locations)
    {
        if (Context.Kind != CompileContextKind.Unreal || Context.EngineRoot is null || locations.Count != 1)
        {
            return false;
        }

        var location = locations[0];
        if (!DefinitionCandidates.IsHeader(location.Path) || !IsUnder(location.Path, Context.EngineRoot))
        {
            return false;
        }

        var text = SourceLinePreview.ReadText(location.Path);
        return text is not null && !DefinitionCandidates.LooksLikeTypeOrMacro(SourceLinePreview.LineAt(text, location.Line));
    }

    private async Task<IReadOnlyList<NavigationLocation>?> ResolveEngineDefinitionAsync(NavigationQuery query, SemanticSymbol symbol, string header,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var symbols = options.FindSymbols?.Invoke(symbol.Name) ?? Array.Empty<SourceSymbolLocation>();
        // 이름 인덱스는 Solution을 연 직후 비어 있을 수 있으므로 소속 모듈 폴더의 같은 이름 cpp를 함께 봅니다.
        var stems = (options.FindByStem?.Invoke(Path.GetFileNameWithoutExtension(header)) ?? Array.Empty<string>())
            .Concat(SameNameSourcesInModule(header));
        var candidates = DefinitionCandidates.Select(symbol.Name, symbol.ContainerName, header, symbols, stems, options.MaxEngineCandidates)
            .Where(c => IsUnder(c, Context.EngineRoot!))
            .ToArray();
        for (var i = 0; i < candidates.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[i];
            lock (attemptedCandidates)
            {
                // 한 번 연 후보는 background index에 남으므로 다시 열어도 새 정보가 없습니다.
                if (!attemptedCandidates.Add(candidate)) continue;
            }

            var command = UnrealCompileCommands.Synthesize(candidate, Context.Commands, Context.OverrideDirectory);
            var text = command is null ? null : SourceLinePreview.ReadText(candidate);
            if (command is null || text is null)
            {
                continue;
            }

            progress?.Report($"엔진 정의 확인 중({i + 1}/{candidates.Length}): {Path.GetFileName(candidate)}");
            session.UpdateCompileCommands(new[] { command });
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

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        var prefix = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/') + "/";
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
