using Microsoft.VisualStudio.Shell.Interop;

namespace VisualBoost.Services;

internal interface ISymbolNavigationProvider
{
    bool CanHandle(string? filePath);

    bool TryNavigateToDefinition(IVsUIShell uiShell);
}
