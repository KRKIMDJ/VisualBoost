using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using VisualBoost.Core.SemanticNavigation;
using VisualBoost.UI;

/// <summary>도킹 참조 창의 표시 모델과 실제 XAML 행 템플릿을 검증합니다.</summary>
internal static class ReferenceResultsTests
{
    public static void Run(string root, string output)
    {
        Model();
        History();
        InitialValuesDoNotRaiseHandlers(root);
        Xaml(root, output);
        Console.WriteLine("PASS: 참조 결과 창의 파일별 묶기·필터·접기·최근 결과와 행 템플릿");
    }

    private static void Model()
    {
        var set = Sample("FMod::Compute");
        var model = new ReferenceResultsModel();
        var changed = new List<string>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);
        model.Show(set);
        Assert(changed.Contains(nameof(ReferenceResultsModel.Rows)) && changed.Contains(nameof(ReferenceResultsModel.Summary)), "변경 알림");

        // 결과 순서(요청한 파일 먼저)를 지키며 파일 머리 행 뒤에 그 파일의 위치가 옵니다. 떨어져 있던 같은 파일 위치도 한데 묶습니다.
        Assert(Shape(model) == "F:Use.cpp(3) L:3 L:8 L:20 F:Other.cpp(1) L:2 F:Mod.h(1) L:5", "파일별 묶기 순서: " + Shape(model));
        Assert(model.Summary == "5개 위치 · 3개 파일 · 색인 진행 중", "요약: " + model.Summary);
        var tick = model.Rows.OfType<ReferenceLineRow>().First(row => row.Line == "8");
        Assert(tick.Container == "Game::Tick" && tick.Match == "Compute" && !tick.ShowFile, "위치 행의 포함 함수·일치 구간");
        Assert(model.Rows[0].ToString() == "Use.cpp, 3개 위치" && tick.ToString() == "Use.cpp 8줄: Total += FMod::Compute(Delta);", "화면 읽기용 행 이름: " + model.Rows[0] + " / " + tick);

        model.Filter = "  tick ";
        Assert(model.Filter == "tick" && model.VisibleCount == 1 && Shape(model) == "F:Use.cpp(1/3) L:8", "포함 함수로 좁히기: " + Shape(model));
        Assert(model.Summary.StartsWith("1/5개 위치", StringComparison.Ordinal), "필터 요약");
        model.Filter = "nothing";
        Assert(model.Rows.Count == 0 && model.VisibleCount == 0, "일치 없음");
        model.Filter = string.Empty;

        model.SetExpanded(set.Items[0].FullPath, false);
        Assert(Shape(model) == "F:Use.cpp(3) F:Other.cpp(1) L:2 F:Mod.h(1) L:5", "파일 접기: " + Shape(model));
        var folded = model.Rows.OfType<ReferenceFileRow>().First();
        Assert(!folded.IsExpanded && folded.Glyph == "▸" && !model.IsExpanded(set.Items[0].FullPath.ToUpperInvariant()), "접힘 표시·대소문자 무시");
        model.SetAllExpanded(false);
        Assert(Shape(model) == "F:Use.cpp(3) F:Other.cpp(1) F:Mod.h(1)", "모두 접기");
        model.SetAllExpanded(true);
        Assert(model.Rows.Count == 8 && model.Rows.OfType<ReferenceFileRow>().All(row => row.IsExpanded && row.Glyph == "▾"), "모두 펼치기");

        model.GroupByFile = false;
        Assert(model.Rows.Count == 5 && model.Rows.OfType<ReferenceLineRow>().All(row => row.ShowFile), "묶지 않으면 위치 행만, 파일 이름과 함께");
        model.GroupByFile = true;
    }

    private static void History()
    {
        var model = new ReferenceResultsModel();
        var sets = Enumerable.Range(0, ReferenceResultsModel.MaxHistory + 2).Select(index => Sample("Symbol" + index)).ToArray();
        foreach (var set in sets) model.Show(set);
        Assert(model.History.Count == ReferenceResultsModel.MaxHistory && model.History[0] == sets[^1] &&
               !model.History.Contains(sets[0]) && !model.History.Contains(sets[1]), "최근 결과 수 제한·최신 우선");

        var older = sets[5];
        model.Filter = "use";
        model.SetExpanded(sets[^1].Items[0].FullPath, false);
        model.Select(older, clearFilter: true);
        Assert(model.Current == older && model.Filter.Length == 0 && model.History[0] == sets[^1], "최근 결과로 돌아가기는 순서를 바꾸지 않음");
        Assert(model.Rows.OfType<ReferenceFileRow>().All(row => row.IsExpanded), "다른 결과로 바꾸면 접힘 초기화");
        model.Filter = "use";
        model.Show(older);
        Assert(model.History[0] == older && model.History.Count(set => set == older) == 1 && model.Filter.Length == 0, "같은 결과를 다시 보이면 맨 앞으로, 필터 지움");
        Assert(older.Title.StartsWith("Symbol5 · 5개 · ", StringComparison.Ordinal), "최근 결과 이름: " + older.Title);

        model.Filter = "use";
        model.Clear();
        Assert(model.Current is null && model.History.Count == 0 && model.Rows.Count == 0 && model.Filter.Length == 0 && model.Summary.Length == 0,
            "Solution을 닫으면 결과·최근 결과·필터 비우기");
    }

    private static void InitialValuesDoNotRaiseHandlers(string root)
    {
        // XAML이 초기값을 넣으며 변경 처리기를 부르면 처리기가 아직 만들지 않은 요소를 건드려 창 생성이 실패합니다(0.38.0 첫 빌드 결함).
        var pairs = new[] { ("Checked", "IsChecked"), ("Unchecked", "IsChecked"), ("TextChanged", "Text"),
            ("SelectionChanged", "SelectedIndex"), ("SelectionChanged", "SelectedItem"), ("SelectionChanged", "SelectedValue") };
        var xml = XDocument.Load(Path.Combine(root, "src", "VisualBoost.Package", "UI", "ReferencesControl.xaml"));
        foreach (var element in xml.Descendants())
        {
            foreach (var (handler, value) in pairs)
                Assert(element.Attribute(handler) is null || element.Attribute(value) is null,
                    $"{element.Name.LocalName}: {value} 초기값과 {handler} 처리기를 함께 두지 않음");
        }
    }

    private static void Xaml(string root, string output)
    {
        var element = Program.LoadXaml(root, "ReferencesControl");
        var window = new Window { Content = element, Width = 1000, Height = 360, Left = -20000, ShowInTaskbar = false };
        var model = new ReferenceResultsModel();
        model.Show(Sample("FMod::Compute"));
        var list = (ListBox)element.FindName("ResultsList");
        ((TextBlock)element.FindName("SymbolText")).Text = model.Current!.Symbol;
        ((TextBlock)element.FindName("SummaryText")).Text = model.Summary;
        ((UIElement)element.FindName("EmptyText")).Visibility = Visibility.Collapsed;
        list.ItemsSource = model.Rows;
        list.SelectedIndex = 2;
        window.Show();
        Program.Pump();

        var items = Program.Descendants<ListBoxItem>(list).ToArray();
        Assert(items.Length == model.Rows.Count, "행 컨테이너 수");
        var texts = Program.Descendants<TextBlock>(list).Where(text => text.IsVisible).ToArray();
        Assert(texts.Any(text => text.Text == "Use.cpp" && text.FontWeight == FontWeights.SemiBold) && texts.Any(text => text.Text == "3"), "파일 머리 행: 이름·위치 수");
        Assert(texts.Any(text => text.Text == "Game::Tick"), "위치 행: 포함 함수");
        var code = texts.First(text => text.Inlines.OfType<Run>().Count() == 3 && text.Inlines.OfType<Run>().ElementAt(1).Text == "Compute");
        Assert(code.Inlines.OfType<Run>().ElementAt(1).FontWeight == FontWeights.Bold, "코드 미리보기 일치 구간 굵게");
        var noContainer = items.First(item => item.DataContext is ReferenceLineRow { Container: "" });
        Assert(Program.Descendants<TextBlock>(noContainer).All(text => text.Name != "ContainerText" || !text.IsVisible), "포함 함수가 없으면 숨김");
        Assert(Program.Descendants<TextBlock>(items[1]).Any(text => text.Name == "FileColumn" && !text.IsVisible), "묶을 때는 위치 행에 파일 이름 숨김");
        Save(window, Path.Combine(output, "ReferencesControl.png"));

        model.GroupByFile = false;
        list.ItemsSource = model.Rows;
        Program.Pump();
        var flat = Program.Descendants<ListBoxItem>(list).First();
        Assert(Program.Descendants<TextBlock>(flat).Any(text => text.Name == "FileColumn" && text.IsVisible && text.Text == "Use.cpp"), "묶지 않으면 위치 행에 파일 이름");
        Save(window, Path.Combine(output, "ReferencesControl-flat.png"));
        window.Close();
        Program.Pump();
    }

    private static ReferenceResultSet Sample(string symbol)
    {
        var root = Path.Combine(Path.GetTempPath(), "Game");
        var use = Path.Combine(root, "Source", "Game", "Use.cpp");
        var other = Path.Combine(root, "Source", "Game", "Other.cpp");
        var header = Path.Combine(root, "Source", "Game", "Mod.h");
        NavigationResultItem Item(string path, int line, string container, string text) =>
            new(new NavigationLocation(path, line - 1, text.IndexOf("Compute", StringComparison.Ordinal), line - 1,
                text.IndexOf("Compute", StringComparison.Ordinal) + "Compute".Length, container), text, root);
        // 같은 파일 위치가 떨어져 있어도 첫 위치 기준으로 묶이는지 보기 위해 Use.cpp를 앞뒤로 나눕니다.
        var items = new[]
        {
            Item(use, 3, "Use", "    return FMod::Compute(1);"),
            Item(use, 8, "Game::Tick", "    Total += FMod::Compute(Delta);"),
            Item(other, 2, "Other", "int Other() { return FMod::Compute(2); }"),
            Item(use, 20, string.Empty, "static int Value = FMod::Compute(3);"),
            Item(header, 5, "FMod", "    static int Compute(int Value);"),
        };
        return new ReferenceResultSet(symbol, items, "색인 진행 중", new DateTime(2026, 10, 5, 21, 7, 0));
    }

    private static string Shape(ReferenceResultsModel model) => string.Join(" ", model.Rows.Select(row => row switch
    {
        ReferenceFileRow file => $"F:{file.FileName}({file.Count})",
        ReferenceLineRow line => "L:" + line.Line,
        _ => "?",
    }));

    private static void Save(Window window, string path)
    {
        var surface = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
        image.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
