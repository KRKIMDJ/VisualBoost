using Microsoft.VisualStudio.Shell.Interop;
using EnvDTE80;

namespace VisualBoost.Services;

internal interface ISymbolNavigationProvider
{
    bool CanHandle(string? filePath);

    bool TryNavigateToDefinition(DTE2 dte, IVsUIShell uiShell);
}
