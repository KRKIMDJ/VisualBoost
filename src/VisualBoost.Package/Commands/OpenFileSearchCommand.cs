using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
        package.JoinableTaskFactory.RunAsync(ExecuteAsync).FileAndForget("VisualBoost/OpenFileSearch");
    }

    private async Task ExecuteAsync()
    {
        var snapshot = fileIndex.GetSnapshot();
        if (snapshot.State == SolutionFileIndexState.Building && snapshot.FileCount == 0)
        {
            await ShowStatusAsync("VisualBoost가 파일 인덱싱을 마칠 때까지 기다리는 중입니다...");
            await fileIndex.WaitUntilReadyAsync();
            snapshot = fileIndex.GetSnapshot();
        }

        if (snapshot.State == SolutionFileIndexState.Faulted && snapshot.FileCount == 0)
        {
            await ShowStatusAsync($"파일 인덱스를 사용할 수 없습니다: {snapshot.LastError}");
            return;
        }

        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
        Assumes.Present(dte);
        var preferredRoot = GetActiveProjectDirectory(dte);
        var dialog = new FileSearchDialog(
            fileIndex,
            package.GetGeneralOptions(),
            preferredRoot,
            GetSolutionDirectory(dte),
            GetOpenDocumentPaths(dte),
            SolutionProjectCatalog.Collect(dte, preferredRoot));
        if (dialog.ShowModal() != true || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        fileIndex.RecordRecentFile(dialog.SelectedPath);
        dte.ItemOperations.OpenFile(dialog.SelectedPath);
    }

    private static string? GetActiveProjectDirectory(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var activeDocument = dte.ActiveDocument;
            var projectFile = activeDocument?.ProjectItem?.ContainingProject?.FullName;
            var projectDirectory = string.IsNullOrWhiteSpace(projectFile)
                ? null
                : Path.GetDirectoryName(projectFile);
            var activeDocumentPath = activeDocument?.FullName;
            var documentDirectory = string.IsNullOrWhiteSpace(activeDocumentPath)
                ? null
                : Path.GetDirectoryName(activeDocumentPath);
            return FindCommonDirectory(projectDirectory, documentDirectory) ?? projectDirectory;
        }
        catch (COMException)
        {
            // 로드 중인 비표준 프로젝트에서는 최근 파일 가중치만 사용합니다.
            return null;
        }
    }

    private static string? GetSolutionDirectory(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return string.IsNullOrWhiteSpace(dte.Solution.FullName)
                ? null
                : Path.GetDirectoryName(dte.Solution.FullName);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static IReadOnlyCollection<string> GetOpenDocumentPaths(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (EnvDTE.Document document in dte.Documents)
            {
                if (!string.IsNullOrWhiteSpace(document.FullName))
                {
                    paths.Add(Path.GetFullPath(document.FullName));
                }
            }
        }
        catch (COMException)
        {
            // 일부 문서 공급자는 로드 중 컬렉션 열거를 지원하지 않을 수 있습니다.
        }

        return paths;
    }

    private static string? FindCommonDirectory(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return null;
        }

        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = new DirectoryInfo(first!); current is not null; current = current.Parent)
        {
            ancestors.Add(current.FullName);
        }

        for (var current = new DirectoryInfo(second!); current is not null; current = current.Parent)
        {
            if (ancestors.Contains(current.FullName))
            {
                // 드라이브 루트는 프로젝트 범위로 의미가 없으며 거의 모든 후보를 같은 값으로 올립니다.
                return current.Parent is null ? null : current.FullName;
            }
        }

        return null;
    }

    private async Task ShowStatusAsync(string message)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var statusBar = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        statusBar?.SetText(message);
    }
}
