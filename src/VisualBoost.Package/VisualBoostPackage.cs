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
using VisualBoost.Commands;
using VisualBoost.Options;
using VisualBoost.Services;

namespace VisualBoost;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("VisualBoost", "C++ 탐색 작업을 빠르게 수행합니다.", "0.12.3")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideToolWindow(
    typeof(UI.SymbolUsagesToolWindow),
    Style = VsDockStyle.Tabbed,
    Window = EnvDTE.Constants.vsWindowKindOutput,
    DockedHeight = 320)]
[ProvideOptionPage(typeof(GeneralOptionsPage), "VisualBoost", "General", 0, 0, true)]
[ProvideProfile(typeof(GeneralOptionsPage), "VisualBoost", "General", 0, 0, true)]
[ProvideOptionPage(typeof(FileSearchOptionsPage), "VisualBoost", "파일 탐색", 0, 0, true)]
[ProvideProfile(typeof(FileSearchOptionsPage), "VisualBoost", "파일 탐색", 0, 0, true)]
[ProvideOptionPage(typeof(IndexingOptionsPage), "VisualBoost", "인덱싱", 0, 0, true)]
[ProvideProfile(typeof(IndexingOptionsPage), "VisualBoost", "인덱싱", 0, 0, true)]
[ProvideOptionPage(typeof(ColoringOptionsPage), "VisualBoost", "Coloring", 0, 0, true)]
[ProvideProfile(typeof(ColoringOptionsPage), "VisualBoost", "Coloring", 0, 0, true)]
[Guid(PackageGuidString)]
public sealed class VisualBoostPackage : AsyncPackage
{
    public const string PackageGuidString = "d54a4377-4869-4f58-a583-5318b38d77f2";

    private readonly SolutionFileIndexService fileIndex = new();
    private SolutionEvents? solutionEvents;
    private DocumentEvents? documentEvents;

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
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
        fileIndex.RecordRecentFile(dte.ActiveDocument?.FullName);

        MigrateLegacyOptions();
        var components = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        Assumes.Present(components);
        Coloring.SharedColorPalette.Attach(components.GetService<IEditorFormatMapService>().GetEditorFormatMap("text"));
        Coloring.ColoringSettings.Publish(((ColoringOptionsPage)GetDialogPage(typeof(ColoringOptionsPage))).CreateSettings());
        StartFileIndex(dte);
        await SwitchHeaderSourceCommand.InitializeAsync(this, fileIndex, cancellationToken);
        await OpenOptionsCommand.InitializeAsync(this, cancellationToken);
        await ShowIndexStatusCommand.InitializeAsync(this, fileIndex, cancellationToken);
        await OpenFileSearchCommand.InitializeAsync(this, fileIndex, cancellationToken);
        await OpenSymbolSearchCommand.InitializeAsync(this, fileIndex, cancellationToken);
        await NavigateToDefinitionCommand.InitializeAsync(this, cancellationToken);
        await FindSymbolUsagesCommand.InitializeAsync(this, fileIndex, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            JoinableTaskFactory.Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                UnsubscribeSolutionEvents();
                Coloring.ColoringSettings.Publish(new Coloring.ColoringSettings(false, new string[8]));
                Coloring.SharedColorPalette.Detach();
            });
            fileIndex.Dispose();
            solutionEvents = null;
            documentEvents = null;
        }

        base.Dispose(disposing);
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
    }

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
            }
        }).FileAndForget("VisualBoost/StartFileIndex");
    }

    private void OnSolutionClosed()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        fileIndex.Clear();
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
            StartFileIndex(dte);
        }
    }

    private void StartFileIndex(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        fileIndex.Configure(GetIndexingOptions().CreateConfiguration());
        fileIndex.Start(SolutionSearchRootCollector.Collect(dte));
    }

    internal GeneralOptionsPage GetGeneralOptions()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return (GeneralOptionsPage)GetDialogPage(typeof(GeneralOptionsPage));
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
