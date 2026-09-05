using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Core.Analysis;
using VisualBoost.Services;
using VisualBoost.UI;

namespace VisualBoost.Commands;

internal sealed class FindSymbolUsagesCommand
{
    private static readonly Guid CommandSet = new("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e");
    private readonly VisualBoostPackage package;
    private readonly SolutionFileIndexService fileIndex;
    private readonly ISymbolUsageProvider provider = new IndexedSymbolUsageProvider();

    private FindSymbolUsagesCommand(
        VisualBoostPackage package,
        SolutionFileIndexService fileIndex,
        OleMenuCommandService commandService)
    {
        this.package = package;
        this.fileIndex = fileIndex;
        commandService.AddCommand(new OleMenuCommand(
            Execute,
            new CommandID(CommandSet, CommandIds.FindSymbolUsages)));
    }

    public static async Task InitializeAsync(
        VisualBoostPackage package,
        SolutionFileIndexService fileIndex,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        Assumes.Present(commandService);
        _ = new FindSymbolUsagesCommand(package, fileIndex, commandService);
    }

    private void Execute(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SearchCommandRunner.Run(package, "FindSymbolUsages", ExecuteAsync);
    }

    private async Task ExecuteAsync()
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
        Assumes.Present(dte);
        if (!ActiveEditorSymbolReader.TryGetSymbol(dte, out var symbol))
        {
            var statusBar = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            statusBar?.SetText("현재 커서 위치에서 사용할 심볼을 확인할 수 없습니다.");
            return;
        }

        // 도구 창에 포커스가 넘어가기 전에 실행 문서의 소속을 캡처합니다.
        var sourceProject = dte.ActiveDocument?.ProjectItem?.ContainingProject;
        var sourceProjectFile = SearchPath.TryNormalize(sourceProject?.FullName, out var projectPath)
            ? projectPath : null;
        var sourceProjectName = sourceProject?.Name;
        var context = FileSearchContext.Collect(dte);
        var toolWindow = await package.ShowToolWindowAsync(
            typeof(SymbolUsagesToolWindow),
            0,
            create: true,
            package.DisposalToken) as SymbolUsagesToolWindow;
        if (toolWindow is null)
        {
            var statusBar = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            statusBar?.SetText("코드 검색 창을 열지 못했습니다.");
            return;
        }

        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        toolWindow.View.StartSearch(
            fileIndex,
            provider,
            symbol,
            sourceProjectFile,
            context.Projects,
            location => OpenLocation(dte, location),
            sourceProjectName);
    }

    private static void OpenLocation(DTE2 dte, SourceUsageLocation location)
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
}
