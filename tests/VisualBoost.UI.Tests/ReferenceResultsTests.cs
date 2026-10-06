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
using VisualBoost.Core.Indexing;
using VisualBoost.Core.SemanticNavigation;
using VisualBoost.UI;

/// <summary>도킹 참조 창의 표시 모델·범위 필터·우클릭 메뉴 구성과 실제 XAML 행 템플릿을 검증합니다.</summary>
internal static class ReferenceResultsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Game");
    private static readonly string Use = Path.Combine(Root, "Source", "Game", "Use.cpp");
    private static readonly string Other = Path.Combine(Root, "Source", "Game", "Other.cpp");
    private static readonly string Header = Path.Combine(Root, "Source", "Game", "Mod.h");
    private static readonly string EngineRoot = Path.Combine(Path.GetTempPath(), "Engine");

    public static void Run(string root, string output)
    {
        Model();
        Scopes();
        Menu();
        CopyFormats();
        History();
        InitialValuesDoNotRaiseHandlers(root);
        SharedListFont(root);
        Xaml(root, output);
        Console.WriteLine("PASS: 참조 결과 창의 파일별 묶기·범위·필터·접기·우클릭 메뉴·최근 결과와 행 템플릿");
    }

    private static void Model()
    {
        var set = Sample("FMod::Compute");
        var model = new ReferenceResultsModel();
        var changed = new List<string>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);
        Assert(model.EmptyMessage == ReferenceResultsModel.NoResultsMessage, "결과 전 안내");
        model.Show(set);
        Assert(changed.Contains(nameof(ReferenceResultsModel.Rows)) && changed.Contains(nameof(ReferenceResultsModel.Summary)), "변경 알림");

        // 결과 순서(요청한 파일 먼저)를 지키며 파일 머리 행 뒤에 그 파일의 위치가 옵니다. 떨어져 있던 같은 파일 위치도 한데 묶습니다.
        Assert(Shape(model) == "F:Use.cpp(3) L:3 L:8 L:20 F:Other.cpp(1) L:2 F:Mod.h(1) L:5", "파일별 묶기 순서: " + Shape(model));
        Assert(model.Summary == "5개 위치 · 3개 파일 · 색인 진행 중", "요약: " + model.Summary);
        Assert(model.EmptyMessage.Length == 0, "행이 있으면 안내 없음");
        var tick = model.Rows.OfType<ReferenceLineRow>().First(row => row.Line == "8");
        Assert(tick.Container == "Game::Tick" && tick.Match == "Compute", "위치 행의 포함 함수·일치 구간");
        Assert(model.Rows[0].ToString() == "Use.cpp, 3개 위치" && tick.ToString() == "Use.cpp 8줄: Total += FMod::Compute(Delta);", "화면 읽기용 행 이름: " + model.Rows[0] + " / " + tick);

        model.Filter = "  tick ";
        Assert(model.Filter == "tick" && model.VisibleCount == 1 && Shape(model) == "F:Use.cpp(1/3) L:8", "포함 함수로 좁히기: " + Shape(model));
        Assert(model.Summary.StartsWith("1/5개 위치", StringComparison.Ordinal), "필터 요약");
        model.Filter = "nothing";
        Assert(model.Rows.Count == 0 && model.VisibleCount == 0 && model.EmptyMessage == ReferenceResultsModel.NoMatchMessage, "일치 없음");
        model.Filter = string.Empty;

        model.SetExpanded(set.Items[0].FullPath, false);
        Assert(Shape(model) == "F:Use.cpp(3) F:Other.cpp(1) L:2 F:Mod.h(1) L:5", "파일 접기: " + Shape(model));
        var folded = model.Rows.OfType<ReferenceFileRow>().First();
        Assert(!folded.IsExpanded && folded.Glyph == "▸" && !model.IsExpanded(set.Items[0].FullPath.ToUpperInvariant()), "접힘 표시·대소문자 무시");
        Assert(model.VisibleCount == 5, "접힌 파일의 위치도 보이는 결과에 남음");
        model.SetAllExpanded(false);
        Assert(Shape(model) == "F:Use.cpp(3) F:Other.cpp(1) F:Mod.h(1)", "모두 접기");
        model.SetAllExpanded(true);
        Assert(model.Rows.Count == 8 && model.Rows.OfType<ReferenceFileRow>().All(row => row.IsExpanded && row.Glyph == "▾"), "모두 펼치기");
    }

    private static void Scopes()
    {
        var set = Sample("FMod::Compute");
        var model = new ReferenceResultsModel();
        model.Show(set);
        Assert(model.Scopes.Count == 1 && model.Scope.Id == SymbolSearchScope.All.Id, "범위 목록 게시 전에는 '전체'만");

        var catalog = Catalog(withProject: true);
        model.SetScopes(catalog);
        Assert(model.Scope.Id == SymbolSearchScope.All.Id && model.Scopes.Select(scope => scope.Name).SequenceEqual(new[] { "전체", "프로젝트 코드", "엔진", "소속 미확인", "프로젝트: Game" }),
            "심볼 탐색과 같은 범위 목록: " + string.Join(",", model.Scopes.Select(scope => scope.Name)));

        var game = catalog.Last();
        model.SelectScope(game);
        Assert(Shape(model) == "F:Use.cpp(3) L:3 L:8 L:20 F:Other.cpp(1) L:2", "프로젝트 범위: " + Shape(model));
        Assert(model.Summary.StartsWith("4/5개 위치", StringComparison.Ordinal), "범위로 숨긴 위치를 요약에 표시: " + model.Summary);
        model.Filter = "tick";
        Assert(Shape(model) == "F:Use.cpp(1/3) L:8" && model.Summary.StartsWith("1/5개 위치", StringComparison.Ordinal), "범위와 필터는 함께 적용: " + Shape(model));
        model.Filter = "nothing";
        Assert(model.EmptyMessage == ReferenceResultsModel.NoMatchMessage, "범위 안에서 필터만 비면 필터 안내");
        model.Filter = string.Empty;

        model.SelectScope(catalog.First(scope => scope.Id == "unassigned"));
        Assert(Shape(model) == "F:Mod.h(1) L:5", "소속 미확인 범위: " + Shape(model));
        model.SelectScope(catalog.First(scope => scope.Id == "engine"));
        Assert(model.Rows.Count == 0 && model.EmptyMessage == "이 범위에는 위치가 없습니다 · 전체 5개", "빈 범위 안내: " + model.EmptyMessage);

        // 고른 범위는 같은 Solution 동안 새 결과·최근 결과 전환에도 유지합니다.
        model.SelectScope(game);
        model.Show(Sample("FMod::Other"));
        Assert(ReferenceEquals(model.Scope, game) && model.VisibleCount == 4, "새 결과에도 범위 유지");
        model.Select(set, clearFilter: true);
        Assert(ReferenceEquals(model.Scope, game) && model.VisibleCount == 4, "최근 결과로 돌아가도 범위 유지");

        // 다시 탐색하는 동안 '전체'만 있는 목록이 게시되면 '전체'로 보이다가, 고른 범위가 다시 게시되면 돌아갑니다.
        model.SetScopes(new[] { SymbolSearchScope.All });
        Assert(model.Scope.Id == SymbolSearchScope.All.Id && model.VisibleCount == 5, "범위 목록을 다시 만드는 동안은 '전체'");
        var republished = Catalog(withProject: true);
        model.SetScopes(republished);
        Assert(model.Scope.Id == game.Id && ReferenceEquals(model.Scope, republished.Last()) && model.VisibleCount == 4, "다시 게시되면 고른 범위의 새 목록으로");

        // 소속 목록이 게시되었는데 고른 범위가 없으면 '전체'로 확정하고, 나중에 다시 나타나도 저절로 바꾸지 않습니다.
        model.SetScopes(Catalog(withProject: false));
        Assert(model.Scope.Id == SymbolSearchScope.All.Id && model.VisibleCount == 5, "사라진 범위는 '전체'로");
        model.SetScopes(Catalog(withProject: true));
        Assert(model.Scope.Id == SymbolSearchScope.All.Id, "'전체'로 확정한 뒤에는 저절로 돌아가지 않음");

        model.SelectScope(model.Scopes.Last());
        model.Clear();
        Assert(model.Scope.Id == SymbolSearchScope.All.Id && model.Scopes.Count == 1 && model.EmptyMessage == ReferenceResultsModel.NoResultsMessage,
            "Solution을 닫으면 범위도 '전체'로");
        model.Show(set);
        model.SetScopes(Catalog(withProject: true));
        Assert(model.Scope.Id == SymbolSearchScope.All.Id && model.VisibleCount == 5, "다음 Solution은 '전체'에서 시작");
    }

    private static void Menu()
    {
        var model = new ReferenceResultsModel();
        Assert(Describe(ReferenceMenu.Entries(null, model)) == "전부 펼치기(꺼짐) 전부 접기(꺼짐)", "결과 없음: " + Describe(ReferenceMenu.Entries(null, model)));
        model.Show(Sample("FMod::Compute"));

        var line = model.Rows.OfType<ReferenceLineRow>().First();
        Assert(Describe(ReferenceMenu.Entries(line, model)) ==
               "열기[Enter] 미리 보기[Space] | 위치 복사 코드 줄 복사 전체 경로 복사 탐색기에서 보기 | 보이는 결과 모두 복사 | 전부 펼치기(꺼짐) 전부 접기",
            "위치 행 메뉴: " + Describe(ReferenceMenu.Entries(line, model)));
        var file = model.Rows.OfType<ReferenceFileRow>().First();
        Assert(Describe(ReferenceMenu.Entries(file, model)) ==
               "접기[←] | 전체 경로 복사 탐색기에서 보기 | 보이는 결과 모두 복사 | 전부 펼치기(꺼짐) 전부 접기",
            "파일 머리 행 메뉴: " + Describe(ReferenceMenu.Entries(file, model)));
        Assert(Describe(ReferenceMenu.Entries(null, model)) == "전부 펼치기(꺼짐) 전부 접기", "빈 곳 메뉴");

        model.SetExpanded(file.FullPath, false);
        var folded = model.Rows.OfType<ReferenceFileRow>().First();
        Assert(Describe(ReferenceMenu.Entries(folded, model)).StartsWith("펼치기[→] |", StringComparison.Ordinal) &&
               Describe(ReferenceMenu.Entries(folded, model)).EndsWith("전부 펼치기 전부 접기", StringComparison.Ordinal), "접힌 파일은 펼치기, 전부 펼치기 켜짐");
        model.SetAllExpanded(false);
        Assert(Describe(ReferenceMenu.Entries(null, model)) == "전부 펼치기 전부 접기(꺼짐)", "모두 접히면 전부 접기 꺼짐");
        model.Filter = "nothing";
        Assert(Describe(ReferenceMenu.Entries(null, model)) == "전부 펼치기(꺼짐) 전부 접기(꺼짐)", "보이는 파일이 없으면 둘 다 꺼짐");
    }

    private static void CopyFormats()
    {
        var model = new ReferenceResultsModel();
        var set = Sample("FMod::Compute");
        model.Show(set);
        Assert(ReferenceMenu.Location(set.Items[1]) == Use + "(8)", "위치 복사는 전체경로(줄): " + ReferenceMenu.Location(set.Items[1]));
        model.Filter = "tick";
        Assert(ReferenceMenu.Lines(model.VisibleItems) == Use + "(8): Total += FMod::Compute(Delta);", "보이는 결과 복사: " + ReferenceMenu.Lines(model.VisibleItems));
        model.Filter = string.Empty;
        model.SetAllExpanded(false);
        Assert(string.Join(",", model.VisibleItems.Select(item => item.FileName + ":" + item.Line)) == "Use.cpp:3,Use.cpp:8,Use.cpp:20,Other.cpp:2,Mod.h:5",
            "보이는 결과는 화면의 파일 순서이고 접힌 파일 위치도 포함");
        Assert(ReferenceMenu.Lines(model.VisibleItems).Split(new[] { Environment.NewLine }, StringSplitOptions.None).Length == 5, "위치마다 한 줄");

        // 미리보기는 긴 줄을 줄임표로 자르지만 복사는 원래 줄 그대로입니다.
        var longLine = "\t" + new string('x', 120) + "Compute" + new string('y', 260) + "  ";
        var start = longLine.IndexOf("Compute", StringComparison.Ordinal);
        var item = new NavigationResultItem(new NavigationLocation(Use, 0, start, 0, start + 7), longLine, Root);
        Assert(item.Code.Contains('…') && item.SourceLine == longLine.Trim() && ReferenceMenu.Lines(new[] { item }) == Use + "(1): " + longLine.Trim(),
            "코드 복사는 생략 없는 원래 줄");
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
        foreach (var file in PackageXaml(root))
        {
            foreach (var element in XDocument.Load(file).Descendants())
            {
                foreach (var (handler, value) in pairs)
                    Assert(element.Attribute(handler) is null || element.Attribute(value) is null,
                        $"{Path.GetFileName(file)} {element.Name.LocalName}: {value} 초기값과 {handler} 처리기를 함께 두지 않음");
            }
        }
    }

    private static void SharedListFont(string root)
    {
        // 참조 창·파일 탐색·심볼 탐색·정의 후보 창은 모든 글자가 한 곳(ResultListFont)에서 정한 크기 하나를 씁니다. 창의 맨 위 요소와
        // 부모 없는 팝업인 우클릭 메뉴에만 그 키를 두고, 강조는 크기 대신 굵기로 합니다. 같은 곳에서 VS UI와 같은 글자 렌더링(Display)을 정합니다.
        const string key = "{DynamicResource {x:Static ui:ResultListFont.SizeKey}}";
        foreach (var name in new[] { "ReferencesControl", "FileSearchDialog", "SymbolSearchDialog", "NavigationResultsDialog" })
        {
            var document = XDocument.Load(Path.Combine(root, "src", "VisualBoost.Package", "UI", name + ".xaml"));
            var sized = document.Descendants().Where(element => element.Attribute("FontSize") is not null).ToArray();
            Assert((string?)document.Root!.Attribute("FontSize") == key, name + ": 창 전체 글씨 크기");
            Assert(sized.All(element => element == document.Root || element.Name.LocalName == "ContextMenu"),
                name + ": 요소별 크기 지정 없음: " + string.Join(",", sized.Where(element => element != document.Root).Select(element => element.Name.LocalName)));
            Assert(sized.All(element => (string?)element.Attribute("FontSize") == key), name + ": 우클릭 메뉴도 같은 크기");
            Assert(sized.All(element => (string?)element.Attribute("TextOptions.TextFormattingMode") == "Display"), name + ": 창과 우클릭 메뉴는 VS UI 글자 렌더링");
            FixedHeights(document, name);
        }

        // 문서 함수 트리(Alt+M) 팝업도 표면 하나에서 같은 크기를 정합니다. 글자 렌더링은 열 때 편집기 확대를 보고 정하므로 고정하지 않습니다.
        var popup = XDocument.Load(Path.Combine(root, "src", "VisualBoost.Package", "DocumentNavigation", "DocumentNavigationControl.xaml"));
        var surface = popup.Descendants().Single(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "PopupSurface");
        Assert((string?)surface.Attribute("TextElement.FontSize") == key && surface.Attribute("TextOptions.TextFormattingMode") is null,
            "Alt+M 팝업: 목록 글씨 크기, 렌더링은 열 때 결정");
        Assert(popup.Descendants().All(element => element.Attribute("FontSize") is null), "Alt+M 팝업: 요소별 크기 지정 없음");
        string? Setter(string target, string property) => (string?)popup.Descendants()
            .Where(element => element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") == target)
            .SelectMany(style => style.Elements()).FirstOrDefault(element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == property)
            ?.Attribute("Value");
        Assert(Setter("ListViewItem", "MinHeight") == "20" && Setter("GridViewColumnHeader", "MinHeight") == "22", "Alt+M 팝업: 행 최소 20·머리글 최소 22");
        FixedHeights(popup, "Alt+M 팝업");
    }

    /// <summary>행·열 머리글은 최소 높이만 둡니다. 고정 높이는 큰 환경 글꼴에서 글자를 자릅니다.</summary>
    private static void FixedHeights(XDocument document, string name)
    {
        var fixedRows = document.Descendants()
            .Where(element => element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") is "ListViewItem" or "ListBoxItem" or "GridViewColumnHeader")
            .SelectMany(style => style.Elements())
            .Where(element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "Height").ToArray();
        Assert(fixedRows.Length == 0, name + ": 행·머리글 고정 높이 없음");
    }

    private static void Xaml(string root, string output)
    {
        var element = Program.LoadXaml(root, "ReferencesControl");
        var window = new Window { Content = element, Width = 1000, Height = 360, Left = -20000, ShowInTaskbar = false };
        var model = new ReferenceResultsModel();
        model.Show(Sample("FMod::Compute"));
        model.SetScopes(Catalog(withProject: true));
        var list = (ListBox)element.FindName("ResultsList");
        ((TextBlock)element.FindName("SymbolText")).Text = model.Current!.Symbol;
        ((TextBlock)element.FindName("SummaryText")).Text = model.Summary;
        ((UIElement)element.FindName("EmptyText")).Visibility = Visibility.Collapsed;
        var scopeBox = (ComboBox)element.FindName("ScopeBox");
        scopeBox.ItemsSource = model.Scopes;
        scopeBox.SelectedItem = model.Scope;
        list.ItemsSource = model.Rows;
        list.SelectedIndex = 2;
        window.Show();
        Program.Pump();

        // 머리 줄에는 묶기 체크박스·펼치기/접기 버튼 없이 필터·범위·최근 결과만 둡니다.
        Assert(!Program.Descendants<CheckBox>(element).Any() && !Program.Descendants<Button>(element).Any(), "머리 줄에 묶기·펼치기·접기 컨트롤 없음");
        Assert(Program.Descendants<ComboBox>(element).Select(box => box.Name).SequenceEqual(new[] { "ScopeBox", "HistoryBox" }) &&
               Program.Descendants<TextBlock>(scopeBox).Any(text => text.Text == "전체"), "범위·최근 결과 선택");
        Assert(list.ContextMenu is not null && ((TextBlock)element.FindName("EmptyText")).IsHitTestVisible == false, "빈 안내 위에서도 목록 우클릭 메뉴");

        var items = Program.Descendants<ListBoxItem>(list).ToArray();
        Assert(items.Length == model.Rows.Count, "행 컨테이너 수");
        var texts = Program.Descendants<TextBlock>(list).Where(text => text.IsVisible).ToArray();
        Assert(texts.Any(text => text.Text == "Use.cpp" && text.FontWeight == FontWeights.SemiBold) && texts.Any(text => text.Text == "3"), "파일 머리 행: 이름·위치 수");
        Assert(texts.Any(text => text.Text == "Game::Tick"), "위치 행: 포함 함수");
        Assert(items.Where(item => item.DataContext is ReferenceLineRow).All(item => Program.Descendants<TextBlock>(item).All(text => text.Text != "Use.cpp" || !text.IsVisible)),
            "위치 행에는 파일 이름을 다시 보이지 않음");
        var codes = texts.Where(text => text.Name == "CodeText").ToArray();
        var code = codes.First(text => Joined(text) == "return FMod::Compute(1);");
        Assert(Bold(code) == "Compute", "코드 미리보기 일치 구간만 굵게");
        Assert(codes.All(text => Bold(text) == "Compute"), "모든 위치 행의 일치 구간 굵게");
        // 한 화면에 위치를 많이 보이도록 행은 글자 높이에 위아래 1 DIP 여백만 둡니다. 글씨 크기를 줄이면 행도 함께 줄어듭니다.
        bool Fits(ListBoxItem item) => item.ActualHeight <= Program.Descendants<TextBlock>(item).Max(text => text.ActualHeight) + 2.5;
        var lineItems = items.Where(item => item.DataContext is ReferenceLineRow).ToArray();
        Assert(lineItems.All(item => item.ActualHeight < 22 && Fits(item)), "위치 행 높이: " + string.Join(",", lineItems.Select(item => item.ActualHeight)));
        var fileItems = items.Where(item => item.DataContext is ReferenceFileRow).ToArray();
        Assert(fileItems.All(item => item.ActualHeight < 24 && Fits(item)), "파일 머리 행 높이: " + string.Join(",", fileItems.Select(item => item.ActualHeight)));
        var noContainer = items.First(item => item.DataContext is ReferenceLineRow { Container: "" });
        Assert(Program.Descendants<TextBlock>(noContainer).All(text => text.Name != "ContainerText" || !text.IsVisible), "포함 함수가 없으면 숨김");
        Save(window, Path.Combine(output, "ReferencesControl.png"));

        // 범위 안에 위치가 없으면 빈 안내를 보입니다.
        model.SelectScope(model.Scopes.First(scope => scope.Id == "engine"));
        list.ItemsSource = model.Rows;
        var empty = (TextBlock)element.FindName("EmptyText");
        empty.Text = model.EmptyMessage;
        empty.Visibility = Visibility.Visible;
        Program.Pump();
        Assert(!Program.Descendants<ListBoxItem>(list).Any() && empty.IsVisible && empty.ActualWidth > 0, "빈 범위 안내 표시");
        Save(window, Path.Combine(output, "ReferencesControl-empty-scope.png"));
        window.Close();
        Program.Pump();
    }

    private static IReadOnlyList<SymbolSearchScope> Catalog(bool withProject)
    {
        var projects = withProject ? new[] { SymbolSearchScope.Project(Path.Combine(Root, "Game.vcxproj"), "Game", new[] { Use, Other }) } : Array.Empty<SymbolSearchScope>();
        return SymbolSearchScope.CreateCatalog(projects, new[] { Use, Other }, new[] { EngineRoot });
    }

    private static string[] PackageXaml(string root)
    {
        var files = Directory.GetFiles(Path.Combine(root, "src", "VisualBoost.Package"), "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToArray();
        Assert(files.Any(file => Path.GetFileName(file) == "ReferencesControl.xaml"), "검사 대상 XAML 찾기");
        return files;
    }

    /// <summary>메뉴를 "머리글[키]"로 적고 꺼진 항목은 "(꺼짐)", 구분선은 "|"로 적습니다.</summary>
    private static string Describe(IReadOnlyList<ReferenceMenuEntry?> entries) => string.Join(" ", entries.Select(entry =>
        entry is null ? "|" : entry.Header + (entry.Gesture.Length > 0 ? $"[{entry.Gesture}]" : string.Empty) + (entry.IsEnabled ? string.Empty : "(꺼짐)")));

    private static string Joined(TextBlock text) => string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text));

    private static string Bold(TextBlock text) =>
        string.Concat(text.Inlines.OfType<Run>().Where(run => run.FontWeight == FontWeights.Bold).Select(run => run.Text));

    private static ReferenceResultSet Sample(string symbol)
    {
        NavigationResultItem Item(string path, int line, string container, string text) =>
            new(new NavigationLocation(path, line - 1, text.IndexOf("Compute", StringComparison.Ordinal), line - 1,
                text.IndexOf("Compute", StringComparison.Ordinal) + "Compute".Length, container), text, Root);
        // 같은 파일 위치가 떨어져 있어도 첫 위치 기준으로 묶이는지 보기 위해 Use.cpp를 앞뒤로 나눕니다.
        var items = new[]
        {
            Item(Use, 3, "Use", "    return FMod::Compute(1);"),
            Item(Use, 8, "Game::Tick", "    Total += FMod::Compute(Delta);"),
            Item(Other, 2, "Other", "int Other() { return FMod::Compute(2); }"),
            Item(Use, 20, string.Empty, "static int Value = FMod::Compute(3);"),
            Item(Header, 5, "FMod", "    static int Compute(int Value);"),
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
