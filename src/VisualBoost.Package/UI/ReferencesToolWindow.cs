using System;
using System.IO;
using System.Media;
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
    public const string Title = "VisualBoost 참조";

    private readonly ReferencesControl control;

    public ReferencesToolWindow()
        : base(null)
    {
        // VS는 도구 창을 UI thread에서 만듭니다.
        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = Title;
        EditorCodeStyleSource.EnsureStarted();
        control = new ReferencesControl { OpenLocation = Open };
        Content = control;
    }

    internal ReferencesControl Control => control;

    private void Open(NavigationLocation location, bool activate)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // 최근 결과에는 그 뒤에 지우거나 옮긴 파일이 있을 수 있습니다. 입력 처리기에서 부르므로 여기서 알리고 끝냅니다.
        if (!File.Exists(location.Path))
        {
            Notify("파일이 없습니다: " + location.Path);
            return;
        }

        try
        {
            // VS가 창 배치를 복원하며 만든 경우에도 쓸 수 있게 전역 서비스 공급자로 문서를 엽니다.
            NavigationLocationOpener.Open(ServiceProvider.GlobalProvider, location, activate);
        }
        catch (Exception exception) when (exception is COMException || exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException)
        {
            ActivityLog.LogWarning("VisualBoost/ReferencesWindow", exception.ToString());
            Notify("파일을 열지 못했습니다: " + exception.Message);
            return;
        }

        if (activate) return;
        // 미리보기는 문서를 보이기만 하고, 계속 이 창에서 결과를 훑을 수 있게 초점을 되돌립니다.
        (Frame as IVsWindowFrame)?.Show();
        control.FocusSelection();
    }

    private static void Notify(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SystemSounds.Beep.Play();
        (ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText("VisualBoost: " + message);
    }
}
