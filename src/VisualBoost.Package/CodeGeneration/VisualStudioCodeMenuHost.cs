using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VisualBoost.CodeGeneration;

internal sealed class VisualStudioCodeMenuHost : INativeCodeMenuHost
{
    public async Task ShowAsync(Point screen, NativeCodeActionMenuTarget target, CancellationToken token)
    {
        var service = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsUIShell));
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
        var shell = service as IVsUIShell;
        if (shell is null) throw new InvalidOperationException("Visual Studio 컨텍스트 메뉴 서비스를 사용할 수 없습니다.");
        var group = NativeCodeActionMenuTarget.CommandSet;
        // POINTS는 signed 16비트 화면 좌표입니다. 넘치는 값은 래핑하지 않습니다.
        short Coordinate(double value) => (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(value)));
        var points = new[] { new POINTS { x = Coordinate(screen.X), y = Coordinate(screen.Y) } };
        ErrorHandler.ThrowOnFailure(shell.ShowContextMenu(0, ref group, NativeCodeActionMenuTarget.MenuId, points, target));
    }
}
