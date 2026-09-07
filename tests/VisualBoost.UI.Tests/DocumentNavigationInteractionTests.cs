using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using VisualBoost.Core.DocumentNavigation;
using VisualBoost.DocumentNavigation;
using VisualBoost.UI;

namespace VisualBoost.DocumentNavigation
{
    // 실제 XAML에서 호스트 리소스만 치환하고 이벤트는 실제 코드에 다시 연결합니다.
    public partial class DocumentNavigationControl
    {
        internal static string TestRoot = "";
        internal Border PopupSurface = null!;
        internal TextBlock OrderLabel = null!, Status = null!;
        internal TextBox Search = null!;
        internal FittedResultsList Results = null!;
        private void InitializeComponent()
        {
            var xml = XDocument.Load(Path.Combine(TestRoot, "src", "VisualBoost.Package", "DocumentNavigation", "DocumentNavigationControl.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var handlers = new[] { "Click", "Opened", "Closed", "PreviewKeyDown", "TextChanged", "MouseDoubleClick" };
            foreach (var node in xml.Descendants())
            foreach (var attr in node.Attributes().ToArray())
            {
                if (attr.Name == x + "Class" || handlers.Contains(attr.Name.LocalName)) attr.Remove();
                else if (attr.Value.Contains("DynamicResource {x:Static vs:VsBrushes."))
                    attr.Value = attr.Value.Contains("HighlightTextKey") ? "#FFFFFF" : attr.Value.Contains("HighlightKey") ? "#6154CB"
                        : attr.Value.Contains("TextKey") ? "#E5E5E5" : attr.Value.Contains("BorderKey") ? "#45454B"
                        : attr.Value.Contains("Heading") || attr.Value.Contains("SearchBox") ? "#35353C" : "#26262B";
            }
            var loaded = (Popup)XamlReader.Parse(xml.ToString().Replace("clr-namespace:VisualBoost.UI", "clr-namespace:VisualBoost.UI;assembly=VisualBoost.UI.Tests"));
            PopupSurface = (Border)loaded.FindName("PopupSurface");
            OrderLabel = (TextBlock)loaded.FindName("OrderLabel");
            Status = (TextBlock)loaded.FindName("Status"); Search = (TextBox)loaded.FindName("Search");
            Results = (FittedResultsList)loaded.FindName("Results");
            Resources = loaded.Resources;
            NameScope.SetNameScope(this, NameScope.GetNameScope(loaded));
            loaded.Child = null;
            Child = PopupSurface;
            StaysOpen = false; AllowsTransparency = true;
            Menu.Opened += OnOpened; Menu.Closed += OnClosed;
            PopupSurface.PreviewKeyDown += OnPopupKeyDown; Search.TextChanged += OnSearchChanged; Results.MouseDoubleClick += OnDoubleClick;
            Results.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnExpanderClick));
        }
    }
}

internal static class DocumentNavigationInteractionTests
{
    public static void Run(string root)
    {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        SharedBufferLifetime();
        DocumentNavigationControl.TestRoot = root;
        using var control = new DocumentNavigationControl();
        var provider = new CppDocumentMemberProvider();
        var source = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"void UpdateMovementMode{i}(int count) {{ if (count) {{ Call(); }} }}"));
        var snapshot = provider.Analyze(source);
        var editor = new Border { Background = Brushes.DimGray, Height = 380, Focusable = true };
        control.EditorAnchor = editor;
        // 편집기 표면에 탐색 UI를 붙이지 않아도 명령의 Popup만 열려야 합니다.
        var window = new Window { Content = editor, Width = 1020, Height = 500, Left = -16000, Top = 30, ShowActivated = false };
        window.Show(); window.UpdateLayout();
        try
        {
            control.SetDocument(snapshot, 10, "30개 함수");
            control.SetCaret(snapshot.Members[12].NameOffset);
            control.Open();
            Until(() => control.Results.Items.Count == 30);
            Check(PresentationSource.FromVisual(control) is null && control.Menu.PlacementTarget == editor, "상단 바 없이 독립 Popup 열기");
            Check(((DocumentMemberRow)control.Results.SelectedItem).Name == "UpdateMovementMode12", "현재 함수 초기 선택");
            long receivedVersion = -1; DocumentMember? navigated = null;
            control.Navigate += (member, version) => { navigated = member; receivedVersion = version; };
            control.Search.Text = "Upd Move 2";
            Until(() => control.Results.Items.Count > 0 && control.Results.Items.Count < 30);
            Check(navigated is null, "검색·선택만으로 편집 위치 변경 금지");
            control.PopupSurface.UpdateLayout();
            var columns = ((GridView)control.Results.View).Columns;
            var scroll = Descendants(control.Results).OfType<ScrollViewer>().First();
            Check(Math.Abs(columns.Sum(c => c.Width) - scroll.ViewportWidth) < 2, "Popup 열 너비 합계를 뷰포트에 맞춤");
            var blocks = Descendants(control.Results).OfType<TextBlock>().ToArray();
            Check(blocks.Any(b => b.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.FontWeight == FontWeights.Bold && r.TextDecorations.Count > 0)), "일치 문자 굵기·밑줄");
            Check(!blocks.Any(b => b.Text.Contains("DocumentMemberRow")), "실제 행 템플릿 표시");
            Render(control.PopupSurface, Path.Combine(root, "artifacts", "ui-validation", "DocumentMembers.png"));
            SendKey(control.PopupSurface, Key.Enter);
            Check(navigated is not null && receivedVersion == 10 && !control.Menu.IsOpen, "Enter 선택 버전 전달·닫기");
            control.Open(); Until(() => control.Results.Items.Count == 30);
            control.SetDocument(null, 11, "분석 갱신 중…");
            Check(control.Status.Text == "분석 갱신 중…" && control.Results.Items.Count == 0, "변경 직후 이전 이동 후보 제거");
            var next = provider.Analyze("void NewlyAdded() {}");
            control.SetDocument(next, 11, "1개 함수");
            Until(() => control.Results.Items.Count == 1);
            Check(((DocumentMemberRow)control.Results.Items[0]).Name == "NewlyAdded", "미저장 변경 결과 적용");
            var nested = provider.Analyze("namespace Gameplay { class Character { void Update(); struct Movement { void Walk(); void Jump(); }; void Stop(); }; }");
            control.SetDocument(nested, 12, "4개 함수"); control.SetCaret(nested.Members.First(m => m.Name == "Walk").NameOffset);
            Until(() => control.Results.Items.Count == 7);
            Check(((DocumentMemberRow)control.Results.SelectedItem).Name == "Walk", "트리에서 현재 함수 선택과 상위 펼침");
            var movement = control.Results.Items.Cast<DocumentMemberRow>().First(r => r.Name == "Movement");
            Check(movement.Depth == 2 && movement.Children[0].Depth == 3 && movement.Children[0].Indent.Left == 42, "실제 깊이별 들여쓰기");
            control.Results.UpdateLayout();
            var expander = Descendants(control.Results).OfType<Button>().First(b => b.DataContext == movement);
            expander.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(control.Results.Items.Count == 5 && control.Results.SelectedItem == movement, "버튼으로 그룹 접기와 선택 유지");
            control.Results.Focus(); SendKey(control.PopupSurface, Key.Right);
            Check(control.Results.Items.Count == 7, "오른쪽 키로 다시 펼치기");
            SendKey(control.PopupSurface, Key.Right);
            Check(control.Results.SelectedItem == movement.Children[0], "오른쪽 키로 첫 자식 선택");
            SendKey(control.PopupSurface, Key.Left);
            Check(control.Results.SelectedItem == movement, "왼쪽 키로 부모 선택");
            Render(control.PopupSurface, Path.Combine(root, "artifacts", "ui-validation", "DocumentMemberTree.png"));
            control.Search.Text = "Jump"; Until(() => control.Results.Items.Count == 4);
            Check(control.Results.Items.Cast<DocumentMemberRow>().Select(r => r.Name).SequenceEqual(new[] { "Gameplay", "Character", "Movement", "Jump" }), "검색 중 함수와 상위 경로만 표시");
            Check(((DocumentMemberRow)control.Results.SelectedItem).Name == "Jump", "그룹 대신 검색 함수 자동 선택");
            SendKey(control.PopupSurface, Key.Enter);
            Check(navigated?.Name == "Jump" && receivedVersion == 12 && !control.Menu.IsOpen, "트리 검색 후 Enter 한 번으로 함수 이동");
            control.Open(); Until(() => control.Results.Items.Count == 7);
            control.SetDocument(next, 13, "1개 함수"); control.Search.Text = "";
            Until(() => control.Results.Items.Count == 1);
            var returned = false; control.ReturnFocus += () => returned = true;
            SendKey(control.PopupSurface, Key.Escape);
            Check(returned && !control.Menu.IsOpen, "Escape 편집기 포커스 복귀 요청");
            control.Configure(true); window.UpdateLayout();
            control.Open(); Until(() => control.Results.Items.Count == 1);
            Check(control.OrderLabel.Text == "이름 순서" && control.Menu.PlacementTarget == editor, "이름 정렬 옵션 및 재개방");
            control.Search.Text = "NotFound";
            control.Dispose(); Pump();
            Check(!control.Menu.IsOpen && control.Results.Items.Count == 0, "종료 중 검색 결과 공개 방지");
            Console.WriteLine("PASS: 상단 바 없는 문서 함수 검색·현재 함수 선택·강조·버전·키보드·종료 실제 WPF 검증");
        }
        finally { window.Close(); }
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
        { var child = VisualTreeHelper.GetChild(value, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void SharedBufferLifetime()
    {
        var buffer = new Microsoft.VisualStudio.Text.TestBuffer();
        buffer.Set("void First() {}");
        var first = DocumentMemberBuffer.Attach(buffer, Dispatcher.CurrentDispatcher);
        var second = DocumentMemberBuffer.Attach(buffer, Dispatcher.CurrentDispatcher);
        Check(ReferenceEquals(first, second), "분할 뷰는 버퍼 분석 세션 공유");
        Until(() => first.Members is not null);
        Check(first.Members!.Members[0].Name == "First", "초기 버퍼 분석");
        for (var i = 0; i < 12; i++)
        {
            buffer.Set($"void New{i}() {{}}"); buffer.Type(' ');
            Check(first.Members is null, "편집 시 오래된 범위 즉시 제거");
        }
        first.Detach();
        Until(() => second.Members is not null);
        Check(second.Members!.Members[0].Name == "New11" && second.Snapshot == buffer.CurrentSnapshot, "한 뷰를 닫아도 최신 버전 분석 유지");
        var afterClose = 0;
        second.Changed += (_, _) => afterClose++;
        second.Detach();
        buffer.Type(' '); Pump();
        Check(afterClose == 0 && buffer.Properties.Value is null, "마지막 뷰 종료 시 이벤트·버퍼 속성 해제");
        var reopened = DocumentMemberBuffer.Attach(buffer, Dispatcher.CurrentDispatcher);
        Check(!ReferenceEquals(first, reopened), "다시 열린 문서는 새 세션");
        reopened.Detach();
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 230) { Pump(); System.Threading.Thread.Sleep(1); }
        Check(Microsoft.VisualStudio.Shell.ActivityLog.Errors.Count == 0, "분석·해제 중 비동기 오류 없음");
        Console.WriteLine("PASS: 실제 버퍼 분석 세션 공유·12회 변경 병합·분할 뷰 해제·재개방 (SDK 경계 대역)");
    }
    private static void Render(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var target = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        target.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void SendKey(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)!;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { Pump(); if (watch.ElapsedMilliseconds > 3000) throw new Exception("문서 함수 UI 대기 시간 초과"); System.Threading.Thread.Sleep(1); }
        Pump();
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
