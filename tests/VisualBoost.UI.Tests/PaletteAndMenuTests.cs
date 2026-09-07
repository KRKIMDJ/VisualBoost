using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using VisualBoost.Options;

internal static class PaletteAndMenuTests
{
    public static void Run(string root)
    {
        var context = new TestContext();
        var editor = new TestEditor();
        Assert(editor.GetEditStyle(context) == UITypeEditorEditStyle.Modal, "색상 선택 창 등록");
        Assert(editor.GetPaintValueSupported(context), "색상 미리보기 등록");
        Assert((string?)editor.EditValue(context, null, "") == "", "선택 취소 시 기본색 설정 유지");
        Assert(editor.Initial.ToArgb() == Color.FromArgb(0x68, 0xD5, 0xC4).ToArgb(), "기본 팔레트로 선택 창 초기화");
        Assert((string?)editor.EditValue(context, null, "#123456") == "#123456", "선택 취소 시 저장된 값 유지");
        Assert(editor.Initial.ToArgb() == Color.FromArgb(0x12, 0x34, 0x56).ToArgb(), "저장된 색상으로 초기화");
        editor.Selection = Color.FromArgb(0, 128, 255);
        Assert((string?)editor.EditValue(context, null, "") == "#0080FF", "선택 색상을 기존 저장 형식으로 변환");
        using (var bitmap = new Bitmap(16, 16))
        {
            using (var graphics = Graphics.FromImage(bitmap))
                editor.PaintValue(new PaintValueEventArgs(context, "", graphics, new Rectangle(0, 0, 16, 16)));
            Assert(bitmap.GetPixel(8, 8).ToArgb() == Color.FromArgb(0x68, 0xD5, 0xC4).ToArgb(), "기본색 견본 표시");
        }

        var xml = XDocument.Load(Path.Combine(root, "src", "VisualBoost.Package", "Commands.vsct"));
        XNamespace ns = xml.Root!.Name.Namespace;
        var commands = xml.Root.Element(ns + "Commands")!;
        var menu = commands.Element(ns + "Menus")!.Elements(ns + "Menu").Single(m => (string?)m.Attribute("id") == "VisualBoostSubmenu");
        Assert((string?)menu.Attribute("id") == "VisualBoostSubmenu" && (string?)menu.Element(ns + "Strings")!.Element(ns + "ButtonText") == "Visual Boost", "하위 메뉴 제목");
        var groups = commands.Element(ns + "Groups")!.Elements(ns + "Group").ToDictionary(g => (string)g.Attribute("id")!);
        Assert(groups.Values.Count(g => (string?)g.Element(ns + "Parent")!.Attribute("id") == "IDM_VS_MENU_TOOLS") == 1, "도구 메뉴 직접 그룹은 하나");
        Assert((string?)menu.Element(ns + "Parent")!.Attribute("id") == "VisualBoostToolsGroup", "하위 메뉴를 도구 그룹에 연결");
        foreach (var button in commands.Element(ns + "Buttons")!.Elements(ns + "Button"))
        {
            var group = groups[(string)button.Element(ns + "Parent")!.Attribute("id")!];
            if ((string?)group.Attribute("id") is "VisualBoostActionPopupGroup" or "VisualBoostDeclarationPopupGroup") continue;
            Assert((string?)group.Element(ns + "Parent")!.Attribute("id") == "VisualBoostSubmenu", "모든 명령은 하위 메뉴 안에 배치");
            Assert(((string?)button.Element(ns + "Strings")!.Element(ns + "CanonicalName"))?.StartsWith("VisualBoost.", StringComparison.Ordinal) == true, "정규 명령 이름 유지");
        }
        var expected = new[] { "GenerateFunctionCommand|GUID_TextEditorFactory|Shift Alt|Q", "OpenDocumentMembersCommand|GUID_TextEditorFactory|ALT|M", "SwitchHeaderSourceCommand|guidVSStd97|ALT|O", "OpenFileSearchCommand|GUID_TextEditorFactory|Shift Alt|O",
            "OpenSymbolSearchCommand|GUID_TextEditorFactory|Shift Alt|S", "FindSymbolUsagesCommand|GUID_TextEditorFactory|Shift Alt|F",
            "NavigateToDefinitionCommand|GUID_TextEditorFactory|ALT|G" };
        var actual = xml.Root.Element(ns + "KeyBindings")!.Elements(ns + "KeyBinding").Select(k =>
            string.Join("|", new[] { "id", "editor", "mod1", "key1" }.Select(name => (string?)k.Attribute(name))));
        Assert(actual.SequenceEqual(expected), "기존 단축키와 적용 범위 유지");
        var navigationFolder = Path.Combine(root, "src", "VisualBoost.Package", "DocumentNavigation");
        Assert(Directory.GetFiles(navigationFolder, "*.cs").Any(file => File.ReadAllText(file).Contains("IWpfTextViewMarginProvider")), "공개 상단 margin 등록");
        var navigationView = XDocument.Load(Path.Combine(navigationFolder, "DocumentNavigationControl.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert(navigationView.Descendants().Any(node => (string?)node.Attribute(x + "Name") == "Search"), "상단 줄과 별개로 숨김 모드 검색란 유지");
        var navigationOptions = File.ReadAllText(Path.Combine(root, "src", "VisualBoost.Package", "Options", "DocumentNavigationOptionsPage.cs"));
        Assert(navigationOptions.Contains("ShowBar") && navigationOptions.Contains("NameOrder"), "상단 줄 표시·정렬 설정 유지");
        var codeSearch = commands.Element(ns + "Buttons")!.Elements(ns + "Button")
            .Single(button => (string?)button.Attribute("id") == "FindSymbolUsagesCommand");
        Assert((string?)codeSearch.Element(ns + "Strings")!.Element(ns + "ButtonText") == "코드 검색", "코드 검색 메뉴 표시 이름");
        var view = XDocument.Load(Path.Combine(root, "src", "VisualBoost.Package", "UI", "SymbolUsagesControl.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        Assert(view.Descendants(wpf + "TextBlock").Any(block => (string?)block.Attribute("Text") == "심볼:"), "코드 검색 상단 심볼 라벨");
        var pane = File.ReadAllText(Path.Combine(root, "src", "VisualBoost.Package", "UI", "SymbolUsagesToolWindow.cs"));
        Assert(pane.Contains("Caption = \"VisualBoost 코드 검색\""), "코드 검색 도킹 창 제목");
        Assert(commands.Element(ns + "Buttons")!.Elements(ns + "Button").Count(b => b.Element(ns + "Strings")?.Element(ns + "CanonicalName") is not null) == 9, "내부 메뉴 항목과 별개로 사용자 명령 아홉 개 유지");
        var placement = xml.Root.Element(ns + "CommandPlacements")!.Elements(ns + "CommandPlacement").Single();
        Assert((string?)placement.Attribute("id") == "GenerateFunctionCommand" && (string?)placement.Element(ns + "Parent")!.Attribute("id") == "VisualBoostGenerationContextGroup", "코드 편집기 메뉴가 같은 생성 명령을 재사용");
        var generationCommand = File.ReadAllText(Path.Combine(root, "src", "VisualBoost.Package", "Commands", "GenerateFunctionCommand.cs"));
        Assert(!generationCommand.Contains("ShowModal") && !generationCommand.Contains("ShowMessageBox") && generationCommand.Contains("paths[0]") && generationCommand.Contains("SystemSounds.Beep.Play"), "파일 선택·미리보기·이동 질문 제거, 1순위 자동 대상과 알림음");
        var package = File.ReadAllText(Path.Combine(root, "src", "VisualBoost.Package", "VisualBoostPackage.cs"));
        Assert(package.Contains("ProvideToolWindow") && !package.Contains("PrecisionSearchOptionsPage") && !package.Contains("SemanticIndex"), "이름 검색 창만 등록하고 정밀 옵션·인덱스는 제외");
        var project = File.ReadAllText(Path.Combine(root, "src", "VisualBoost.Package", "VisualBoost.Package.csproj"));
        Assert(!project.Contains("Clang") && !project.Contains("CodeAnalysis") && !project.Contains("LanguageServices"), "정밀 분석기 직접 의존성 재도입 방지");
        Console.WriteLine("PASS: 색상 선택·취소·기존 설정·견본 및 도구 하위 메뉴·단축키 검증");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestEditor : PaletteColorEditor
    {
        public Color Initial { get; private set; }
        public Color? Selection { get; set; }
        protected override Color? PickColor(Color initial, IServiceProvider? provider) { Initial = initial; return Selection; }
    }

    private sealed class Sample
    {
        [PaletteDefault("#68D5C4")]
        public string ColorValue { get; set; } = "";
    }

    private sealed class TestContext : ITypeDescriptorContext
    {
        public IContainer? Container => null;
        public object Instance { get; } = new Sample();
        public PropertyDescriptor PropertyDescriptor => TypeDescriptor.GetProperties(typeof(Sample))[nameof(Sample.ColorValue)]!;
        public object? GetService(Type serviceType) => null;
        public bool OnComponentChanging() => true;
        public void OnComponentChanged() { }
    }
}
