using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Services;

namespace VisualBoost.Commands;

internal sealed class NavigateToDefinitionCommand
{
    private static readonly Guid CommandSet = new("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e");
    private readonly VisualBoostPackage package;
    private readonly ISymbolNavigationProvider provider = new VisualStudioSymbolNavigationProvider();

    private NavigateToDefinitionCommand(VisualBoostPackage package, OleMenuCommandService commandService)
    {
        this.package = package;
        commandService.AddCommand(new OleMenuCommand(
            Execute,
            new CommandID(CommandSet, CommandIds.NavigateToDefinition)));
    }

    public static async Task InitializeAsync(
        VisualBoostPackage package,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        Assumes.Present(commandService);
        _ = new NavigateToDefinitionCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        package.JoinableTaskFactory.RunAsync(ExecuteAsync).FileAndForget("VisualBoost/NavigateToDefinition");
    }

    private async Task ExecuteAsync()
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
        Assumes.Present(dte);
        var activeFile = dte.ActiveDocument?.FullName;
        var uiShell = await package.GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
        if (!provider.CanHandle(activeFile) || uiShell is null || !provider.TryNavigateToDefinition(uiShell))
        {
            var statusBar = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            statusBar?.SetText("현재 커서 위치에서 선언 또는 정의를 찾지 못했습니다.");
        }
    }
}
