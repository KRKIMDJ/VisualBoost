using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Services;

internal sealed class VisualStudioSymbolNavigationProvider : ISymbolNavigationProvider
{
    public bool CanHandle(string? filePath) => !string.IsNullOrWhiteSpace(filePath);

    public bool TryNavigateToDefinition(DTE2 dte, IVsUIShell uiShell)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var direction = CppNavigationDirectionReader.Read(dte);
        if (direction == SymbolNavigationDirection.Declaration &&
            PostCommand(uiShell, VSConstants.VSStd97CmdID.GotoDecl))
        {
            return true;
        }

        return PostCommand(uiShell, VSConstants.VSStd97CmdID.GotoDefn);
    }

    private static bool PostCommand(IVsUIShell uiShell, VSConstants.VSStd97CmdID command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var commandGroup = VSConstants.GUID_VSStandardCommandSet97;
        // 자동화 계층의 문자열 명령은 버전에 따라 인수를 요구할 수 있으므로
        // 편집기가 처리하는 표준 OLE 명령을 큐에 직접 전달합니다.
        var result = uiShell.PostExecCommand(
            ref commandGroup,
            (uint)command,
            0,
            null);
        return ErrorHandler.Succeeded(result);
    }
}
