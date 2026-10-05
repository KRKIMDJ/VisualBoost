using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Core.SemanticNavigation;

namespace VisualBoost.SemanticNavigation;

/// <summary>정의·참조 결과 위치를 편집기에서 엽니다.</summary>
internal static class NavigationLocationOpener
{
    /// <param name="activate">
    /// true이면 편집기로 초점을 옮깁니다. false이면 미리 보기로, 임시 탭에 문서를 보이기만 하고 초점은 옮기지 않습니다.
    /// 결과를 훑는 동안 탭이 쌓이지 않게 하려는 것입니다.
    /// </param>
    public static void Open(IServiceProvider provider, NavigationLocation location, bool activate)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        IVsWindowFrame? frame;
        Microsoft.VisualStudio.TextManager.Interop.IVsTextView? textView;
        if (activate)
        {
            VsShellUtilities.OpenDocument(provider, location.Path, Guid.Empty, out _, out _, out frame, out textView);
        }
        else
        {
            using (new NewDocumentStateScope(__VSNEWDOCUMENTSTATE.NDS_Provisional, VSConstants.NewDocumentStateReason.Navigation))
            {
                VsShellUtilities.OpenDocument(provider, location.Path, Guid.Empty, out _, out _, out frame, out textView);
            }
        }

        if (activate) frame?.Show();
        else frame?.ShowNoActivate();
        if (textView is null) return;
        var line = location.Line;
        var column = location.Character;
        if (textView.GetBuffer(out var buffer) == 0 && buffer is not null)
        {
            // 파일이 바뀌어 위치가 범위를 벗어나도 가장 가까운 위치로 이동합니다.
            buffer.GetLineCount(out var lineCount);
            line = Math.Max(0, Math.Min(line, lineCount - 1));
            buffer.GetLengthOfLine(line, out var length);
            column = Math.Max(0, Math.Min(column, length));
        }

        textView.SetCaretPos(line, column);
        textView.CenterLines(line, 1);
        if (activate) textView.SendExplicitFocus();
    }
}
