using System;
using System.ComponentModel.Design;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Core.FilePairing;
using VisualBoost.Services;

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
        await fileIndex.WaitUntilReadyAsync();
        var stem = Path.GetFileNameWithoutExtension(activeFile);
        var candidates = fileIndex.FindByStem(stem);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var options = package.GetGeneralOptions();
        var resolver = new FilePairResolver(options.CreateFilePairingOptions());
        var matches = resolver.FindMatches(activeFile, candidates);
        if (matches.Count == 0)
        {
            var diagnostic = options.ShowIndexCountOnFailure
                ? $" 인덱스: {fileIndex.Count:N0}개 파일"
                : string.Empty;
            await ShowStatusAsync($"'{Path.GetFileName(activeFile)}'의 대응 파일을 찾지 못했습니다.{diagnostic}");
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        dte.ItemOperations.OpenFile(matches[0].Path);

        if (matches.Count > 1)
        {
            await ShowStatusAsync($"후보 {matches.Count}개 중 가장 가까운 파일을 열었습니다: {matches[0].Path}");
        }
    }

    private async Task ShowStatusAsync(string message)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var statusBar = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        statusBar?.SetText(message);
    }
}
