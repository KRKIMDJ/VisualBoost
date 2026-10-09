using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace VisualBoost.UI;

/// <summary>검색 결과 우클릭 메뉴가 함께 쓰는 동작입니다. 결과는 창의 상태 줄에 보일 짧은 문구로 돌려줍니다.</summary>
internal static class ResultActions
{
    public static string Copy(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            return done;
        }
        catch (ExternalException)
        {
            // 다른 프로세스가 클립보드를 잡고 있는 일시적 상황입니다. 다시 시도하면 됩니다.
            return "클립보드를 사용할 수 없습니다.";
        }
    }

    /// <summary>파일 탐색기에서 파일을 선택한 채로 엽니다. 실패하면 상태 문구를, 성공하면 null을 돌려줍니다.</summary>
    public static string? ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                // 이름만 주면 현재 폴더(열린 저장소일 수 있음)를 Windows 폴더보다 먼저 찾으므로 Windows 폴더의 탐색기를 경로로 지정합니다.
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return "탐색기를 열 수 없습니다.";
        }
    }
}
