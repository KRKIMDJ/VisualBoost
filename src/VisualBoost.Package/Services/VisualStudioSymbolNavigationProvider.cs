using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VisualBoost.Services;

internal sealed class VisualStudioSymbolNavigationProvider : ISymbolNavigationProvider
{
    public bool CanHandle(string? filePath) => !string.IsNullOrWhiteSpace(filePath);

    public bool TryNavigateToDefinition(IVsUIShell uiShell)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var commandGroup = VSConstants.GUID_VSStandardCommandSet97;
        // 자동화 계층의 문자열 명령은 버전에 따라 인수를 요구할 수 있으므로
        // 편집기가 처리하는 표준 OLE 명령을 큐에 직접 전달합니다.
        var result = uiShell.PostExecCommand(
            ref commandGroup,
            (uint)VSConstants.VSStd97CmdID.GotoDefn,
            0,
            null);
        return ErrorHandler.Succeeded(result);
    }
}
