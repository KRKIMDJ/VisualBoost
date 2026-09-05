using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using VisualBoost.UI;
using VisualBoost.Coloring;
using VisualBoost.Core.Analysis;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args[0]);
            var output = Path.Combine(root, "artifacts", "ui-validation");
            Directory.CreateDirectory(output);
            ColoringSettings.Publish(new ColoringSettings(true, new string[8]));
            foreach (var name in new[] { "FileSearchDialog", "SymbolSearchDialog", "SymbolUsagesControl" })
                Validate(root, output, name);
            UsageLifecycleTests.Run();
            ColorFormatTests.Run();
            PaletteAndMenuTests.Run(root);
            SharedColorTests.Run();
            Console.WriteLine("PASS: 실제 검색 XAML의 행 표시, 비율 조절, 창 크기 변경, 재개방 검증");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Validate(string root, string output, string name)
    {
        // VS 호스트 없이 테마·아이콘과 이벤트 연결만 치환하고 실제 행·열 템플릿은 그대로 검증합니다.
        var xml = XDocument.Load(Path.Combine(root, "src", "VisualBoost.Package", "UI", name + ".xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace ui = "clr-namespace:VisualBoost.UI;assembly=VisualBoost.UI.Tests";
        var events = new HashSet<string> { "PreviewKeyDown", "TextChanged", "Click", "GotKeyboardFocus",
            "LostKeyboardFocus", "PreviewMouseRightButtonDown", "ContextMenuOpening", "MouseDoubleClick", "SelectionChanged" };
        foreach (var node in xml.Descendants().ToArray())
        {
            if (node.Name.LocalName == "DialogWindow") node.Name = wpf + "Window";
            if (node.Name.LocalName == "DialogWindow.Resources") node.Name = wpf + "Window.Resources";
            if (node.Name.LocalName == "FittedResultsList") node.Name = ui + "FittedResultsList";
            if (node.Name.NamespaceName.StartsWith("clr-namespace:VisualBoost.UI", StringComparison.Ordinal)) node.Name = ui + node.Name.LocalName;
            if (node.Name.LocalName == "CrispImage")
            {
                node.Name = wpf + "Border";
                node.Attribute("Moniker")?.Remove();
            }
            foreach (var attribute in node.Attributes().ToArray())
            {
                if (attribute.Name.NamespaceName.StartsWith("clr-namespace:VisualBoost.UI", StringComparison.Ordinal))
                {
                    node.Add(new XAttribute(ui + attribute.Name.LocalName, attribute.Value));
                    attribute.Remove();
                    continue;
                }
                if (attribute.Name == x + "Class" || events.Contains(attribute.Name.LocalName)) attribute.Remove();
                else if (attribute.IsNamespaceDeclaration && attribute.Name.LocalName == "ui") attribute.Value = ui.NamespaceName;
                else if (attribute.Value.Contains("DynamicResource {x:Static vsshell:VsBrushes."))
                    attribute.Value = attribute.Value.Contains("HighlightText") ? "#FFFFFF"
                        : attribute.Value.Contains("Highlight") ? "#6154CB"
                        : attribute.Value.Contains("TextKey") ? "#E5E5E5"
                        : attribute.Value.Contains("Border") ? "#454545"
                        : attribute.Value.Contains("Heading") ? "#303036"
                        : attribute.Value.Contains("CommandBar") ? "#303036"
                        : attribute.Value.Contains("SearchBox") ? "#38383F" : "#252529";
            }
        }
        var element = (FrameworkElement)XamlReader.Parse(xml.ToString());
        var window = element as Window ?? new Window { Content = element, Width = 1100, Height = 400 };
        var defaultWidth = window.Width;
        window.Left = -20000;
        window.ShowInTaskbar = false;
        if (element.FindName("EmptyStatePanel") is FrameworkElement empty) empty.Visibility = Visibility.Collapsed;
        if (element.FindName("SearchBox") is TextBox box) box.Text = "SetMovementMode";
        if (element.FindName("SearchPlaceholder") is FrameworkElement placeholder) placeholder.Visibility = Visibility.Collapsed;
        if (element.FindName("SymbolText") is TextBlock symbol) symbol.Text = "SetMovementMode";
        if (element.FindName("StatusText") is TextBlock status) status.Text = "30개 결과 · 준비됨";
        if (element.FindName("ScopeSelector") is ComboBox scope) scope.SelectedIndex = 0;
        var items = (ItemsControl)element.FindName("ResultsList");
        items.ItemsSource = Enumerable.Range(1, 30).Select(i => new Row(i)).ToArray();
        window.Show();
        Pump();
        if (items is FittedResultsList list)
        {
            var grid = (GridView)list.View;
            Assert(Descendants<GridViewRowPresenter>(list).Any(), name + " 행 템플릿 누락");
            Assert(Descendants<TextBlock>(list).Any(t => t.Text == "SetMovementMode" || t.Text == "CharacterMovementComponent.cpp"), name + " 심볼/파일 바인딩 누락");
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var before = grid.Columns.Sum(c => c.Width);
                grid.Columns[iteration % grid.Columns.Count].Width += iteration % 2 == 0 ? 50 : -30;
                Pump();
                Assert(Math.Abs(before - grid.Columns.Sum(c => c.Width)) < 1, "열 합계 보존 실패");
                Assert(grid.Columns.All(c => c.Width > 0), "열 너비 음수");
                window.Width = 850 + iteration * 15;
                Pump();
                var scroll = Descendants<ScrollViewer>(list).First();
                Assert(Math.Abs(scroll.ViewportWidth - grid.Columns.Sum(c => c.Width)) < 1, "뷰포트 채움 실패");
            }
            for (var iteration = 0; iteration < 5; iteration++)
            {
                window.Hide();
                window.Show();
                Pump();
            }
            list.ResetColumnWidths();
            window.Width = defaultWidth;
            list.SelectedIndex = 2;
        }
        Pump();
        if (name == "SymbolSearchDialog")
        {
            var symbolItems = Descendants<ListViewItem>(items).ToArray();
            var unselectedText = Descendants<TextBlock>(symbolItems.First(item => !item.IsSelected)).First(text => text.Text == "SetMovementMode");
            var selectedText = Descendants<TextBlock>(symbolItems.First(item => item.IsSelected)).First(text => text.Text == "SetMovementMode");
            Assert(((SolidColorBrush)unselectedText.Foreground).Color == Color.FromRgb(0xF2, 0xCB, 0x8D), "심볼 팔레트 적용 실패");
            Assert(((SolidColorBrush)selectedText.Foreground).Color == Colors.White, "선택 행 색상 우선순위 실패");
            ColoringSettings.Publish(new ColoringSettings(true, new[] { "", "", "#11AA66", "", "", "", "", "" }));
            Pump();
            Assert(((SolidColorBrush)unselectedText.Foreground).Color == Color.FromRgb(0x11, 0xAA, 0x66), "열린 창 색상 변경 반영 실패");
            ColoringSettings.Publish(new ColoringSettings(true, new string[8]));
            Pump();
        }
        var surface = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
        image.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(stream);
        window.Close();
        Pump();
        var closedRevision = SearchPalette.GetRevision(element);
        ColoringSettings.Publish(new ColoringSettings(true, new string[8]));
        Pump();
        Assert(SearchPalette.GetRevision(element) == closedRevision, "닫은 창의 팔레트 이벤트 해제 실패");
        Console.WriteLine("PASS: " + name);
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    public sealed class Row
    {
        public Row(int line) { Line = line; }
        public string Name => "SetMovementMode";
        public SourceSymbolLocation Location => new(Name, FullPath, Line, 1, SourceSymbolKind.Function);
        public string FileName => "CharacterMovementComponent.cpp";
        public string ProjectName => "Engine";
        public string DirectoryPath => "Engine/Source/Runtime/Engine/Private/Components";
        public string FullPath => DirectoryPath + "/" + FileName;
        public int Line { get; }
        public Visibility IconVisibility => Visibility.Collapsed;
        public Visibility FallbackIconVisibility => Visibility.Visible;
        public string IconText => "C++";
        public object[] FileNameSegments => new object[] { new { Text = FileName, IsMatch = true } };
        public object[] PathSegments => new object[] { new { Text = DirectoryPath, IsMatch = false } };
        public object[] CodeSegments => new object[] { new { Text = "Movement->", IsMatch = false }, new { Text = Name, IsMatch = true }, new { Text = "(MOVE_Walking);", IsMatch = false } };
    }
}
