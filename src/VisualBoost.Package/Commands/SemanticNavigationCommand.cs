using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using VisualBoost.Core.FilePairing;
using VisualBoost.Core.SemanticNavigation;
using VisualBoost.SemanticNavigation;
using VisualBoost.Services;
using VisualBoost.UI;

namespace VisualBoost.Commands;

/// <summary>정의로 이동·참조 찾기 명령입니다. C++ 문서는 clangd로, 그 밖의 문서는 Visual Studio 기본 명령으로 처리합니다.</summary>
internal sealed class SemanticNavigationCommand
{
    private static readonly Guid CommandSet = new("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    private readonly VisualBoostPackage package;
    private readonly SemanticNavigationService service;
    private readonly SolutionFileIndexService fileIndex;

    // 진행 중인 요청입니다. UI thread에서만 읽고 씁니다.
    private CancellationTokenSource? active;

    private SemanticNavigationCommand(VisualBoostPackage package, SemanticNavigationService service, SolutionFileIndexService fileIndex,
        OleMenuCommandService commandService)
    {
        this.package = package;
        this.service = service;
        this.fileIndex = fileIndex;
        commandService.AddCommand(new OleMenuCommand(ExecuteDefinition, new CommandID(CommandSet, CommandIds.GoToDefinition)));
        commandService.AddCommand(new OleMenuCommand(ExecuteReferences, new CommandID(CommandSet, CommandIds.FindReferences)));
        commandService.AddCommand(new OleMenuCommand(ExecuteShowReferencesWindow, new CommandID(CommandSet, CommandIds.ShowReferencesWindow)));
    }

    private enum Kind
    {
        Definition,
        References
    }

    public static async Task InitializeAsync(VisualBoostPackage package, SemanticNavigationService service, SolutionFileIndexService fileIndex,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        Assumes.Present(commandService);
        _ = new SemanticNavigationCommand(package, service, fileIndex, commandService);
    }

    private void ExecuteDefinition(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Execute(Kind.Definition);
    }

    private void ExecuteReferences(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Execute(Kind.References);
    }

    private void ExecuteShowReferencesWindow(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SearchCommandRunner.Run(package, "ShowReferencesWindow", async () =>
            await package.ShowToolWindowAsync(typeof(ReferencesToolWindow), 0, true, package.DisposalToken));
    }

    private void Execute(Kind kind)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SearchCommandRunner.Run(package, kind == Kind.Definition ? "GoToDefinition" : "FindReferences", () => RunAsync(kind));
    }

    private async Task RunAsync(Kind kind)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var components = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        var manager = await package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
        Assumes.Present(components);
        Assumes.Present(manager);
        var view = ActiveView(manager, components.GetService<IVsEditorAdaptersFactoryService>());
        if (view is null || !view.TextBuffer.ContentType.IsOfType("C/C++") ||
            !components.GetService<ITextDocumentFactoryService>().TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var document))
        {
            // C++ 이외 문서에서는 같은 단축키로 Visual Studio 기본 탐색을 씁니다.
            await RunVisualStudioCommandAsync(kind, null);
            return;
        }

        var caret = view.Caret.Position.BufferPosition;
        var snapshot = caret.Snapshot;
        var line = snapshot.GetLineFromPosition(caret.Position);
        var path = document.FilePath;
        var revision = SemanticDocumentTracker.RevisionOf(snapshot);
        var others = SemanticDocumentTracker.OpenDocuments()
            .Where(d => !string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase))
            .Select(d => new DocumentText(d.Path, d.Snapshot.GetText, d.Revision))
            .ToArray();
        var query = new NavigationQuery(new DocumentText(path, snapshot.GetText, revision), line.LineNumber, caret.Position - line.Start.Position, others);
        var openTexts = others.ToDictionary(d => d.Path, d => d, StringComparer.OrdinalIgnoreCase);
        openTexts[path] = query.Document;
        var statusBar = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        // 같은 키를 다시 누르면 이전 요청을 취소합니다. clangd는 한 파일의 요청을 차례로 처리하므로, 응답이 늦다고 다시 누를 때마다
        // 요청이 쌓여 더 늦어지던 것을 막습니다(취소하면 아직 시작하지 않은 clangd 요청은 버려집니다).
        active?.Cancel();
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        active = request;
        var waiting = new RequestStatus(statusBar, kind == Kind.Definition ? "정의 찾는 중" : "참조 찾는 중", service);
        // Progress<T>는 만든 UI 문맥으로 보고를 넘깁니다.
        var progress = new Progress<string>(text =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!request.IsCancellationRequested) waiting.Stage(text);
        });

        NavigationResult result;
        int referenceLimit;
        bool limited;
        IReadOnlyList<string> lines = Array.Empty<string>();
        DocumentErrors? errors = null;
        try
        {
            try
            {
                (result, limited, referenceLimit, lines, errors) = await Task.Run(async () =>
                {
                    var navigator = await service.GetNavigatorAsync(request.Token).ConfigureAwait(false);
                    var found = kind == Kind.Definition
                        ? await navigator.DefinitionAsync(query, progress, request.Token).ConfigureAwait(false)
                        : await navigator.ReferencesAsync(query, request.Token, progress).ConfigureAwait(false);
                    var ordered = Order(found.Locations, path);
                    found = found.WithLocations(ordered);
                    // 결과가 하나인 정의 이동은 미리보기가 필요 없습니다.
                    var preview = kind == Kind.References || ordered.Count > 1
                        ? SourceLinePreview.LoadLines(ordered, p => openTexts.TryGetValue(p, out var open) ? open.Text : null, timeout.Token)
                        : Array.Empty<string>();
                    // clangd가 보낸 원래 항목 수로 판단합니다. 겹친 위치를 합친 뒤의 개수는 상한보다 작을 수 있습니다.
                    var limited = kind == Kind.References && found.Limited;
                    if (kind == Kind.References)
                    {
                        // 클래스 자신의 멤버 정의 머리(Class::Member)와 생성자·소멸자 이름, 찾는 이름이 쓰이지 않은 매크로 자리는 쓰는 곳이 아니므로 뺍니다.
                        var kept = OwnDefinitionReferences.Kept(ordered, preview, IsType(found.SymbolKind), found.Symbol?.Name, found.Symbol?.ContainerName);
                        if (kept.Count < ordered.Count)
                        {
                            ordered = kept.Select(i => ordered[i]).ToArray();
                            preview = kept.Select(i => preview[i]).ToArray();
                            found = found.WithLocations(ordered);
                        }
                    }

                    // 요청 문서가 분석 오류로 심볼을 해석하지 못했을 수 있으므로 첫 오류를 함께 알립니다.
                    return (found, limited, navigator.ReferenceLimit, preview, navigator.ErrorsOf(path));
                }, timeout.Token);
            }
            catch (SemanticNavigationUnavailableException exception)
            {
                await RunVisualStudioCommandAsync(kind, exception.Message);
                return;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                await NotifyAsync($"{Name(kind)} 요청이 {RequestTimeout.TotalSeconds:N0}초 안에 끝나지 않았습니다(마지막 단계: {waiting.Current}). " +
                                  "색인 중이면 끝난 뒤 다시 시도하세요.");
                return;
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                // 새 요청이 이 요청을 대신했습니다. 상태 표시는 새 요청이 씁니다.
                return;
            }
            catch (LspConnectionClosedException)
            {
                await NotifyAsync("clangd 연결이 끊겼습니다. 다시 실행하면 새로 시작합니다.");
                return;
            }
            catch (LspRequestException exception)
            {
                await NotifyAsync($"{Name(kind)}을(를) 완료하지 못했습니다: {exception.Message}");
                return;
            }
        }
        finally
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            waiting.Dispose();
            if (ReferenceEquals(active, request)) active = null;
        }

        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var incomplete = result.Progress.Active
            ? $"색인 진행 중 {result.Progress.Done:N0}/{result.Progress.Total:N0} · 결과가 불완전할 수 있습니다"
            : string.Empty;
        var broken = errors is null
            ? string.Empty
            : $"이 파일에 분석 오류 {errors.Count:N0}개 · {errors.FirstLine + 1}행 {errors.FirstMessage}";
        if (result.Locations.Count == 0)
        {
            await NotifyAsync((kind == Kind.Definition ? "정의를 찾지 못했습니다." : "참조를 찾지 못했습니다.") +
                              (broken.Length > 0 ? " " + broken : string.Empty) + (incomplete.Length > 0 ? " " + incomplete : string.Empty));
            return;
        }

        var symbol = result.Symbol?.QualifiedName ?? WordAt(line.GetText(), caret.Position - line.Start.Position);
        if (kind == Kind.Definition && result.Locations.Count == 1)
        {
            var target = result.Locations[0];
            NavigationLocationOpener.Open(package, target, activate: true);
            await SetStatusAsync($"{Path.GetFileName(target.Path)}:{target.Line + 1}" +
                                 (result.ResolvedOnDemand ? " · " + OnDemandNote(result, "찾았습니다") : string.Empty) +
                                 (incomplete.Length > 0 ? " · " + incomplete : string.Empty));
            return;
        }

        var notes = new List<string>();
        if (kind == Kind.Definition) notes.Add("정의 후보가 여러 개입니다");
        if (limited) notes.Add($"결과가 {referenceLimit:N0}개로 제한되었습니다");
        if (kind == Kind.References && result.CurrentFileOnly) notes.Add("네임스페이스는 이 파일의 참조만 찾습니다");
        // 정의 헤더는 모듈마다 하나라, 사용자에게는 빠졌을 수 있는 모듈 수로 알립니다(2026-10-10 검토).
        if (kind == Kind.References && result.UncheckedDefinitionFiles > 0) notes.Add($"다른 모듈 {result.UncheckedDefinitionFiles:N0}개의 참조는 확인하지 않았습니다");
        if (kind == Kind.References && result.ResolvedOnDemand) notes.Add(OnDemandNote(result, "더했습니다"));
        if (incomplete.Length > 0) notes.Add(incomplete);
        if (broken.Length > 0) notes.Add(broken);
        var dte = await package.GetServiceAsync(typeof(Microsoft.VisualStudio.Shell.Interop.SDTE)) as DTE2;
        Assumes.Present(dte);
        var solutionPath = dte.Solution?.FullName;
        var solutionDirectory = string.IsNullOrEmpty(solutionPath) ? null : Path.GetDirectoryName(solutionPath);
        var items = result.Locations.Select((location, index) => new NavigationResultItem(location, lines[index], solutionDirectory, result.SymbolKind, result.RoleOf(location))).ToArray();
        await SetStatusAsync(string.Empty);
        if (kind == Kind.References)
        {
            // 참조는 편집하면서 오가며 보도록 도킹 창에 남기고, 정의 후보는 하나를 고르면 끝나므로 대화상자로 묻습니다.
            var pane = await package.ShowToolWindowAsync(typeof(ReferencesToolWindow), 0, true, package.DisposalToken);
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (pane is not ReferencesToolWindow window) throw new InvalidOperationException("참조 결과 창을 만들지 못했습니다.");
            // 범위 빠른 필터는 심볼 탐색과 같은 범위 목록을 씁니다. 창은 VS가 배치를 복원하며 먼저 만들 수 있어 결과를 보일 때 연결합니다.
            window.Control.ScopeSource ??= () => fileIndex.SymbolScopes;
            // 짝 파일은 헤더·구현 전환과 같은 규칙·옵션으로 결과 파일 중 가장 높은 후보 하나만 고릅니다(이름만 같은 먼 파일 제외).
            var pair = new FilePairResolver(package.GetGeneralOptions().CreateFilePairingOptions())
                .FindMatches(path, items.Select(item => item.FullPath)).FirstOrDefault()?.Path;
            window.Control.Show(new ReferenceResultSet(symbol, items, string.Join(" · ", notes), DateTime.Now, path, pair));
            return;
        }

        var dialog = new NavigationResultsDialog("VisualBoost 정의 후보", symbol, items, string.Join(" · ", notes));
        if (dialog.ShowModal() == true && dialog.SelectedLocation is not null)
        {
            NavigationLocationOpener.Open(package, dialog.SelectedLocation, activate: true);
        }
    }

    // 종류를 모르면(판정 시간 초과 등) 타입으로 보지 않습니다. 생성자 이름 규칙이 재귀 호출을 빼지 않게 하기 위해서입니다.
    private static bool IsType(Core.Analysis.SourceSymbolKind? kind) =>
        kind is Core.Analysis.SourceSymbolKind.Type or Core.Analysis.SourceSymbolKind.Class or Core.Analysis.SourceSymbolKind.Struct or
            Core.Analysis.SourceSymbolKind.Union;

    /// <summary>현재 파일을 앞에 두고 경로·위치 순으로 정렬합니다.</summary>
    private static IReadOnlyList<NavigationLocation> Order(IReadOnlyList<NavigationLocation> locations, string currentPath) =>
        NavigationLocation.Normalize(locations)
            .OrderBy(l => string.Equals(l.Path, currentPath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Line)
            .ThenBy(l => l.Character)
            .ToArray();

    private async Task RunVisualStudioCommandAsync(Kind kind, string? reason)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (reason is not null && !service.Settings.FallbackToVisualStudio)
        {
            await NotifyAsync(reason);
            return;
        }

        // DTE 명령 이름으로 실행하면 인수를 요구하는 대화상자가 뜨는 버전이 있어, 단축키와 같은 경로인
        // 표준 명령 ID를 셸에 게시합니다. 이 명령이 끝난 뒤 활성 편집기 문맥에서 실행됩니다.
        var shell = await package.GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
        Assumes.Present(shell);
        var group = VSConstants.GUID_VSStandardCommandSet97;
        object? argument = null;
        var command = kind == Kind.Definition ? VSConstants.VSStd97CmdID.GotoDefn : VSConstants.VSStd97CmdID.FindReferences;
        if (ErrorHandler.Failed(shell.PostExecCommand(ref group, (uint)command, 0, ref argument)))
        {
            await NotifyAsync(reason ?? "현재 위치에서는 탐색할 수 없습니다.");
            return;
        }

        if (reason is not null) await SetStatusAsync(reason + " Visual Studio 기본 탐색을 실행했습니다.");
    }

    private static IWpfTextView? ActiveView(IVsTextManager manager, IVsEditorAdaptersFactoryService adapters)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (manager.GetActiveView(1, null, out var native) != 0 || native is null) return null;
        var view = adapters.GetWpfTextView(native);
        return view is { IsClosed: false } ? view : null;
    }

    /// <summary>
    /// 요청 시점에 정의 파일을 분석한 이유를 알립니다. 색인을 마친 뒤에도 엔진 cpp는 색인 범위 밖이라 처음 찾을 때 분석하므로, 색인이
    /// 덜 된 것으로 오해하지 않게 엔진과 프로젝트를 나눠 씁니다.
    /// </summary>
    private static string OnDemandNote(NavigationResult result, string verb) =>
        result.ResolvedFromEngine ? $"색인하지 않는 엔진 cpp를 바로 분석해 정의를 {verb}" : $"아직 색인되지 않은 파일을 바로 분석해 정의를 {verb}";

    private static string WordAt(string text, int column)
    {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        var start = Math.Max(0, Math.Min(column, text.Length));
        var end = start;
        while (start > 0 && IsWord(text[start - 1])) start--;
        while (end < text.Length && IsWord(text[end])) end++;
        return end > start ? text.Substring(start, end - start) : text.Trim();
    }

    private static string Name(Kind kind) => kind == Kind.Definition ? "정의 찾기" : "참조 찾기";

    private async Task NotifyAsync(string message)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        SystemSounds.Beep.Play();
        await SetStatusAsync(message);
    }

    private async Task SetStatusAsync(string message)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var status = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        if (status is null) return;
        if (message.Length == 0) status.Clear();
        else status.SetText("VisualBoost: " + message);
    }

    /// <summary>
    /// 요청이 끝날 때까지 상태 표시줄에 단계·경과 시간·색인 진행을 1초마다 보입니다. UI thread에서만 씁니다.
    /// </summary>
    /// <remarks>
    /// 큰 Unreal 파일은 처음 열 때 분석이 수 초~수십 초 걸리고 색인 중에는 더 늦어지는데, 고정 문구만 보이면 멈춘 것처럼 보여
    /// 다시 누르게 되었습니다(2026-10-08 회사 사용 피드백: 느리거나 안 됨).
    /// </remarks>
    private sealed class RequestStatus : IDisposable
    {
        private readonly IVsStatusbar? statusBar;
        private readonly SemanticNavigationService service;
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly DispatcherTimer timer;

        public RequestStatus(IVsStatusbar? statusBar, string stage, SemanticNavigationService service)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.statusBar = statusBar;
            this.service = service;
            Current = stage;
            Show();
            timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Show(), Dispatcher.CurrentDispatcher);
        }

        /// <summary>마지막으로 보인 단계입니다.</summary>
        public string Current { get; private set; }

        public void Stage(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Current = text;
            Show();
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            timer.Stop();
        }

        private void Show()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var seconds = (int)watch.Elapsed.TotalSeconds;
            var index = service.Current?.Progress is { Active: true, Total: > 0 } progress ? $" · 색인 {progress.Done:N0}/{progress.Total:N0}" : string.Empty;
            // 분석 진행 문구(AnalysisStatusText)처럼 " · "로만 나눕니다. 단계 문구 끝에 말줄임표를 덧붙이지 않습니다(2026-10-10 검토).
            statusBar?.SetText("VisualBoost: " + Current + (seconds >= 2 ? $" · {seconds}초" : string.Empty) + index);
        }
    }
}
