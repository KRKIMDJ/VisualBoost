using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Services;
using VisualBoost.UI;

namespace VisualBoost.Commands;

internal sealed class OpenSymbolSearchCommand
{
    private static readonly Guid CommandSet = new("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e");

    private readonly VisualBoostPackage package;
    private readonly SolutionFileIndexService fileIndex;

    private OpenSymbolSearchCommand(
        VisualBoostPackage package,
        SolutionFileIndexService fileIndex,
        OleMenuCommandService commandService)
    {
        this.package = package;
        this.fileIndex = fileIndex;
        commandService.AddCommand(new OleMenuCommand(
            Execute,
            new CommandID(CommandSet, CommandIds.OpenSymbolSearch)));
    }

    public static async Task InitializeAsync(
        VisualBoostPackage package,
        SolutionFileIndexService fileIndex,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        Assumes.Present(commandService);
        _ = new OpenSymbolSearchCommand(package, fileIndex, commandService);
    }

    private void Execute(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SearchCommandRunner.Run(package, "OpenSymbolSearch", ExecuteAsync);
    }

    private async Task ExecuteAsync()
    {
        var snapshot = fileIndex.GetSnapshot();
        if (snapshot.State == SolutionFileIndexState.Empty && snapshot.SymbolCount == 0)
        {
            await ShowStatusAsync("심볼 탐색에는 열린 Solution이 필요합니다.");
            return;
        }

        if (snapshot.State == SolutionFileIndexState.Faulted && snapshot.SymbolCount == 0)
        {
            await ShowStatusAsync($"심볼 인덱스를 준비하지 못했습니다. {snapshot.LastError}");
            return;
        }

        if (snapshot.State != SolutionFileIndexState.Building && !snapshot.IsAnalyzing && snapshot.SymbolCount == 0)
        {
            var reason = string.IsNullOrWhiteSpace(snapshot.AnalysisError)
                ? "인덱싱 옵션에서 C++ 소스 분석이 활성화되어 있는지 확인하세요."
                : snapshot.AnalysisError;
            await ShowStatusAsync($"탐색할 심볼이 없습니다. {reason}");
            return;
        }

        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
        Assumes.Present(dte);
        var context = FileSearchContext.Collect(dte);
        var dialog = new SymbolSearchDialog(fileIndex, context.Projects);
        if (dialog.ShowModal() != true || dialog.SelectedLocation is null)
        {
            return;
        }

        OpenLocation(dte, dialog.SelectedLocation);
    }

    private static void OpenLocation(DTE2 dte, Core.Analysis.SourceSymbolLocation location)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var window = dte.ItemOperations.OpenFile(location.Path);
        window?.Activate();
        if (dte.ActiveDocument?.Selection is TextSelection selection)
        {
            selection.MoveToLineAndOffset(
                Math.Max(1, location.Line),
                Math.Max(1, location.Column),
                Extend: false);
        }
    }

    private async Task ShowStatusAsync(string message)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var statusBar = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        statusBar?.SetText(message);
    }
}
