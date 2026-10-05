using System;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Core.SemanticNavigation;

namespace VisualBoost.SemanticNavigation;

/// <summary>정의·참조 결과 위치를 편집기에서 엽니다.</summary>
internal static class NavigationLocationOpener
{
    /// <param name="activate">true이면 편집기로 초점을 옮깁니다. false이면 문서를 보이기만 하고 초점은 옮기지 않습니다.</param>
    public static void Open(IServiceProvider provider, NavigationLocation location, bool activate)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.OpenDocument(provider, location.Path, Guid.Empty, out _, out _, out var frame, out var textView);
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
