using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VisualBoost.Commands;
using VisualBoost.Options;
using VisualBoost.SemanticNavigation;
using VisualBoost.Services;

namespace VisualBoost;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("VisualBoost", "파일·심볼 탐색과 C++ 편집을 지원합니다.", "0.46.7")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
// 폴더 열기 작업 영역(CMake 등)은 Solution 존재 상태를 켜지 않으므로 따로 등록합니다.
[ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideOptionPage(typeof(GeneralOptionsPage), "VisualBoost", "General", 0, 0, true)]
[ProvideProfile(typeof(GeneralOptionsPage), "VisualBoost", "General", 0, 0, true)]
[ProvideOptionPage(typeof(FileSearchOptionsPage), "VisualBoost", "파일 탐색", 0, 0, true)]
[ProvideProfile(typeof(FileSearchOptionsPage), "VisualBoost", "파일 탐색", 0, 0, true)]
[ProvideOptionPage(typeof(IndexingOptionsPage), "VisualBoost", "인덱싱", 0, 0, true)]
[ProvideProfile(typeof(IndexingOptionsPage), "VisualBoost", "인덱싱", 0, 0, true)]
[ProvideOptionPage(typeof(ColoringOptionsPage), "VisualBoost", "Coloring", 0, 0, true)]
[ProvideProfile(typeof(ColoringOptionsPage), "VisualBoost", "Coloring", 0, 0, true)]
[ProvideOptionPage(typeof(CompletionOptionsPage), "VisualBoost", "자동완성", 0, 0, true)]
[ProvideProfile(typeof(CompletionOptionsPage), "VisualBoost", "자동완성", 0, 0, true)]
[ProvideOptionPage(typeof(DocumentNavigationOptionsPage), "VisualBoost", "문서 함수 탐색", 0, 0, true)]
[ProvideProfile(typeof(DocumentNavigationOptionsPage), "VisualBoost", "문서 함수 탐색", 0, 0, true)]
[ProvideOptionPage(typeof(CodeGenerationOptionsPage), "VisualBoost", "코드 생성", 0, 0, true)]
[ProvideProfile(typeof(CodeGenerationOptionsPage), "VisualBoost", "코드 생성", 0, 0, true)]
[ProvideOptionPage(typeof(EditorToolsOptionsPage), "VisualBoost", "편집 도구", 0, 0, true)]
[ProvideProfile(typeof(EditorToolsOptionsPage), "VisualBoost", "편집 도구", 0, 0, true)]
// 참조 결과 창은 출력 창과 같은 도킹 영역에 탭으로 엽니다.
[ProvideToolWindow(typeof(UI.ReferencesToolWindow), Style = VsDockStyle.Tabbed, Window = "34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3")]
[ProvideOptionPage(typeof(CodeNavigationOptionsPage), "VisualBoost", "정의·참조 탐색", 0, 0, true)]
[ProvideProfile(typeof(CodeNavigationOptionsPage), "VisualBoost", "정의·참조 탐색", 0, 0, true)]
[Guid(PackageGuidString)]
public sealed class VisualBoostPackage : AsyncPackage
{
    public const string PackageGuidString = "d54a4377-4869-4f58-a583-5318b38d77f2";

    private readonly SolutionFileIndexService fileIndex = new();
    // 다시 연 Solution에서 그대로인 C++ 프로젝트의 항목을 자동화로 다시 열거하지 않게 합니다.
    private readonly ProjectMembershipCache projectMembership = new();
    private SemanticNavigationService? navigation;
    private SolutionEvents? solutionEvents;
    private BuildEvents? buildEvents;
    private DocumentEvents? documentEvents;
    private ProjectItemsEvents? projectItemsEvents;
    private CancellationTokenSource? discoveryCancellation;
    // 마지막으로 시작한 프로젝트 항목 수집입니다. 인덱스 다시 만들기가 취소한 수집의 끝(프로젝트 소속 저장)을 기다릴 때 씁니다. UI thread에서만 씁니다.
    private JoinableTask? discoveryWork;
    private AnalysisStatusBar? analysisStatus;

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        // 문서 색상은 DTE 프로젝트 이벤트 연결이나 파일 인덱스 초기화를 기다리지 않습니다.
        var components = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        Assumes.Present(components);
        Coloring.SharedColorPalette.Attach(components.GetService<IEditorFormatMapService>().GetEditorFormatMap("text"));
        Coloring.ColoringSettings.Publish(((ColoringOptionsPage)GetDialogPage(typeof(ColoringOptionsPage))).CreateSettings());
        var dte = await GetServiceAsync(typeof(SDTE)) as DTE2;
        Assumes.Present(dte);

        solutionEvents = dte.Events.SolutionEvents;
        solutionEvents.Opened += OnSolutionOpened;
        solutionEvents.AfterClosing += OnSolutionClosed;
        solutionEvents.ProjectAdded += OnProjectChanged;
        solutionEvents.ProjectRemoved += OnProjectChanged;
        solutionEvents.ProjectRenamed += OnProjectRenamed;
        documentEvents = dte.Events.DocumentEvents;
        documentEvents.DocumentOpened += OnDocumentOpened;
        projectItemsEvents = ((Events2)dte.Events).ProjectItemsEvents;
        projectItemsEvents.ItemAdded += OnProjectItemChanged;
        projectItemsEvents.ItemRemoved += OnProjectItemChanged;
        projectItemsEvents.ItemRenamed += OnProjectItemRenamed;
        buildEvents = dte.Events.BuildEvents;
        buildEvents.OnBuildDone += OnBuildDone;
        RecordOpenDocuments(dte);

        MigrateLegacyOptions();
        ((CompletionOptionsPage)GetDialogPage(typeof(CompletionOptionsPage))).Publish();
        ((DocumentNavigationOptionsPage)GetDialogPage(typeof(DocumentNavigationOptionsPage))).Publish();
        GetGeneralOptions().Publish();
        var statusBar = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        // 정의·참조 탐색은 Solution 이벤트보다 먼저 만들어 이미 열린 Solution도 처리합니다.
        navigation = new SemanticNavigationService(fileIndex, ((CodeNavigationOptionsPage)GetDialogPage(typeof(CodeNavigationOptionsPage))).CreateSettings(),
            token => VcProjectCollector.CollectAsync(dte, token));
        SemanticNavigationRuntime.Service = navigation;
        navigation.NavigatorStarted += focusOnly => JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            SemanticDocumentTracker.WarmRecent(focusOnly);
        }).FileAndForget("VisualBoost/SemanticNavigation/Warm");
        var currentNavigation = navigation;
        if (statusBar is not null) analysisStatus = new AnalysisStatusBar(fileIndex, statusBar, () => currentNavigation.StatusText);
        StartFileIndex(dte);
        if (!string.IsNullOrEmpty(dte.Solution?.FullName)) navigation.SolutionOpened(dte.Solution!.FullName);
        CommentLinks.CommentLinkRuntime.Enabled = ((EditorToolsOptionsPage)GetDialogPage(typeof(EditorToolsOptionsPage))).CommentLinksEnabled;
        CommentLinks.CommentLinkRuntime.Index = fileIndex;
        UI.CodePreviewStyle.NameKind = new UI.CodePreviewNames(fileIndex).Resolve;
        await SwitchHeaderSourceCommand.InitializeAsync(this, fileIndex, cancellationToken);
        await OpenOptionsCommand.InitializeAsync(this, cancellationToken);
        await ShowIndexStatusCommand.InitializeAsync(this, fileIndex, cancellationToken);
        await RebuildIndexCommand.InitializeAsync(this, fileIndex, cancellationToken);
        await OpenFileSearchCommand.InitializeAsync(this, fileIndex, cancellationToken);
        await OpenSymbolSearchCommand.InitializeAsync(this, fileIndex, cancellationToken);
        await OpenDocumentMembersCommand.InitializeAsync(this, cancellationToken);
        await GenerateFunctionCommand.InitializeAsync(this, fileIndex, cancellationToken);
        await CodeContextMenuCommand.InitializeAsync(this, cancellationToken);
        await SemanticNavigationCommand.InitializeAsync(this, navigation, fileIndex, cancellationToken);
    }

    // 참조 결과 창이 열린 채로 다시 시작해도 창 복원이 패키지를 UI thread에서 동기 로드하지 않게 비동기로 만듭니다.
    public override IVsAsyncToolWindowFactory? GetAsyncToolWindowFactory(Guid toolWindowType) =>
        toolWindowType == typeof(UI.ReferencesToolWindow).GUID ? this : null;

    protected override string GetToolWindowTitle(Type toolWindowType, int id) =>
        toolWindowType == typeof(UI.ReferencesToolWindow) ? UI.ReferencesToolWindow.Title : base.GetToolWindowTitle(toolWindowType, id);

    // 창 생성에 넘길 준비 작업이 없으므로 기본 생성자를 쓰게 합니다.
    protected override Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken) =>
        toolWindowType == typeof(UI.ReferencesToolWindow) ? Task.FromResult<object>(ToolWindowCreationContext.Unspecified) : base.InitializeToolWindowAsync(toolWindowType, id, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            discoveryCancellation?.Cancel();
            JoinableTaskFactory.Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                UnsubscribeSolutionEvents();
                analysisStatus?.Dispose();
                analysisStatus = null;
                Completion.CompletionRuntime.GetSnapshot = null;
                CommentLinks.CommentLinkRuntime.Index = null;
                UI.CodePreviewStyle.NameKind = null;
                SemanticNavigationRuntime.Service = null;
                Coloring.ColoringSettings.Publish(new Coloring.ColoringSettings(false, new string[8]));
                Coloring.SharedColorPalette.Detach();
            });
            navigation?.Dispose();
            fileIndex.Dispose();
            solutionEvents = null;
            documentEvents = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 패키지보다 먼저 복원된 문서도 분석 우선순위에 넣습니다. 활성 문서를 마지막에 기록해 가장 앞에 둡니다.
    /// 우선순위 힌트일 뿐이므로 읽지 못한 문서는 건너뛰고 패키지 초기화를 계속합니다.
    /// </summary>
    private void RecordOpenDocuments(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            foreach (Document document in dte.Documents)
            {
                try { fileIndex.RecordRecentFile(document.FullName); }
                catch (COMException) { }
            }

            fileIndex.RecordRecentFile(dte.ActiveDocument?.FullName);
        }
        catch (COMException)
        {
        }
    }

    private void UnsubscribeSolutionEvents()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (solutionEvents is null)
        {
            return;
        }

        solutionEvents.Opened -= OnSolutionOpened;
        solutionEvents.AfterClosing -= OnSolutionClosed;
        solutionEvents.ProjectAdded -= OnProjectChanged;
        solutionEvents.ProjectRemoved -= OnProjectChanged;
        solutionEvents.ProjectRenamed -= OnProjectRenamed;
        if (documentEvents is not null)
        {
            documentEvents.DocumentOpened -= OnDocumentOpened;
        }
        if (projectItemsEvents is not null)
        {
            projectItemsEvents.ItemAdded -= OnProjectItemChanged;
            projectItemsEvents.ItemRemoved -= OnProjectItemChanged;
            projectItemsEvents.ItemRenamed -= OnProjectItemRenamed;
        }
        if (buildEvents is not null)
        {
            buildEvents.OnBuildDone -= OnBuildDone;
        }
    }

    internal bool IsQuickIncludeEnabled() => ((EditorToolsOptionsPage)GetDialogPage(typeof(EditorToolsOptionsPage))).QuickIncludeEnabled;

    private void OnDocumentOpened(Document document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        fileIndex.RecordRecentFile(document.FullName);
    }

    private void OnSolutionOpened()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await GetServiceAsync(typeof(SDTE)) as DTE2;
            if (dte is not null)
            {
                StartFileIndex(dte);
                navigation?.SolutionOpened(dte.Solution.FullName ?? string.Empty);
            }
        }).FileAndForget("VisualBoost/StartFileIndex");
    }

    private void OnSolutionClosed()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        (FindToolWindow(typeof(UI.ReferencesToolWindow), 0, false) as UI.ReferencesToolWindow)?.Control.Clear();
        discoveryCancellation?.Cancel();
        Completion.CompletionRuntime.GetSnapshot = null;
        fileIndex.Clear();
        navigation?.SolutionClosed();
    }

    private void OnBuildDone(vsBuildScope scope, vsBuildAction action)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // 빌드가 응답 파일을 새로 썼을 수 있습니다. 명령이 바뀌었을 때만 clangd를 다시 시작합니다.
        if (action != vsBuildAction.vsBuildActionClean) navigation?.BuildCompleted();
    }

    private void OnProjectChanged(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        RefreshFileIndexRoots();
    }

    private void OnProjectRenamed(Project project, string oldName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        RefreshFileIndexRoots();
    }

    private void RefreshFileIndexRoots()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = GetService(typeof(SDTE)) as DTE2;
        if (dte is not null)
        {
            StartFileIndex(dte, refresh: true);
        }
    }

    /// <param name="refresh">
    /// 프로젝트·항목 변경으로 같은 Solution을 다시 수집합니다. 인덱스를 비우지 않고 수집 결과와의 차이만 반영합니다(<see cref="SolutionFileIndexService.Start"/>).
    /// 비우면 다시 채울 때까지 이름 검색이 빠지고 저장된 분석을 다시 읽어 공개합니다.
    /// </param>
    private void StartFileIndex(DTE2 dte, bool refresh = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Completion.CompletionRuntime.GetSnapshot = () => fileIndex.CompletionSnapshot;
        var configuration = GetIndexingOptions().CreateConfiguration();
        fileIndex.Configure(configuration);
        var membership = configuration.UsePersistentFileCache ? projectMembership : null;
        discoveryCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        discoveryCancellation = cancellation;
        if (!refresh) fileIndex.BeginDiscovery(dte.Solution.FullName ?? string.Empty);
        var work = JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                // 프로젝트 일괄 추가 이벤트는 한 번의 스냅샷 수집으로 합칩니다.
                await Task.Delay(200, cancellation.Token);
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                var discovery = await SolutionSearchRootCollector.CollectAsync(dte, cancellation.Token,
                    files => fileIndex.PublishDiscoveredFiles(files, cancellation.Token), membership);
                cancellation.Token.ThrowIfCancellationRequested();
                // 큰 파일 목록의 정렬·비교(엔진 규모 약 0.1초)를 UI thread 밖에서 합니다. 그사이 새 수집이 시작됐으면 Start가 잠금 안에서 건너뜁니다.
                await TaskScheduler.Default;
                fileIndex.Start(discovery, cancellationToken: cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                fileIndex.FailDiscovery(exception.Message, cancellation.Token);
            }
            finally
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ReferenceEquals(discoveryCancellation, cancellation)) discoveryCancellation = null;
                cancellation.Dispose();
            }
        });
        discoveryWork = work;
        work.FileAndForget("VisualBoost/CollectProjectFiles");
    }

    // 다시 만들기가 취소한 수집이 끝나기를 기다리는 상한입니다. 수집은 프로젝트·묶음마다 취소를 확인하므로 보통 곧 끝납니다.
    private static readonly TimeSpan DiscoveryStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 인덱스 다시 만들기: 현재 Solution의 저장된 프로젝트 항목·파일 목록·이름 분석을 지우고 처음 열 때처럼 다시 수집·분석합니다. clangd 색인은
    /// clangd가 파일 내용으로 직접 검증하므로 지우지 않습니다. 지우지 못한 저장 파일이 있으면 false입니다(다시 수집은 그대로 시작).
    /// </summary>
    internal async Task<bool> RebuildFileIndexAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        var dte = await GetServiceAsync(typeof(SDTE)) as DTE2;
        var solution = dte?.Solution?.FullName;
        if (dte is null || string.IsNullOrEmpty(solution)) return true;
        discoveryCancellation?.Cancel();
        var collecting = discoveryWork;
        await TaskScheduler.Default;
        // 취소한 수집이 마지막 묶음에서 프로젝트 소속을 저장하는 중이면 아래에서 지운 파일을 되살리므로 끝나기를 기다립니다(2026-10-10 검토 91).
        // 수집의 끝은 UI thread로 돌아가므로 join해 기다립니다.
        if (collecting is not null) await Task.WhenAny(collecting.JoinAsync(), Task.Delay(DiscoveryStopTimeout)).ConfigureAwait(false);
        var deleted = await fileIndex.ResetAsync().ConfigureAwait(false);
        deleted = projectMembership.Delete(solution!) && deleted;
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        StartFileIndex(dte);
        return deleted;
    }

    private void OnProjectItemChanged(ProjectItem item)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        RefreshFileIndexRoots();
    }
    private void OnProjectItemRenamed(ProjectItem item, string oldName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        RefreshFileIndexRoots();
    }

    internal GeneralOptionsPage GetGeneralOptions()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return (GeneralOptionsPage)GetDialogPage(typeof(GeneralOptionsPage));
    }

    internal bool IsCodeGenerationEnabled()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ((CodeGenerationOptionsPage)GetDialogPage(typeof(CodeGenerationOptionsPage))).Enabled;
    }

    internal FileSearchOptionsPage GetFileSearchOptions()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return (FileSearchOptionsPage)GetDialogPage(typeof(FileSearchOptionsPage));
    }

    internal IndexingOptionsPage GetIndexingOptions()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return (IndexingOptionsPage)GetDialogPage(typeof(IndexingOptionsPage));
    }

    internal void ShowGeneralOptions()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ShowOptionPage(typeof(GeneralOptionsPage));
    }

    private void MigrateLegacyOptions()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var general = GetGeneralOptions();
        var fileSearch = GetFileSearchOptions();
        if (!fileSearch.LegacySettingsMigrated)
        {
            fileSearch.FileSearchScope = general.FileSearchScope;
            fileSearch.FileSearchWidth = general.FileSearchWidth;
            fileSearch.FileSearchHeight = general.FileSearchHeight;
            fileSearch.FileSearchLeft = general.FileSearchLeft;
            fileSearch.FileSearchTop = general.FileSearchTop;
            fileSearch.FileSearchPlacementSaved = general.FileSearchPlacementSaved;
            fileSearch.LegacySettingsMigrated = true;
            fileSearch.SaveSettingsToStorage();
        }

        var indexing = GetIndexingOptions();
        if (!indexing.LegacySettingsMigrated)
        {
            indexing.ShowIndexCountOnFailure = general.ShowIndexCountOnFailure;
            indexing.LegacySettingsMigrated = true;
            indexing.SaveSettingsToStorage();
        }
    }
}
