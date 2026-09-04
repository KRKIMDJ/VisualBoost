using System;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Core.FilePairing;
using VisualBoost.Services;
using VisualBoost.UI;

namespace VisualBoost.Commands;

internal sealed class SwitchHeaderSourceCommand
{
    private static readonly Guid CommandSet = new("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e");

    private readonly VisualBoostPackage package;
    private readonly SolutionFileIndexService fileIndex;

    private SwitchHeaderSourceCommand(
        VisualBoostPackage package,
        SolutionFileIndexService fileIndex,
        OleMenuCommandService commandService)
    {
        this.package = package;
        this.fileIndex = fileIndex;
        var commandId = new CommandID(CommandSet, CommandIds.SwitchHeaderSource);
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
        _ = new SwitchHeaderSourceCommand(package, fileIndex, commandService);
    }

    private void Execute(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        package.JoinableTaskFactory.RunAsync(ExecuteAsync).FileAndForget("VisualBoost/SwitchHeaderSource");
    }

    private async Task ExecuteAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
        Assumes.Present(dte);
        var currentFile = dte.ActiveDocument?.FullName;
        var solutionFile = dte.Solution?.FullName;

        if (string.IsNullOrWhiteSpace(currentFile) || string.IsNullOrWhiteSpace(solutionFile))
        {
            await ShowStatusAsync("열린 C++ 파일과 Solution이 필요합니다.");
            return;
        }

        var activeFile = currentFile!;
        if (fileIndex.Count == 0)
        {
            await fileIndex.WaitUntilReadyAsync();
        }

        var stem = Path.GetFileNameWithoutExtension(activeFile);
        var candidates = fileIndex.FindByStem(stem);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var options = package.GetGeneralOptions();
        var resolver = new FilePairResolver(options.CreateFilePairingOptions());
        var matches = resolver.FindMatches(activeFile, candidates);
        if (matches.Count == 0 && fileIndex.GetSnapshot().State == SolutionFileIndexState.Building)
        {
            await fileIndex.WaitUntilReadyAsync();
            candidates = fileIndex.FindByStem(stem);
            matches = resolver.FindMatches(activeFile, candidates);
        }

        if (matches.Count == 0)
        {
            var diagnostic = package.GetIndexingOptions().ShowIndexCountOnFailure
                ? $" 인덱스: {fileIndex.Count:N0}개 파일"
                : string.Empty;
            await ShowStatusAsync($"'{Path.GetFileName(activeFile)}'의 대응 파일을 찾지 못했습니다.{diagnostic}");
            return;
        }

        var selectedPath = matches[0].Path;
        if (matches.Count > 1)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var context = FileSearchContext.Collect(dte);
            var dialog = new FileSearchDialog(
                fileIndex,
                package.GetFileSearchOptions(),
                context.PreferredRoot,
                context.SolutionRoot,
                context.OpenFiles,
                context.Projects,
                matches.Select(match => match.Path).ToArray());
            if (dialog.ShowModal() != true || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                return;
            }

            selectedPath = dialog.SelectedPath!;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        fileIndex.RecordRecentFile(selectedPath);
        dte.ItemOperations.OpenFile(selectedPath);
    }

    private async Task ShowStatusAsync(string message)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var statusBar = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        statusBar?.SetText(message);
    }
}
