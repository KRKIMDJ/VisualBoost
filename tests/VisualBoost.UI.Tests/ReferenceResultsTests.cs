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
using VisualBoost.Core.Coloring;
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
        Modes();
        Order();
        Grouping();
        Layout();
        Menu();
        CopyFormats();
        History();
        InitialValuesDoNotRaiseHandlers(root);
        SharedListFont(root);
        Xaml(root, output);
        Console.WriteLine("PASS: 참조 결과 창의 프로젝트·파일별 묶기·범위·필터·접기·우클릭 메뉴·최근 결과와 행 템플릿");
    }

    private static void Model()
    {
        var set = Sample("FMod::Compute");
        var model = AllProjectsModel();
        var changed = new List<string>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);
        Assert(model.EmptyMessage == ReferenceResultsModel.NoResultsMessage, "결과 전 안내");
        model.Show(set);
        Assert(changed.Contains(nameof(ReferenceResultsModel.Rows)) && changed.Contains(nameof(ReferenceResultsModel.Summary)), "변경 알림");

        // 요청한 파일(Use.cpp)이 먼저이고 나머지는 같은 폴더라 경로 순서입니다. 떨어져 있던 같은 파일 위치도 한데 묶습니다.
        Assert(Shape(model) == "F:Use.cpp(3) L:3 L:8 L:20 F:Mod.h(1) L:5 F:Other.cpp(1) L:2", "파일별 묶기 순서: " + Shape(model));
        Assert(model.Summary == "5개 위치 · 3개 파일 · 색인 진행 중", "요약: " + model.Summary);
        Assert(model.EmptyMessage.Length == 0, "행이 있으면 안내 없음");
        var tick = model.Rows.OfType<ReferenceLineRow>().First(row => row.Line == "8");
        Assert(tick.Container == "Game::Tick" && tick.Match == "Compute", "위치 행의 포함 함수·일치 구간");
        Assert(model.Rows[0].ToString() == "Use.cpp, 3개 위치" && tick.ToString() == "Use.cpp 8줄 Game::Tick: Total += FMod::Compute(Delta);", "화면 읽기용 행 이름: " + model.Rows[0] + " / " + tick);

        model.Filter = "  tick ";
        Assert(model.Filter == "tick" && model.VisibleCount == 1 && Shape(model) == "F:Use.cpp(1/3) L:8", "포함 함수로 좁히기: " + Shape(model));
        Assert(model.Summary == "1/5개 위치 · 1/3개 파일 · 색인 진행 중", "필터 요약은 위치·파일 모두 보이는 수/전체: " + model.Summary);
        model.Filter = "nothing";
        Assert(model.Rows.Count == 0 && model.VisibleCount == 0 && model.EmptyMessage == ReferenceResultsModel.NoMatchMessage, "일치 없음");

        // 역할 표식은 화면 읽기 이름에 들어가고, 표식 글자를 그대로 치면 그 역할의 위치만 남습니다. 표식 글자 일부로는 좁히지 않습니다.
        model.Filter = "선언";
        var declaration = model.Rows.OfType<ReferenceLineRow>().SingleOrDefault();
        Assert(Shape(model) == "F:Mod.h(1) L:5" && declaration is { RoleText: "선언" } && declaration.ToString() == "Mod.h 5줄 선언 FMod: static int Compute(int Value);",
            "역할로 좁히기·화면 읽기 이름: " + Shape(model) + " / " + declaration);
        model.Filter = "선";
        Assert(model.Rows.Count == 0, "표식 글자 일부로는 좁히지 않음");
        Assert(tick.RoleText.Length == 0 && Item(Use, 1, string.Empty, "x", NavigationRole.Definition).RoleText == "정의", "역할 없는 위치는 표식 없음");
        model.Filter = string.Empty;
        RoleFilter();

        model.SetExpanded(set.Items[0].FullPath, false);
        Assert(Shape(model) == "F:Use.cpp(3) F:Mod.h(1) L:5 F:Other.cpp(1) L:2", "파일 접기: " + Shape(model));
        var folded = model.Rows.OfType<ReferenceFileRow>().First();
        Assert(!folded.IsExpanded && folded.Glyph == "▸" && !model.IsExpanded(set.Items[0].FullPath.ToUpperInvariant()), "접힘 표시·대소문자 무시");
        Assert(model.VisibleCount == 5, "접힌 파일의 위치도 보이는 결과에 남음");
        model.SetAllExpanded(false);
        Assert(Shape(model) == "F:Use.cpp(3) F:Mod.h(1) F:Other.cpp(1)", "모두 접기");
        model.SetAllExpanded(true);
        Assert(model.Rows.Count == 8 && model.Rows.OfType<ReferenceFileRow>().All(row => row.IsExpanded && row.Glyph == "▾"), "모두 펼치기");
    }

    private static void Modes()
    {
        var set = Sample("FMod::Compute");
        var model = new ReferenceResultsModel();
        model.Show(set);
        Assert(model.Mode == ReferenceScopeMode.CurrentProject && !model.HasCurrentProject, "기본은 현재 프로젝트, 소속 목록 전에는 현재 프로젝트 모름");

        // 소속 목록이 게시되기 전에는 '현재 프로젝트'를 골라도 모든 위치를 보이고 확인 중임을 알립니다.
        model.SetMode(ReferenceScopeMode.CurrentProject);
        Assert(model.VisibleCount == 5 && model.Summary.Contains("현재 프로젝트 확인 중") &&
               model.CurrentProjectToolTip == ReferenceResultsModel.CurrentProjectTip + "\n현재 프로젝트 확인 중", "확인 중 안내: " + model.Summary);

        // '전체' 하나뿐인 초기 목록은 아직 확인 중이고, 게시된 목록에 프로젝트가 없으면(폴더 작업 영역 등) 확인 중으로 남기지 않습니다.
        var bare = new ReferenceResultsModel();
        bare.Show(Sample("FMod::Compute"));
        bare.SetMode(ReferenceScopeMode.CurrentProject);
        bare.SetProjects(new[] { SymbolSearchScope.All });
        var initial = bare.Summary;
        bare.SetProjects(Catalog(withProject: false));
        Assert(initial.Contains("현재 프로젝트 확인 중") && bare.VisibleCount == 5 && bare.Summary.Contains("프로젝트 정보가 없어 모든 위치 표시"),
            "프로젝트 없는 작업 영역 안내: " + initial + " → " + bare.Summary);

        // 요청한 파일(Use.cpp)이 속한 프로젝트 Game에는 Use.cpp·Other.cpp만 있습니다. Mod.h는 미등록입니다.
        var catalog = Catalog(withProject: true);
        Assert(model.SetProjects(catalog) && !model.SetProjects(catalog), "같은 소속 목록은 다시 계산하지 않음");
        Assert(model.HasCurrentProject && model.CurrentProjectNames == "프로젝트: Game" &&
               model.CurrentProjectToolTip == ReferenceResultsModel.CurrentProjectTip + "\n프로젝트: Game", "현재 프로젝트 판정: " + model.CurrentProjectToolTip);
        Assert(Shape(model) == "F:Use.cpp(3) L:3 L:8 L:20 F:Other.cpp(1) L:2", "현재 프로젝트만: " + Shape(model));
        Assert(model.Summary == "4/5개 위치 · 2/3개 파일 · 색인 진행 중", "범위로 숨긴 위치를 요약에 표시: " + model.Summary);
        model.Filter = "tick";
        Assert(Shape(model) == "F:Use.cpp(1/3) L:8", "범위와 필터는 함께 적용: " + Shape(model));
        model.Filter = string.Empty;

        // 모드는 새 결과·최근 결과 전환에도 유지하고, 현재 프로젝트는 그 결과를 찾은 파일 기준입니다.
        model.Show(Sample("FMod::Other"));
        Assert(model.Mode == ReferenceScopeMode.CurrentProject && model.VisibleCount == 4, "새 결과에도 모드 유지");
        model.Show(Sample("FEngine::Tick", Path.Combine(EngineRoot, "Source", "Tick.cpp")));
        Assert(!model.HasCurrentProject && model.VisibleCount == 5 && model.Summary.Contains("현재 파일이 속한 프로젝트가 없어 모든 프로젝트 표시"),
            "요청한 파일이 프로젝트 밖이면 모든 위치: " + model.Summary);
        model.Select(set);
        Assert(model.HasCurrentProject && model.VisibleCount == 4, "최근 결과로 돌아가면 그 결과의 현재 프로젝트");

        var outside = new ReferenceResultSet("FMod::Only", new[] { Item(Header, 5, "FMod", "    static int Compute(int Value);") }, string.Empty, DateTime.Now, Use);
        model.Show(outside);
        Assert(model.Rows.Count == 0 && model.EmptyMessage == "현재 프로젝트에는 위치가 없습니다 · 전체 1개", "현재 프로젝트에 위치가 없으면 안내: " + model.EmptyMessage);
        model.SetMode(ReferenceScopeMode.AllProjects);
        Assert(model.Rows.Count == 2 && model.EmptyMessage.Length == 0, "모든 프로젝트로 돌아가기");

        // Solution을 닫으면 결과와 소속 목록은 지우고 모드(Solution과 무관한 선택)는 유지합니다.
        model.SetMode(ReferenceScopeMode.CurrentProject);
        model.Clear();
        Assert(model.Mode == ReferenceScopeMode.CurrentProject && model.EmptyMessage == ReferenceResultsModel.NoResultsMessage, "닫아도 모드 유지");
        model.Show(set);
        Assert(!model.HasCurrentProject && model.VisibleCount == 5, "다음 Solution의 소속 목록 전에는 모든 위치");
        model.SetProjects(Catalog(withProject: true));
        Assert(model.VisibleCount == 4, "소속 목록을 받으면 현재 프로젝트 적용");
    }

    private static void Order()
    {
        // clangd 참조 응답에는 관련도 순서가 없어 VisualBoost가 요청한 파일에서 가까운 순서를 정합니다.
        var game = Path.Combine(Root, "Source", "Game");
        var origin = Path.Combine(game, "Private", "Foo.cpp");
        var header = Path.Combine(game, "Public", "Foo.h");
        var near = Path.Combine(game, "Private", "Baz.cpp");
        var deeper = Path.Combine(game, "Private", "Sub", "Bar.cpp");
        var tool = Path.Combine(Root, "Source", "Tools", "Tool.cpp");
        var generated = Path.Combine(Root, "Intermediate", "Gen.h");
        var engine = Path.Combine(EngineRoot, "Source", "Runtime", "Engine.cpp");
        var items = new[]
        {
            Item(engine, 3, "", "Foo();"), Item(tool, 4, "", "Foo();"), Item(deeper, 7, "", "Foo();"), Item(near, 9, "", "Foo();"),
            Item(generated, 1, "", "Foo();"), Item(header, 2, "", "void Foo();"), Item(near, 2, "", "Foo();"), Item(origin, 5, "", "Foo();"),
        };
        var projects = new[]
        {
            SymbolSearchScope.Project(Path.Combine(game, "Game.vcxproj"), "Game", new[] { origin, header, near, deeper }),
            SymbolSearchScope.Project(Path.Combine(Root, "Source", "Tools", "Tools.vcxproj"), "Tools", new[] { tool }),
        };
        var catalog = SymbolSearchScope.CreateCatalog(projects, new[] { origin, header, near, deeper, tool }, new[] { EngineRoot });
        var model = AllProjectsModel();
        model.Show(new ReferenceResultSet("Foo", items, string.Empty, DateTime.Now, origin, header));
        model.SetProjects(catalog);
        var order = string.Join(" ", model.VisibleItems.Select(item => item.FileName + ":" + item.Line));
        Assert(order == "Foo.cpp:5 Foo.h:2 Baz.cpp:2 Baz.cpp:9 Bar.cpp:7 Tool.cpp:4 Gen.h:1 Engine.cpp:3",
            "요청 파일 → 짝 헤더 → 현재 프로젝트(가까운 폴더 먼저) → 다른 프로젝트 → 그 밖(가까운 폴더 먼저): " + order);

        // 짝은 명령이 헤더·구현 전환 규칙으로 고른 하나뿐입니다. 이름만 같은 엔진 헤더는 앞에 오지 않습니다.
        var engineFoo = Path.Combine(EngineRoot, "Source", "Runtime", "Foo.h");
        var sameName = AllProjectsModel();
        sameName.Show(new ReferenceResultSet("Foo", items.Append(Item(engineFoo, 6, "", "void Foo();")).ToArray(), string.Empty, DateTime.Now, origin, header));
        sameName.SetProjects(catalog);
        var sameNameOrder = string.Join(" ", sameName.VisibleItems.Select(item => item.FileName + ":" + item.Line));
        Assert(sameNameOrder.StartsWith("Foo.cpp:5 Foo.h:2 Baz.cpp:2", StringComparison.Ordinal) &&
               sameNameOrder.IndexOf("Foo.h:6", StringComparison.Ordinal) > sameNameOrder.IndexOf("Tool.cpp:4", StringComparison.Ordinal),
            "이름만 같은 먼 파일은 짝 등급 아님: " + sameNameOrder);

        // 현재 프로젝트를 알 때 짝이 다른 프로젝트·엔진에 있으면 현재 프로젝트 파일보다 앞에 두지 않고, 모를 때는 짝을 앞에 둡니다.
        var inGame = new HashSet<string>(new[] { origin, near }, StringComparer.OrdinalIgnoreCase);
        string Sorted(bool hasProject) => string.Join(" ", ReferenceOrder.Sort(new[] { Item(engineFoo, 6, "", "void Foo();"), Item(near, 2, "", "Foo();"), Item(origin, 5, "", "Foo();") },
            origin, engineFoo, hasProject, inGame.Contains, _ => false).Select(item => item.FileName));
        Assert(Sorted(hasProject: true) == "Foo.cpp Baz.cpp Foo.h" && Sorted(hasProject: false) == "Foo.cpp Foo.h Baz.cpp",
            "현재 프로젝트 밖의 짝: " + Sorted(true) + " / 소속을 모를 때: " + Sorted(false));
        Assert(ReferenceOrder.Distance(ReferenceOrder.Folders(@"C:\A\B\C"), ReferenceOrder.Folders(@"C:\A\D")) == 3 &&
               ReferenceOrder.Distance(ReferenceOrder.Folders(@"C:\A\B"), ReferenceOrder.Folders(@"c:\a\b")) == 0 &&
               ReferenceOrder.Distance(null, ReferenceOrder.Folders(@"C:\A")) == 0, "폴더 거리");

        // 소속 목록 전에도 요청 파일·짝 파일·폴더 거리로 정렬합니다.
        var early = AllProjectsModel();
        early.Show(new ReferenceResultSet("Foo", items, string.Empty, DateTime.Now, origin, header));
        var earlyOrder = string.Join(" ", early.VisibleItems.Select(item => item.FileName));
        Assert(earlyOrder.StartsWith("Foo.cpp Foo.h Baz.cpp Baz.cpp Bar.cpp", StringComparison.Ordinal), "소속 목록 전 순서: " + earlyOrder);
    }

    private static void Grouping()
    {
        // 프로젝트 머리 행은 모든 프로젝트 모드에서 결과가 둘 이상의 묶음(프로젝트·프로젝트 밖)에 걸칠 때만 둡니다.
        var model = AllProjectsModel();
        model.Show(Sample("FMod::Compute"));
        Assert(!model.IsGrouped && Shape(model).StartsWith("F:", StringComparison.Ordinal), "소속 목록 전에는 한 묶음이라 머리 행 없음: " + Shape(model));
        model.SetProjects(Catalog(withProject: true));
        Assert(model.IsGrouped && Shape(model) == "P:Game(4) F:Use.cpp(3) L:3 L:8 L:20 F:Other.cpp(1) L:2 P:프로젝트 밖(1) F:Mod.h(1) L:5",
            "프로젝트별 묶기: " + Shape(model));
        var game = model.Rows.OfType<ReferenceProjectRow>().First();
        var outside = model.Rows.OfType<ReferenceProjectRow>().Last();
        Assert(game.Name == "Game" && !game.IsOutside && game.IconKind == "project" && game.Glyph == "▾" && game.ToString() == "Game, 4개 위치",
            "프로젝트 머리 행은 범위 이름 앞말 없이: " + game);
        Assert(outside.Key == ReferenceResultsModel.OutsideKey && outside.IsOutside && outside.IconKind == "unknown" && outside.Name == ReferenceResultsModel.OutsideName,
            "프로젝트 밖 머리 행");
        Assert(model.Rows.OfType<ReferenceFileRow>().All(row => row.Indent.Left == ReferenceRowText.LevelWidth) &&
               model.Rows.OfType<ReferenceLineRow>().All(row => row.Indent.Left == ReferenceRowText.LevelWidth + ReferenceRowText.GlyphWidth),
            "묶이면 파일·위치 행을 한 단계 들여씀");
        Assert(model.Summary == "5개 위치 · 3개 파일 · 색인 진행 중", "묶어도 요약은 위치·파일 수: " + model.Summary);

        // 필터를 치는 동안 묶음 여부가 흔들리지 않게 필터 전 위치로 정하고, 머리 행 수는 보이는 수/전체입니다.
        model.Filter = "tick";
        Assert(Shape(model) == "P:Game(1/4) F:Use.cpp(1/3) L:8", "필터 중 머리 행: " + Shape(model));
        model.Filter = string.Empty;

        // 묶음 접기는 파일 접기와 같은 기억을 쓰고, 접힌 묶음의 위치도 보이는 결과(복사 대상)에 남깁니다.
        model.SetExpanded(game.Key, false);
        Assert(Shape(model) == "P:Game(4) P:프로젝트 밖(1) F:Mod.h(1) L:5" && !model.IsExpanded(game.Key) && model.VisibleCount == 5 &&
               model.Rows.OfType<ReferenceProjectRow>().First().Glyph == "▸", "프로젝트 접기: " + Shape(model));
        Assert(string.Join(",", model.VisibleItems.Select(item => item.FileName + ":" + item.Line)) == "Use.cpp:3,Use.cpp:8,Use.cpp:20,Other.cpp:2,Mod.h:5",
            "보이는 결과는 묶음 순서");
        var foldedHead = model.Rows.OfType<ReferenceProjectRow>().First();
        Assert(Describe(ReferenceMenu.Entries(foldedHead, model)) == "펼치기[→] | 보이는 결과 모두 복사 | 전부 펼치기 전부 접기",
            "접힌 프로젝트 머리 행 메뉴: " + Describe(ReferenceMenu.Entries(foldedHead, model)));
        model.SetAllExpanded(false);
        Assert(Shape(model) == "P:Game(4) P:프로젝트 밖(1)" && Describe(ReferenceMenu.Entries(null, model)) == "전부 펼치기 전부 접기(꺼짐)",
            "전부 접기는 프로젝트·파일 모두: " + Shape(model));
        model.SetExpanded(game.Key, true);
        Assert(Shape(model) == "P:Game(4) F:Use.cpp(3) F:Other.cpp(1) P:프로젝트 밖(1)", "프로젝트만 펼치면 파일은 접힌 채: " + Shape(model));
        model.SetAllExpanded(true);
        Assert(model.Rows.Count == 10, "전부 펼치기는 두 단계 모두");
        var head = model.Rows.OfType<ReferenceProjectRow>().First();
        Assert(Describe(ReferenceMenu.Entries(head, model)) == "접기[←] | 보이는 결과 모두 복사 | 전부 펼치기(꺼짐) 전부 접기",
            "프로젝트 머리 행 메뉴: " + Describe(ReferenceMenu.Entries(head, model)));

        // 현재 프로젝트 모드는 이미 한 프로젝트라 머리 행 없이 파일부터 보입니다.
        model.SetMode(ReferenceScopeMode.CurrentProject);
        Assert(!model.IsGrouped && Shape(model) == "F:Use.cpp(3) L:3 L:8 L:20 F:Other.cpp(1) L:2" &&
               model.Rows.OfType<ReferenceFileRow>().All(row => row.Indent.Left == 0) &&
               model.Rows.OfType<ReferenceLineRow>().All(row => row.Indent.Left == ReferenceRowText.GlyphWidth), "현재 프로젝트 모드는 머리 행 없음: " + Shape(model));

        // 결과가 한 프로젝트 안에만 있으면 모든 프로젝트 모드에서도 머리 행을 두지 않습니다.
        var single = AllProjectsModel();
        single.Show(new ReferenceResultSet("FMod::Compute", new[] { Item(Use, 3, "Use", "return FMod::Compute(1);"), Item(Other, 2, "Other", "return FMod::Compute(2);") },
            string.Empty, DateTime.Now, Use));
        single.SetProjects(Catalog(withProject: true));
        Assert(!single.IsGrouped && Shape(single) == "F:Use.cpp(1) L:3 F:Other.cpp(1) L:2", "한 프로젝트뿐이면 머리 행 없음: " + Shape(single));

        // 여러 프로젝트에 등록된 공유 헤더는 요청한 파일의 프로젝트에, 그렇지 않으면 범위 목록 순서(프로젝트 이름순)상 첫 프로젝트에 묶습니다.
        // 요청한 파일의 프로젝트가 맨 앞입니다.
        var shared = Path.Combine(Root, "Source", "Shared", "Shared.h");
        var tool = Path.Combine(Root, "Source", "Tools", "Tool.cpp");
        var editor = Path.Combine(Root, "Source", "Editor", "Editor.cpp");
        var projects = new[]
        {
            SymbolSearchScope.Project(Path.Combine(Root, "Tools.vcxproj"), "Tools", new[] { tool, shared }),
            SymbolSearchScope.Project(Path.Combine(Root, "Editor.vcxproj"), "Editor", new[] { editor }),
            SymbolSearchScope.Project(Path.Combine(Root, "Game.vcxproj"), "Game", new[] { Use, shared }),
        };
        var catalog = SymbolSearchScope.CreateCatalog(projects, new[] { tool, shared, editor, Use }, new[] { EngineRoot });
        var items = new[] { Item(shared, 1, "", "int Compute();"), Item(tool, 2, "", "Compute();"), Item(editor, 3, "", "Compute();"), Item(Use, 4, "", "Compute();") };
        string Owners(string origin)
        {
            var grouped = AllProjectsModel();
            grouped.Show(new ReferenceResultSet("Compute", items, string.Empty, DateTime.Now, origin));
            grouped.SetProjects(catalog);
            var owner = string.Empty;
            var result = new List<string>();
            foreach (var row in grouped.Rows)
            {
                if (row is ReferenceProjectRow project) owner = project.Name;
                else if (row is ReferenceFileRow file) result.Add(file.FileName + "@" + owner);
            }

            return string.Join(" ", result);
        }

        // Shared.h는 Game·Tools 둘에 등록돼 있습니다. Tools에서 찾으면 Tools에, Game 밖(Editor)에서 찾으면 이름순 첫 프로젝트 Game에 묶습니다.
        var fromTools = Owners(tool);
        var fromEditor = Owners(editor);
        Assert(fromTools.StartsWith("Tool.cpp@Tools ", StringComparison.Ordinal) && fromTools.Contains("Shared.h@Tools") && fromTools.Contains("Use.cpp@Game"),
            "공유 헤더는 요청한 파일의 프로젝트에: " + fromTools);
        Assert(fromEditor.StartsWith("Editor.cpp@Editor ", StringComparison.Ordinal) && fromEditor.Contains("Shared.h@Game") && fromEditor.Contains("Tool.cpp@Tools"),
            "요청한 파일의 프로젝트에 없으면 목록 순서상 첫 프로젝트에: " + fromEditor);
    }

    private static void Layout()
    {
        // 소속 이름은 바깥 단계의 마지막 이름 마디만 보입니다. 템플릿 인수 안의 ::로는 나누지 않습니다.
        string Last(string name) => NavigationResultItem.LastSegment(name);
        Assert(Last("Game::Tick") == "Tick" && Last("Tick") == "Tick" && Last(string.Empty) == string.Empty && Last("::Run") == "Run" &&
               Last("ns::TMap<a::B, c::D>") == "TMap<a::B, c::D>" && Last("ns::Outer<a::B>::Inner") == "Inner" &&
               Last("(anonymous namespace)::Helper") == "Helper", "소속 짧은 이름");
        var tick = Item(Use, 8, "Game::Tick", "    Total += FMod::Compute(Delta);");
        Assert(tick.ContainerShortName == "Tick" && tick.Container == "Game::Tick" && Item(Use, 20, string.Empty, "x").ContainerShortName.Length == 0,
            "위치 항목의 소속 짧은 이름·전체 이름");

        // 줄 번호는 결과 전체의 최대 자릿수로 앞을 숫자 폭 공백으로 채웁니다. 필터로 줄어도 자릿수는 그대로입니다.
        var model = AllProjectsModel();
        model.Show(Sample("FMod::Compute"));
        string Labels() => string.Join(",", model.Rows.OfType<ReferenceLineRow>().Select(row => row.LineLabel.Replace('\u2007', '_')));
        Assert(Labels() == "_3,_8,20,_5,_2", "줄 번호 앞 채움: " + Labels());
        model.Filter = "tick";
        Assert(Labels() == "_8", "필터 뒤에도 같은 자릿수: " + Labels());
        Assert(ReferenceRowText.LineLabel("7", 0) == "7" && ReferenceRowText.LineLabel("123", 2) == "123", "자릿수보다 긴 줄 번호는 그대로");

        // 소속 이름의 색은 이름 인덱스가 타입·함수·네임스페이스 한 그룹으로 판정할 때만 칠하고, 다른 판정이나 모르는 이름은 추측하지 않습니다.
        try
        {
            CodePreviewStyle.NameKind = name => name switch
            {
                "Tick" => CodePreviewKind.Function,
                "FMod" => CodePreviewKind.Type,
                "Game" => CodePreviewKind.Namespace,
                "Value" => CodePreviewKind.Variable,
                _ => null,
            };
            CodePreviewKind? Kind(string container) => Item(Use, 1, container, "Compute();").ContainerKind;
            Assert(Kind("Game::Tick") == CodePreviewKind.Function && Kind("FMod") == CodePreviewKind.Type && Kind("Game") == CodePreviewKind.Namespace &&
                   Kind("Value") is null && Kind("Unknown") is null && Kind(string.Empty) is null, "소속 이름 종류 판정");
            var cached = Item(Use, 1, "Game::Tick", "Compute();");
            _ = cached.ContainerKind;
            CodePreviewStyle.NameKind = _ => null;
            Assert(cached.ContainerKind == CodePreviewKind.Function, "항목마다 한 번 판정");
        }
        finally
        {
            CodePreviewStyle.NameKind = null;
        }

        // 선택 행·종류 모름에서는 색을 지워 행 전경색을 상속합니다. 바탕이 없으면 어두운 바탕 색을 씁니다.
        if (!SystemParameters.HighContrast)
        {
            var text = new TextBlock();
            KindForeground.SetKind(text, CodePreviewKind.Function);
            Assert(CodePreviewStyle.BrushFor(CodePreviewKind.Function, true) is { } dark && ReferenceEquals(text.Foreground, dark), "소속 이름 함수 색");
            KindForeground.SetSurface(text, Brushes.White);
            Assert(ReferenceEquals(text.Foreground, CodePreviewStyle.BrushFor(CodePreviewKind.Function, false)), "밝은 바탕 색");
            KindForeground.SetPlain(text, true);
            Assert(text.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue, "선택 행은 색 없음");
            KindForeground.SetPlain(text, false);
            KindForeground.SetKind(text, null);
            Assert(text.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue, "종류 모름은 색 없음");
        }
    }

    private static void Menu()
    {
        var model = AllProjectsModel();
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
        var model = AllProjectsModel();
        var set = Sample("FMod::Compute");
        model.Show(set);
        Assert(ReferenceMenu.Location(set.Items[1]) == Use + "(8)", "위치 복사는 전체경로(줄): " + ReferenceMenu.Location(set.Items[1]));
        model.Filter = "tick";
        Assert(ReferenceMenu.Lines(model.VisibleItems) == Use + "(8): Total += FMod::Compute(Delta);", "보이는 결과 복사: " + ReferenceMenu.Lines(model.VisibleItems));
        model.Filter = string.Empty;
        model.SetAllExpanded(false);
        Assert(string.Join(",", model.VisibleItems.Select(item => item.FileName + ":" + item.Line)) == "Use.cpp:3,Use.cpp:8,Use.cpp:20,Mod.h:5,Other.cpp:2",
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
        var model = AllProjectsModel();
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

        // 크기는 VS 환경 글꼴 × 옵션 비율(기본 100%, 80~200%)입니다. PC마다 다른 환경 글꼴을 그대로 따르고 사용자가 키울 수 있습니다.
        Assert(ResultListFont.DefaultPercent == 100 && ResultListFont.Clamp(50) == 80 && ResultListFont.Clamp(500) == 200 &&
               ResultListFont.Size(12, 100) == 12 && ResultListFont.Size(12, 125) == 15 && ResultListFont.Size(12, 10) == 9.6, "목록 글씨 크기 비율·범위");

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

    /// <summary>행·열 머리글과 글자를 담는 입력·버튼은 최소 높이만 둡니다. 고정 높이는 큰 환경 글꼴에서 글자를 자릅니다.</summary>
    private static void FixedHeights(XDocument document, string name)
    {
        var fixedRows = document.Descendants()
            .Where(element => element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") is "ListViewItem" or "ListBoxItem" or "GridViewColumnHeader")
            .SelectMany(style => style.Elements())
            .Where(element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "Height").ToArray();
        Assert(fixedRows.Length == 0, name + ": 행·머리글 고정 높이 없음");
        var fixedText = document.Descendants()
            .Where(element => element.Name.LocalName is "TextBox" or "TextBlock" or "ComboBox" or "Button" or "CheckBox" && element.Attribute("Height") is not null)
            .Select(element => element.Name.LocalName).ToArray();
        Assert(fixedText.Length == 0, name + ": 글자 요소 고정 높이 없음: " + string.Join(",", fixedText));
    }

    private static void Xaml(string root, string output)
    {
        var element = Program.LoadXaml(root, "ReferencesControl");
        var window = new Window { Content = element, Width = 1000, Height = 360, Left = -20000, ShowInTaskbar = false };
        var model = AllProjectsModel();
        // 소속 이름 색을 보려고 이름 판정을 잠시 연결합니다. 항목은 처음 읽을 때 한 번 판정하므로 결과를 만들기 전에 연결합니다.
        CodePreviewStyle.NameKind = name => name is "Tick" or "Use" ? CodePreviewKind.Function : null;
        model.Show(Sample("FMod::Compute"));
        model.SetProjects(Catalog(withProject: true));
        _ = model.Rows.OfType<ReferenceLineRow>().Select(row => row.ContainerKind).ToArray();
        CodePreviewStyle.NameKind = null;
        var list = (ListBox)element.FindName("ResultsList");
        ((TextBlock)element.FindName("SymbolText")).Text = model.Current!.Symbol;
        ((TextBlock)element.FindName("SummaryText")).Text = model.Summary;
        ((UIElement)element.FindName("EmptyText")).Visibility = Visibility.Collapsed;
        var currentButton = (RadioButton)element.FindName("CurrentProjectButton");
        var allButton = (RadioButton)element.FindName("AllProjectsButton");
        allButton.IsChecked = true;
        list.ItemsSource = model.Rows;
        list.SelectedIndex = 2;
        window.Show();
        Program.Pump();

        // 머리 줄에는 묶기 체크박스·펼치기/접기 버튼 없이 필터·범위 모드 아이콘 버튼 둘·최근 결과만 둡니다.
        Assert(!Program.Descendants<CheckBox>(element).Any() && !Program.Descendants<Button>(element).Any(), "머리 줄에 묶기·펼치기·접기 컨트롤 없음");
        Assert(Program.Descendants<ComboBox>(element).Select(box => box.Name).SequenceEqual(new[] { "HistoryBox" }), "프로젝트 목록 콤보 상자 없음");
        Assert(Program.Descendants<RadioButton>(element).Select(button => button.Name).SequenceEqual(new[] { "CurrentProjectButton", "AllProjectsButton" }) &&
               currentButton.GroupName == allButton.GroupName && currentButton.IsChecked == false && allButton.IsChecked == true, "범위 모드 버튼 둘");
        Assert((string)currentButton.ToolTip == ReferenceResultsModel.CurrentProjectTip && (string)allButton.ToolTip == ReferenceResultsModel.AllProjectsTip,
            "호버 툴팁: '현재 프로젝트 탐색'·'모든 프로젝트 탐색'");
        Assert(ReferenceEquals(Program.Descendants<ProductIcon>(currentButton).Single().Source, ProductIcons.Symbol("project")) &&
               ReferenceEquals(Program.Descendants<ProductIcon>(allButton).Single().Source, ProductIcons.Symbol("projects")) &&
               !ReferenceEquals(ProductIcons.Symbol("project"), ProductIcons.Symbol("unknown")) && !ReferenceEquals(ProductIcons.Symbol("projects"), ProductIcons.Symbol("unknown")),
            "범위 모드 버튼은 제품 아이콘");
        Assert(list.ContextMenu is not null && ((TextBlock)element.FindName("EmptyText")).IsHitTestVisible == false, "빈 안내 위에서도 목록 우클릭 메뉴");

        var items = Program.Descendants<ListBoxItem>(list).ToArray();
        Assert(items.Length == model.Rows.Count, "행 컨테이너 수");
        var texts = Program.Descendants<TextBlock>(list).Where(text => text.IsVisible).ToArray();
        Assert(texts.Any(text => text.Text == "Use.cpp" && text.FontWeight == FontWeights.SemiBold) && texts.Any(text => text.Text == "3"), "파일 머리 행: 이름·위치 수");
        var tickText = texts.Single(text => text.Name == "ContainerText" && text.Text == "Tick");
        Assert((string)tickText.ToolTip == "Game::Tick" && !texts.Any(text => text.Text == "Game::Tick"), "위치 행: 소속 짧은 이름, 전체 이름은 툴팁");
        Assert(items.Where(item => item.DataContext is ReferenceLineRow).All(item => Program.Descendants<TextBlock>(item).All(text => text.Text != "Use.cpp" || !text.IsVisible)),
            "위치 행에는 파일 이름을 다시 보이지 않음");
        var codes = texts.Where(text => text.Name == "CodeText").ToArray();
        var code = codes.First(text => Joined(text) == "return FMod::Compute(1);");
        Assert(Bold(code) == "Compute", "코드 미리보기 일치 구간만 굵게");
        Assert(codes.All(text => Bold(text) == "Compute"), "모든 위치 행의 일치 구간 굵게");
        // 한 화면에 위치를 많이 보이도록 행은 글자(파일 행은 아이콘 포함) 높이에 위아래 1 DIP 여백만 둡니다. 글씨 크기를 줄이면 행도 함께 줄어듭니다.
        bool Fits(ListBoxItem item) => item.ActualHeight <=
            Program.Descendants<FrameworkElement>(item).Where(part => part is TextBlock || part is Image).Max(part => part.ActualHeight) + 2.5;
        var lineItems = items.Where(item => item.DataContext is ReferenceLineRow).ToArray();
        Assert(lineItems.All(item => item.ActualHeight < 22 && Fits(item)), "위치 행 높이: " + string.Join(",", lineItems.Select(item => item.ActualHeight)));
        var fileItems = items.Where(item => item.DataContext is ReferenceFileRow).ToArray();
        Assert(fileItems.All(item => item.ActualHeight < 24 && Fits(item)), "파일 머리 행 높이: " + string.Join(",", fileItems.Select(item => item.ActualHeight)));
        Assert(fileItems.All(item => Program.Descendants<ProductIcon>(item).Single() is { Name: "FileIcon" } icon && icon.IsVisible &&
                                     ReferenceEquals(icon.Source, ProductIcons.File(((ReferenceFileRow)item.DataContext).FullPath))) &&
               lineItems.All(item => !Program.Descendants<ProductIcon>(item).Any()), "파일 머리 행 앞에 파일 형식 제품 아이콘");
        var noContainer = items.First(item => item.DataContext is ReferenceLineRow { Container: "" });
        Assert(Program.Descendants<TextBlock>(noContainer).All(text => text.Name != "ContainerText" || !text.IsVisible), "포함 함수가 없으면 숨김");

        // 프로젝트 머리 행: 펼침 표시 · 프로젝트 아이콘(프로젝트 밖은 미확인 아이콘) · 굵은 이름 · 위치 수. 파일 행은 그 아래 한 단계 들여씁니다.
        var projectItems = items.Where(item => item.DataContext is ReferenceProjectRow).ToArray();
        Assert(projectItems.Length == 2 && projectItems.All(item => item.ActualHeight < 24 && Fits(item)),
            "프로젝트 머리 행 높이: " + string.Join(",", projectItems.Select(item => item.ActualHeight)));
        Assert(projectItems.All(item => Program.Descendants<ProductIcon>(item).Single() is { Name: "ProjectIcon" } icon && icon.IsVisible &&
                                        ReferenceEquals(icon.Source, ProductIcons.Symbol(((ReferenceProjectRow)item.DataContext).IsOutside ? "unknown" : "project"))),
            "프로젝트 머리 행 아이콘");
        Assert(texts.Any(text => text.Text == "Game" && text.FontWeight == FontWeights.Bold) &&
               texts.Any(text => text.Text == ReferenceResultsModel.OutsideName && text.FontWeight == FontWeights.Bold) && texts.Any(text => text.Text == "4"),
            "프로젝트 머리 행: 굵은 이름·위치 수");
        double X(FrameworkElement part) => part.TranslatePoint(new Point(0, 0), list).X;
        FrameworkElement Part(ListBoxItem item, string name) => Program.Descendants<FrameworkElement>(item).Single(part => part.Name == name);
        Assert(Math.Abs(X(Part(fileItems[0], "FileIcon")) - X(Part(projectItems[0], "ProjectIcon")) - ReferenceRowText.LevelWidth) < 0.5,
            "묶이면 파일 머리 행을 한 단계 들여씀");

        // 위치 행: 줄 번호 → 소속 이름 → 코드. 줄 번호는 파일 아이콘 열에서 시작하고 오른쪽 끝이 맞습니다.
        Assert(lineItems.All(item => Math.Abs(X(Part(item, "LineText")) - X(Part(fileItems[0], "FileIcon"))) < 0.5), "줄 번호는 파일 아이콘 열에서 시작");
        var lineRights = lineItems.Select(item => X(Part(item, "LineText")) + Part(item, "LineText").ActualWidth).ToArray();
        Assert(lineRights.Max() - lineRights.Min() < 0.6, "줄 번호 오른쪽 끝 맞춤: " + string.Join(",", lineRights));
        Assert(lineItems.Where(item => ((ReferenceLineRow)item.DataContext).ContainerShortName.Length > 0)
                .All(item => X(Part(item, "LineText")) < X(Part(item, "ContainerText")) && X(Part(item, "ContainerText")) < X(Part(item, "CodeText"))),
            "줄 번호 → 소속 이름 → 코드 순서");
        Assert(X(Part(noContainer, "CodeText")) < X(Part(lineItems.First(item => item.DataContext is ReferenceLineRow { Container: "Game::Tick" }), "CodeText")),
            "소속 이름이 없으면 빈 칸 없이 코드가 붙음");

        // 역할 표식은 줄 번호와 소속 이름 사이에 두고, 소속 이름과 코드 사이에는 세로선을 둡니다. 소속·역할이 없으면 둘 다 접습니다.
        var declared = lineItems.Single(item => item.DataContext is ReferenceLineRow { RoleText: "선언" });
        Assert(Part(declared, "RoleBadge").IsVisible && X(Part(declared, "LineText")) < X(Part(declared, "RoleBadge")) &&
               X(Part(declared, "RoleBadge")) < X(Part(declared, "ContainerText")) && X(Part(declared, "ContainerText")) < X(Part(declared, "ContainerSeparator")) &&
               X(Part(declared, "ContainerSeparator")) < X(Part(declared, "CodeText")) && texts.Any(text => text.Name == "RoleText" && text.Text == "선언"),
            "줄 번호 → 역할 표식 → 소속 이름 → 세로선 → 코드");
        var separator = Part(declared, "ContainerSeparator");
        Assert(separator.ActualWidth == 1 && separator.ActualHeight > 6 && declared.ActualHeight < 22 && Fits(declared), "세로선은 글자 높이 안, 표식이 있어도 행 높이 그대로");
        Assert(lineItems.Where(item => item != declared).All(item => !Part(item, "RoleBadge").IsVisible) && !Part(noContainer, "ContainerSeparator").IsVisible &&
               lineItems.Where(item => item != noContainer).All(item => Part(item, "ContainerSeparator").IsVisible), "역할·소속이 없으면 표식·세로선 접기");
        if (!SystemParameters.HighContrast)
        {
            var dark = KindForeground.GetSurface(tickText) is not SolidColorBrush surface ||
                       SemanticColorPalette.IsDark(surface.Color.R, surface.Color.G, surface.Color.B);
            Assert(CodePreviewStyle.BrushFor(CodePreviewKind.Function, dark) is { } function && ReferenceEquals(tickText.Foreground, function), "소속 이름 함수 색");
            var selected = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex);
            Assert(selected.DataContext is ReferenceLineRow { ContainerKind: CodePreviewKind.Function } &&
                   Part(selected, "ContainerText").ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue, "선택 행 소속 이름은 행 전경색");
        }

        Save(window, Path.Combine(output, "ReferencesControl.png"));

        // 현재 프로젝트에 위치가 없으면 빈 안내를 보입니다.
        model.SetMode(ReferenceScopeMode.CurrentProject);
        model.Show(new ReferenceResultSet("FMod::Only", new[] { Item(Header, 5, "FMod", "    static int Compute(int Value);") }, string.Empty, DateTime.Now, Use));
        list.ItemsSource = model.Rows;
        currentButton.IsChecked = true;
        ((TextBlock)element.FindName("SymbolText")).Text = model.Current!.Symbol;
        ((TextBlock)element.FindName("SummaryText")).Text = model.Summary;
        var empty = (TextBlock)element.FindName("EmptyText");
        empty.Text = model.EmptyMessage;
        empty.Visibility = Visibility.Visible;
        Program.Pump();
        Assert(!Program.Descendants<ListBoxItem>(list).Any() && empty.IsVisible && empty.ActualWidth > 0, "빈 범위 안내 표시");
        Save(window, Path.Combine(output, "ReferencesControl-empty-scope.png"));
        window.Close();
        Program.Pump();
    }

    /// <summary>모든 프로젝트 모드로 시작한 모델입니다. 기본(현재 프로젝트) 모드와 무관한 표시 규칙을 검증할 때 씁니다.</summary>
    /// <summary>필터 전체가 표식 글자와 같을 때만 역할로 거르고, 그 밖에는 글자 검색만 합니다(2026-10-07 검토).</summary>
    private static void RoleFilter()
    {
        var model = AllProjectsModel();
        model.Show(new ReferenceResultSet("F", new[]
        {
            Item(Use, 3, "Use", "int F() { return 1; }", NavigationRole.Definition),
            Item(Use, 9, "Use", "// F의 정의는 위에 있습니다", NavigationRole.None),
            Item(Other, 2, "Other", "return F();"),
        }, string.Empty, DateTime.Now, Use));
        model.Filter = " 정의 ";
        Assert(model.VisibleCount == 1 && model.VisibleItems[0].Role == NavigationRole.Definition, "역할 필터는 코드에 '정의'가 든 줄을 남기지 않음");
        model.Filter = "정";
        Assert(model.VisibleCount == 1 && model.VisibleItems[0].Role == NavigationRole.None, "표식 글자 일부는 코드 글자 검색");
        model.Filter = "선언";
        Assert(model.VisibleCount == 0 && model.EmptyMessage == ReferenceResultsModel.NoRoleMatchMessage, "역할 위치가 없으면 표식 안내");
        model.Filter = "없음";
        Assert(model.EmptyMessage == ReferenceResultsModel.NoMatchMessage, "글자 검색 결과가 없으면 일반 안내");
        Assert(Item(Use, 9, "Use", "// 정의", NavigationRole.None).Matches("정의"), "정의 후보 창 등 다른 목록의 글자 검색은 그대로");
    }

    private static ReferenceResultsModel AllProjectsModel()
    {
        var model = new ReferenceResultsModel();
        model.SetMode(ReferenceScopeMode.AllProjects);
        return model;
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

    /// <summary>"Compute"가 있으면 그 이름을, 없으면 줄 앞을 일치 구간으로 둔 위치입니다.</summary>
    private static NavigationResultItem Item(string path, int line, string container, string text, NavigationRole role = NavigationRole.None)
    {
        var at = text.IndexOf("Compute", StringComparison.Ordinal);
        var start = Math.Max(0, at);
        return new(new NavigationLocation(path, line - 1, start, line - 1, at < 0 ? start : at + "Compute".Length, container), text, Root, role: role);
    }

    /// <summary>참조 결과 예시입니다. 요청한 파일은 기본으로 Use.cpp입니다.</summary>
    private static ReferenceResultSet Sample(string symbol, string? origin = null)
    {
        // 같은 파일 위치가 떨어져 있어도 첫 위치 기준으로 묶이는지 보기 위해 Use.cpp를 앞뒤로 나눕니다.
        var items = new[]
        {
            Item(Use, 3, "Use", "    return FMod::Compute(1);"),
            Item(Use, 8, "Game::Tick", "    Total += FMod::Compute(Delta);"),
            Item(Other, 2, "Other", "int Other() { return FMod::Compute(2); }"),
            Item(Use, 20, string.Empty, "static int Value = FMod::Compute(3);"),
            Item(Header, 5, "FMod", "    static int Compute(int Value);", NavigationRole.Declaration),
        };
        return new ReferenceResultSet(symbol, items, "색인 진행 중", new DateTime(2026, 10, 5, 21, 7, 0), origin ?? Use);
    }

    private static string Shape(ReferenceResultsModel model) => string.Join(" ", model.Rows.Select(row => row switch
    {
        ReferenceProjectRow project => $"P:{project.Name}({project.Count})",
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
