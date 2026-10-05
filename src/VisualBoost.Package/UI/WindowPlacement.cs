using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace VisualBoost.UI;

/// <summary>
/// 검색 창의 크기·위치를 다시 열 때 되살리는 규칙입니다. 모니터 구성이 바뀌어 창을 잡을 수 없는 곳에 열리는 일을
/// 막으려고, 저장 위치의 제목 표시줄 띠가 어느 모니터 작업 영역 안에 충분히 들어올 때만 위치를 쓰고,
/// 크기는 창의 최소 크기 이상일 때만 씁니다.
/// </summary>
internal static class WindowPlacement
{
    /// <summary>제목 표시줄로 보는 창 위쪽 띠의 높이(DIP)입니다.</summary>
    internal const double TitleBarHeight = 32;

    /// <summary>끌어서 옮길 수 있다고 볼 제목 표시줄의 최소 보이는 너비·높이(DIP)입니다.</summary>
    internal const double MinimumGrabWidth = 100;
    internal const double MinimumGrabHeight = 16;

    /// <summary>
    /// 저장한 경계 중 지금 쓸 수 있는 값만 고릅니다. 쓰지 않는 값은 null입니다. 작업 영역 외접 사각형이 아니라
    /// 모니터마다 판정하므로, ㄱ자 배치의 빈 영역이나 화면 위쪽 밖으로 제목 표시줄이 나간 위치는 버립니다.
    /// </summary>
    public static (double? Width, double? Height, Point? Position) Resolve(Rect? saved, Size minimum, IReadOnlyList<Rect> workAreas)
    {
        if (saved is not { } bounds) return (null, null, null);
        double? width = IsFinite(bounds.Width) && bounds.Width >= minimum.Width ? bounds.Width : null;
        double? height = IsFinite(bounds.Height) && bounds.Height >= minimum.Height ? bounds.Height : null;
        if (!IsFinite(bounds.Left) || !IsFinite(bounds.Top)) return (width, height, null);

        // 판정은 실제로 열릴 너비로 합니다. 크기를 버리면 기본(최소) 너비 기준으로 다시 판단합니다.
        var openedWidth = width ?? minimum.Width;
        var titleBar = new Rect(bounds.Left, bounds.Top, Math.Max(1, openedWidth), TitleBarHeight);
        var grabWidth = Math.Min(MinimumGrabWidth, titleBar.Width);
        var reachable = workAreas.Any(area =>
        {
            var visible = Rect.Intersect(titleBar, area);
            return !visible.IsEmpty && visible.Width >= grabWidth && visible.Height >= MinimumGrabHeight;
        });
        return (width, height, reachable ? bounds.TopLeft : null);
    }

    /// <summary>창을 보이기 전에 저장한 크기·위치를 적용합니다.</summary>
    public static void Apply(Window window, Rect? saved)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));
        var (width, height, position) = Resolve(saved, new Size(window.MinWidth, window.MinHeight), WorkAreas());
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

    /// <summary>
    /// 모니터별 작업 영역(작업 표시줄 제외)을 WPF 좌표(DIP)로 돌려줍니다. 화면 좌표는 물리 픽셀이므로 주 모니터의
    /// 픽셀/DIP 비율로 환산합니다. 모니터마다 배율이 다르면 근사이며, 화면 정보를 얻지 못하면 가상 화면 전체를 씁니다.
    /// </summary>
    private static IReadOnlyList<Rect> WorkAreas()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        if (screens.Length == 0 || primary is null || SystemParameters.PrimaryScreenWidth <= 0)
        {
            return new[]
            {
                new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight),
            };
        }

        var scale = primary.Bounds.Width / SystemParameters.PrimaryScreenWidth;
        if (!IsFinite(scale) || scale <= 0) scale = 1;
        return screens
            .Select(screen => screen.WorkingArea)
            .Select(area => new Rect(area.Left / scale, area.Top / scale, area.Width / scale, area.Height / scale))
            .ToArray();
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
