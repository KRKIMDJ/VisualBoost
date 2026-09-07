using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VisualBoost.CommentLinks;

internal static class CommentLinkTooltipTests
{
    public static void Run()
    {
        var anchor = new Border { Width = 400, Height = 200, ToolTip = "기존 편집기 안내" };
        var window = new Window { Content = anchor, Width = 420, Height = 240, Left = -18000, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        using var tooltip = new CommentLinkTooltip(anchor);
        try
        {
            tooltip.Update("10:9", new Point(20, 30));
            Check(!tooltip.Tip.IsOpen, "즉시 표시 대신 호버 대기");
            Wait(() => tooltip.Tip.IsOpen);
            Check((string)tooltip.Tip.Content == "Ctrl+클릭으로 이동" && tooltip.Tip.PlacementTarget == anchor, "Ctrl을 누르지 않은 일반 호버 안내");
            Check((string)anchor.ToolTip == "기존 편집기 안내", "편집기 기존 툴팁 보존");
            tooltip.Update(null, new Point()); Check(!tooltip.Tip.IsOpen && tooltip.Tip.PlacementTarget is null, "링크 이탈 시 닫기");
            tooltip.Update("20:9", new Point()); tooltip.Hide();
            PumpFor(400); Check(!tooltip.Tip.IsOpen, "편집·옵션·포커스 전환 후 지연 표시 취소");
            tooltip.Update("30:9", new Point()); tooltip.Dispose();
            PumpFor(400); Check(!tooltip.Tip.IsOpen && tooltip.Tip.PlacementTarget is null, "문서 종료 후 툴팁 재개방 없음");
            Console.WriteLine("PASS: 주석 링크 호버 안내·대기·기존 툴팁 보존·이탈·취소·종료 실제 WPF 검증");
        }
        finally { window.Close(); }
    }
    private static void Wait(Func<bool> done) { var watch = Stopwatch.StartNew(); while (!done()) { PumpFor(10); if (watch.ElapsedMilliseconds > 2000) throw new Exception("호버 안내 대기 시간 초과"); } }
    private static void PumpFor(int ms)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) }; var frame = new DispatcherFrame();
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
