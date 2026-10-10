using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Services;

namespace VisualBoost.Commands;

/// <summary>
/// 인덱스 다시 만들기: 현재 Solution의 저장된 수집·분석 결과를 지우고 처음 열 때처럼 다시 만듭니다. 저장된 결과는 파일 크기·수정 시각으로만
/// 재사용을 판단하므로, 수정 시각을 보존한 복사처럼 이 판단을 비껴간 변경이 남았을 때 되돌리는 경로입니다.
/// </summary>
internal sealed class RebuildIndexCommand
{
    private static readonly Guid CommandSet = new("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e");

    private readonly VisualBoostPackage package;
    private readonly SolutionFileIndexService fileIndex;

    private RebuildIndexCommand(VisualBoostPackage package, SolutionFileIndexService fileIndex, OleMenuCommandService commandService)
    {
        this.package = package;
        this.fileIndex = fileIndex;
        var command = new OleMenuCommand(Execute, new CommandID(CommandSet, CommandIds.RebuildIndex));
        command.BeforeQueryStatus += OnBeforeQueryStatus;
        commandService.AddCommand(command);
    }

    public static async Task InitializeAsync(VisualBoostPackage package, SolutionFileIndexService fileIndex, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        Assumes.Present(commandService);
        _ = new RebuildIndexCommand(package, fileIndex, commandService);
    }

    private void OnBeforeQueryStatus(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // 열린 Solution이 없으면 지울 결과도 없습니다.
        if (sender is OleMenuCommand command) command.Enabled = fileIndex.GetSnapshot().State != SolutionFileIndexState.Empty;
    }

    private void Execute(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var answer = VsShellUtilities.ShowMessageBox(
            package,
            "현재 Solution의 저장된 파일 목록과 이름 분석을 지우고 처음부터 다시 만듭니다.\n" +
            "큰 Solution은 다시 분석하는 데 몇 분 걸리고, 그동안 심볼 탐색 결과가 줄어듭니다.\n\n계속할까요?",
            "VisualBoost 인덱스 다시 만들기",
            OLEMSGICON.OLEMSGICON_QUERY,
            OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
        if (answer != (int)VSConstants.MessageBoxResult.IDOK) return;
        package.JoinableTaskFactory.RunAsync(async () =>
        {
            if (await package.RebuildFileIndexAsync()) return;
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            VsShellUtilities.ShowMessageBox(
                package,
                "일부 저장 파일을 지우지 못했습니다. 다른 Visual Studio가 같은 Solution을 열고 있으면 그 창을 닫은 뒤 다시 실행하세요.",
                "VisualBoost 인덱스 다시 만들기",
                OLEMSGICON.OLEMSGICON_WARNING,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }).FileAndForget("VisualBoost/RebuildIndex");
    }
}
