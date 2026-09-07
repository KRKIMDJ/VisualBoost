using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace VisualBoost.CommentLinks;

// 편집기 전체의 ToolTip 값을 바꾸지 않아 기본 Quick Info와 다른 확장의 설정을 보존합니다.
internal sealed class CommentLinkTooltip : IDisposable
{
    private readonly FrameworkElement anchor;
    private readonly DispatcherTimer delay = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
    private string? key;
    private Point position;
    private bool disposed;
    internal ToolTip Tip { get; } = new() { Content = "Ctrl+클릭으로 이동", Placement = PlacementMode.RelativePoint, StaysOpen = true, IsHitTestVisible = false };
    public CommentLinkTooltip(FrameworkElement anchor) { this.anchor = anchor; delay.Tick += OnDelay; }
    public void Update(string? linkKey, Point location)
    {
        if (disposed) return;
        if (linkKey is null) { Hide(); return; }
        position = location;
        if (key == linkKey) return;
        Hide(); key = linkKey; delay.Start();
    }
    private void OnDelay(object? sender, EventArgs e)
    {
        delay.Stop();
        if (disposed || key is null || !anchor.IsVisible) return;
        Tip.PlacementTarget = anchor; Tip.HorizontalOffset = position.X + 12; Tip.VerticalOffset = position.Y + 20;
        Tip.IsOpen = true;
    }
    public void Hide() { delay.Stop(); key = null; Tip.IsOpen = false; Tip.PlacementTarget = null; }
    public void Dispose() { if (disposed) return; disposed = true; Hide(); delay.Tick -= OnDelay; }
}
