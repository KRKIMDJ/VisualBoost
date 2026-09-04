using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Services;

namespace VisualBoost.Commands;

internal sealed class ShowIndexStatusCommand
{
    private static readonly Guid CommandSet = new("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e");

    private readonly VisualBoostPackage package;
    private readonly SolutionFileIndexService fileIndex;

    private ShowIndexStatusCommand(
        VisualBoostPackage package,
        SolutionFileIndexService fileIndex,
        OleMenuCommandService commandService)
    {
        this.package = package;
        this.fileIndex = fileIndex;
        var commandId = new CommandID(CommandSet, CommandIds.ShowIndexStatus);
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
        _ = new ShowIndexStatusCommand(package, fileIndex, commandService);
    }

    private void Execute(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var snapshot = fileIndex.GetSnapshot();
        var duration = snapshot.LastBuildDuration == TimeSpan.Zero
            ? "기록 없음"
            : $"{snapshot.LastBuildDuration.TotalMilliseconds:N0} ms";
        var message =
            $"상태: {GetStateText(snapshot.State)}\n" +
            $"파일: {snapshot.FileCount:N0}개\n" +
            $"검색 루트: {snapshot.RootCount:N0}개\n" +
            $"심볼 후보: {snapshot.SymbolCount:N0}개\n" +
            $"include 연결: {snapshot.IncludeEdgeCount:N0}개\n" +
            $"소스 분석: {(snapshot.IsAnalyzing ? "진행 중" : "대기/완료")}\n" +
            $"최근 인덱싱: {duration}";

        if (!string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            message += $"\n\n최근 오류: {snapshot.LastError}";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.AnalysisError))
        {
            message += $"\n\n최근 분석 오류: {snapshot.AnalysisError}";
        }

        VsShellUtilities.ShowMessageBox(
            package,
            message,
            "VisualBoost 인덱스 상태",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    private static string GetStateText(SolutionFileIndexState state) => state switch
    {
        SolutionFileIndexState.Empty => "Solution 없음",
        SolutionFileIndexState.Building => "인덱싱 중",
        SolutionFileIndexState.Ready => "준비됨",
        SolutionFileIndexState.Faulted => "실패",
        _ => "알 수 없음",
    };
}
