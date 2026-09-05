using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.UI;

[Guid("9d54b908-07a0-42ea-9e3e-0aa9dba01c65")]
public sealed class SymbolUsagesToolWindow : ToolWindowPane
{
    public SymbolUsagesToolWindow()
        : base(null)
    {
        Caption = "VisualBoost 코드 검색";
        Content = new SymbolUsagesControl();
    }

    internal SymbolUsagesControl View => (SymbolUsagesControl)Content;
}
