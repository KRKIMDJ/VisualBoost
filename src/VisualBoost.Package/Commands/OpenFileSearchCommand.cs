using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Core.Searching;
using VisualBoost.Core.SemanticNavigation;
using VisualBoost.SemanticNavigation;
using VisualBoost.Services;
using VisualBoost.UI;

namespace VisualBoost.Commands;

internal sealed class OpenFileSearchCommand
{
    private static readonly Guid CommandSet = new("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e");

    private readonly VisualBoostPackage package;
    private readonly SolutionFileIndexService fileIndex;

    private OpenFileSearchCommand(
        VisualBoostPackage package,
        SolutionFileIndexService fileIndex,
        OleMenuCommandService commandService)
    {
        this.package = package;
        this.fileIndex = fileIndex;
        var commandId = new CommandID(CommandSet, CommandIds.OpenFileSearch);
        commandService.AddCommand(new OleMenuCommand(Execute, commandId));
    }

    public static async Task InitializeAsync(
        VisualBoostPackage package,
        SolutionFileIndexService fileIndex,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        Assumes.Present(commandService);
        _ = new OpenFileSearchCommand(package, fileIndex, commandService);
    }

    private void Execute(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SearchCommandRunner.Run(package, "OpenFileSearch", ExecuteAsync);
    }

    private async Task ExecuteAsync()
    {
        var snapshot = fileIndex.GetSnapshot();

        if (snapshot.State == SolutionFileIndexState.Faulted && snapshot.FileCount == 0)
        {
            await ShowStatusAsync($"파일 인덱스를 사용할 수 없습니다: {snapshot.LastError}");
            return;
        }

        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
        Assumes.Present(dte);
        var context = FileSearchContext.Collect(dte);
        var dialog = new FileSearchDialog(
            fileIndex,
            package.GetFileSearchOptions(),
            context.PreferredRoot,
            context.SolutionRoot,
            context.OpenFiles,
            context.Projects,
            initialQuery: SearchQuerySeed.ForFileSearch(FileSearchContext.GetSelectedText(dte)));
        if (dialog.ShowModal() != true || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var path = dialog.SelectedPath!;
        fileIndex.RecordRecentFile(path);
        if (dialog.SelectedLine is { } line)
        {
            // 줄 위치가 파일 길이를 넘으면 마지막 줄로 맞추고 화면 가운데에 보입니다.
            var column = Math.Max(0, (dialog.SelectedColumn ?? 1) - 1);
            NavigationLocationOpener.Open(package, new NavigationLocation(path, line - 1, column, line - 1, column), activate: true);
            return;
        }

        dte.ItemOperations.OpenFile(path);
    }

    private async Task ShowStatusAsync(string message)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var statusBar = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        statusBar?.SetText(message);
    }
}
