using System;
using System.Windows;

namespace VisualBoost.UI;

/// <summary>
/// 검색 창의 크기·위치를 다시 열 때 되살리는 규칙입니다. 모니터 구성이 바뀌어 화면 밖에 열리는 일을 막으려고
/// 저장 위치가 현재 가상 화면과 겹칠 때만 위치를 쓰고, 크기는 창의 최소 크기 이상일 때만 씁니다.
/// </summary>
internal static class WindowPlacement
{
    /// <summary>저장한 경계 중 지금 쓸 수 있는 값만 고릅니다. 쓰지 않는 값은 null입니다.</summary>
    public static (double? Width, double? Height, Point? Position) Resolve(Rect? saved, Size minimum, Rect virtualScreen)
    {
        if (saved is not { } bounds) return (null, null, null);
        double? width = IsFinite(bounds.Width) && bounds.Width >= minimum.Width ? bounds.Width : null;
        double? height = IsFinite(bounds.Height) && bounds.Height >= minimum.Height ? bounds.Height : null;
        if (!IsFinite(bounds.Left) || !IsFinite(bounds.Top)) return (width, height, null);

        // 겹침 판정은 실제로 열릴 크기로 합니다. 크기를 버리면 위치도 기본 크기 기준으로 다시 판단합니다.
        var opened = new Rect(bounds.Left, bounds.Top, width ?? minimum.Width, height ?? minimum.Height);
        return (width, height, opened.IntersectsWith(virtualScreen) ? bounds.TopLeft : null);
    }

    /// <summary>창을 보이기 전에 저장한 크기·위치를 적용합니다.</summary>
    public static void Apply(Window window, Rect? saved)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var (width, height, position) = Resolve(saved, new Size(window.MinWidth, window.MinHeight), virtualScreen);
        if (width is { } w) window.Width = w;
        if (height is { } h) window.Height = h;
        if (position is not { } point) return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = point.X;
        window.Top = point.Y;
    }

    /// <summary>닫히는 창의 보통 상태 경계입니다. 최대화·최소화 중이면 복원 경계를 씁니다. 쓸 수 없으면 null입니다.</summary>
    public static Rect? Capture(Window window)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        return !bounds.IsEmpty && IsFinite(bounds.Left) && IsFinite(bounds.Top) &&
               IsFinite(bounds.Width) && bounds.Width >= window.MinWidth &&
               IsFinite(bounds.Height) && bounds.Height >= window.MinHeight
            ? bounds
            : null;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
