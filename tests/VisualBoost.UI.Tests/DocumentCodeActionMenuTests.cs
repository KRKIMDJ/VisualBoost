using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisualBoost.CodeGeneration;
using VisualBoost.Core.CodeGeneration;

internal static class DocumentCodeActionMenuTests
{
    public static void Run(string output)
    {
        var context = new GenerationModelContext { Function = new("Reset", "Widget", 0, 0, 10, false) };
        var actions = GenerationCodeActions.Create(context, "Widget.cpp");
        Check(actions.Count == 1 && actions[0].Title == "정의 생성", "선언에서는 정의 생성만 노출");
        context.ExistingPath = "Widget.cpp";
        var empty = GenerationCodeActions.Create(context, "Widget.cpp");
        Check(empty.Count == 0, "대응 코드 존재 시 노출할 도구 없음");
        context.ExistingPath = ""; context.Function = new("Reset", "Widget", 0, 0, 10, true);
        var declaration = GenerationCodeActions.Create(context, "Widget.h");
        Check(declaration.Count == 1 && declaration[0].Title == "선언 생성" && declaration[0].Children.Select(c => c.Id).SequenceEqual(new[] { "public", "protected", "private" }), "정의에서는 선언 생성과 접근 수준 하위 선택");
        var anchor = new Border { Width = 700, Height = 300, Focusable = true };
        var host = new Window { Content = anchor, Width = 720, Height = 340, Left = -20000, ShowInTaskbar = false };
        host.Show(); host.UpdateLayout();
        try
        {
            var none = new DocumentCodeActionMenu();
            Check(none.ShowAsync(anchor, new(70, 30), empty, CancellationToken.None).GetAwaiter().GetResult() is null && !none.Menu.IsOpen, "빈 메뉴는 표시하지 않음");
            for (var i = 0; i < 15; i++)
            {
                var menu = new DocumentCodeActionMenu();
                menu.Menu.Resources[SystemColors.WindowBrushKey] = new SolidColorBrush(Color.FromRgb(37, 37, 41));
                menu.Menu.Resources[SystemColors.WindowTextBrushKey] = Brushes.WhiteSmoke;
                var task = menu.ShowAsync(anchor, new(70, 30), actions, CancellationToken.None);
                Pump();
                Check(menu.Menu.IsOpen && menu.Menu.HorizontalOffset == 70 && menu.Menu.VerticalOffset == 30, "마우스가 아닌 전달된 커서 좌표 사용");
                var firstItem = (MenuItem)menu.Menu.Items[0];
                var header = (FrameworkElement)firstItem.Template.FindName("Header", firstItem);
                Check(header.TranslatePoint(new Point(), menu.Menu).X < 20 && firstItem.Template.FindName("Icon", firstItem) is null, "아이콘·체크 표시 열 없이 텍스트 시작");
                if (i == 0)
                {
                    menu.Menu.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(Math.Max(1, (int)menu.Menu.ActualWidth), Math.Max(1, (int)menu.Menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(menu.Menu);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, "DocumentCodeActionMenu.png")); encoder.Save(file);
                }
                if (i % 2 == 0)
                {
                    ((MenuItem)menu.Menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    WaitFor(task);
                    Check(task.GetAwaiter().GetResult()?.Id == "definition", "선택한 도구 반환");
                }
                else { menu.Menu.IsOpen = false; WaitFor(task); Check(task.GetAwaiter().GetResult() is null, "닫기는 실행 없이 취소"); }
                Check(menu.Menu.PlacementTarget is null && menu.Menu.Items.Count == 0, "닫을 때 참조 해제 및 다음 명령 허용");
            }
            var sub = new DocumentCodeActionMenu();
            var selection = sub.ShowAsync(anchor, new(70, 30), declaration, CancellationToken.None); Pump();
            var parentItem = (MenuItem)sub.Menu.Items[0];
            parentItem.Focus();
            parentItem.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(parentItem), Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
            Pump(); Check(parentItem.IsSubmenuOpen, "텍스트 전용 템플릿에서 오른쪽 키로 하위 메뉴 열기");
            var childItem = (MenuItem)parentItem.Items[1];
            childItem.ApplyTemplate();
            Check(childItem.Template.FindName("Header", childItem) is FrameworkElement && childItem.Template.FindName("Icon", childItem) is null, "하위 도구에도 아이콘 슬롯 없음");
            childItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            WaitFor(selection);
            Check(selection.GetAwaiter().GetResult()?.Id == "protected", "파일 선택 창 없이 선언 접근 수준 반환");
            var escapeMenu = new DocumentCodeActionMenu();
            var escaped = escapeMenu.ShowAsync(anchor, new(70, 30), actions, CancellationToken.None); Pump();
            escapeMenu.Menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(escapeMenu.Menu), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            WaitFor(escaped); Check(escaped.Result is null, "Esc 키로 메뉴만 닫고 작업 미실행");
            using var cancellation = new CancellationTokenSource();
            var cancelMenu = new DocumentCodeActionMenu(); var canceled = cancelMenu.ShowAsync(anchor, new(), actions, cancellation.Token);
            cancellation.Cancel(); WaitFor(canceled);
            Check(canceled.IsCompleted && canceled.Result is null && !cancelMenu.Menu.IsOpen, "문서 변경·종료 취소 신호로 메뉴 닫기");
        }
        finally { host.Close(); }
        Console.WriteLine("PASS: 문맥별 도구·빈 메뉴 억제·커서 위치·15회 선택/취소·접근 수준·취소 수명 실제 WPF 검증");
    }
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void WaitFor(Task task)
    {
        var frame = new DispatcherFrame(); var start = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow - start > TimeSpan.FromSeconds(3)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Check(task.IsCompleted, "메뉴 종료 후 명령 수명 해제 시간 제한");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
