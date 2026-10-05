using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Core.SemanticNavigation;
using VisualBoost.SemanticNavigation;

namespace VisualBoost.UI;

/// <summary>
/// 참조 찾기 결과를 보이는 도킹 창입니다. 한 개만 만들며, 창을 닫아도 Visual Studio를 끝내기 전까지 최근 결과가 남습니다.
/// </summary>
[Guid(GuidString)]
public sealed class ReferencesToolWindow : ToolWindowPane
{
    public const string GuidString = "b1029515-8a54-4f8f-a72d-3f8b991afa90";

    private readonly ReferencesControl control;

    public ReferencesToolWindow()
        : base(null)
    {
        Caption = "VisualBoost 참조";
        control = new ReferencesControl { OpenLocation = Open };
        Content = control;
    }

    internal ReferencesControl Control => control;

    private void Open(NavigationLocation location, bool activate)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // VS가 창 배치를 복원하며 만든 경우에도 쓸 수 있게 전역 서비스 공급자로 문서를 엽니다.
        NavigationLocationOpener.Open(ServiceProvider.GlobalProvider, location, activate);
        if (activate) return;
        // 미리보기는 문서를 보이기만 하고, 계속 이 창에서 결과를 훑을 수 있게 초점을 되돌립니다.
        (Frame as IVsWindowFrame)?.Show();
        control.FocusSelection();
    }
}
