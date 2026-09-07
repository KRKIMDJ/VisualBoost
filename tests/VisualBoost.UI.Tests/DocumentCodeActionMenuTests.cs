using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Microsoft.VisualStudio.OLE.Interop;
using VisualBoost.CodeGeneration;
using VisualBoost.Core.CodeGeneration;

internal static class DocumentCodeActionMenuTests
{
    public static void Run(string output)
    {
        var context = new GenerationModelContext { Function = new("Reset", "Widget", 0, 0, 10, false) };
        var actions = GenerationCodeActions.Create(context, "Widget.cpp");
        Check(actions.Count == 1 && actions[0].Title == "정의 생성", "정의 생성 노출");
        context.ExistingPath = "Widget.cpp";
        var empty = GenerationCodeActions.Create(context, "Widget.cpp");
        Check(empty.Count == 0, "대응 코드가 있으면 빈 도구");
        context.ExistingPath = ""; context.Function = new("Reset", "Widget", 0, 0, 10, true);
        var declaration = GenerationCodeActions.Create(context, "Widget.h");
        Check(declaration[0].Children.Select(c => c.Id).SequenceEqual(new[] { "public", "protected", "private" }), "접근 수준 유지");
        var anchor = new Border { Width = 700, Height = 300 };
        var window = new Window { Content = anchor, Width = 720, Height = 340, Left = -20000, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        try
        {
            for (var i = 0; i < 30; i++)
            {
                var select = i % 2 == 0;
                NativeCodeActionMenuTarget? captured = null;
                var host = new TestHost((screen, target, token) =>
                {
                    captured = target;
                    Check(screen == anchor.PointToScreen(new Point(70, 30)), "캐럿 화면 좌표·음수 모니터 좌표");
                    Check(Status(target, NativeCodeActionMenuTarget.ItemStart) == 3, "실행 항목 활성화");
                    Check(Status(target, NativeCodeActionMenuTarget.DeclarationMenuId) == 17, "없는 하위 메뉴 숨김");
                    var group = NativeCodeActionMenuTarget.CommandSet;
                    var last = new[] { new OLECMD { cmdID = NativeCodeActionMenuTarget.ItemStart + 1 } };
                    Check(target.QueryStatus(ref group, 1, last, IntPtr.Zero) < 0 && last[0].cmdf == 0, "동적 열거 종료");
                    if (select) { Check(Execute(target, NativeCodeActionMenuTarget.ItemStart) == 0, "선택 전달"); Check(Execute(target, NativeCodeActionMenuTarget.ItemStart) < 0, "중복 차단"); }
                });
                var result = new DocumentCodeActionMenu(host).ShowAsync(anchor, new(70, 30), actions, CancellationToken.None).GetAwaiter().GetResult();
                Check(result == (select ? actions[0] : null), "선택/취소 반환");
                Check(captured!.Selected is null && Execute(captured, NativeCodeActionMenuTarget.ItemStart) < 0, "종료 후 참조 해제·늦은 실행 차단");
            }
            var childHost = new TestHost((_, target, _) =>
            {
                Check(Status(target, NativeCodeActionMenuTarget.ItemStart) == 17, "빈 루트 숨김");
                Check(Status(target, NativeCodeActionMenuTarget.DeclarationMenuId) == 3, "선언 하위 메뉴");
                Check(Execute(target, NativeCodeActionMenuTarget.DeclarationMenuId) < 0, "메뉴 자체는 편집하지 않음");
                Execute(target, NativeCodeActionMenuTarget.ChildStart + 1);
            });
            Check(new DocumentCodeActionMenu(childHost).ShowAsync(anchor, new(), declaration, CancellationToken.None).Result?.Id == "protected", "접근 수준 반환");
            var include = QuickIncludeCodeActions.Create("Widget", @"C:\Test\Widget.h", "Widget.h");
            Check(include.Children.Count == 0, "빠른 include 1순위 직접 실행");
            using var cancel = new CancellationTokenSource();
            var cancelHost = new TestHost((_, target, _) => { cancel.Cancel(); Check(Execute(target, NativeCodeActionMenuTarget.ItemStart) < 0, "취소 후 실행 차단"); });
            Check(new DocumentCodeActionMenu(cancelHost).ShowAsync(anchor, new(), new[] { include }, cancel.Token).Result is null, "문서 변경/종료 취소");
            var noShow = new TestHost((_, _, _) => throw new Exception("메뉴를 열면 안 됩니다."));
            Check(new DocumentCodeActionMenu(noShow).ShowAsync(anchor, new(), empty, CancellationToken.None).Result is null, "빈 메뉴 억제");
            Check(new DocumentCodeActionMenu(noShow).ShowAsync(anchor, new(), actions, cancel.Token).Result is null, "사전 취소 억제");
            var failed = new TestHost((_, _, _) => throw new InvalidOperationException("Shell failure"));
            try { new DocumentCodeActionMenu(failed).ShowAsync(anchor, new(), actions, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("실패 누락"); }
            catch (InvalidOperationException e) when (e.Message == "Shell failure") { }
            using var target = new NativeCodeActionMenuTarget(new[] { new DocumentCodeAction("include", "A&B_한글.h", "전체 경로 안내") }, CancellationToken.None);
            Check(Text(target, 1, 100) == "A&&B_한글.h", "밑줄·한글 유지 및 & 이스케이프");
            Check(Text(target, 2, 100) == "전체 경로 안내", "상태 설명 공급");
            Check(Text(target, 1, 4) == "A&&" && Text(target, 1, 1) == "", "문자 버퍼 상한");
            var foreign = Guid.NewGuid();
            Check(target.Exec(ref foreign, NativeCodeActionMenuTarget.ItemStart, 0, IntPtr.Zero, IntPtr.Zero) < 0, "다른 명령 그룹 무간섭");
        }
        finally { window.Close(); }
        ValidateRegistration();
        Console.WriteLine("PASS: VS 기본 메뉴 명령 대상·동적 열거·접근 수준·문자 버퍼·30회 선택/취소·수명·VSCT 등록 (Shell 표시는 대역)");
    }
    private static void ValidateRegistration()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/VisualBoost.Package/Commands.vsct"))) root = root.Parent;
        Check(root is not null, "실제 VSCT 확인");
        var doc = XDocument.Load(Path.Combine(root!.FullName, "src/VisualBoost.Package/Commands.vsct"));
        XNamespace ns = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
        Check(doc.Descendants(ns + "Menu").Any(e => (string?)e.Attribute("id") == "VisualBoostActionPopup" && (string?)e.Attribute("type") == "Context"), "Shell 메뉴 등록");
        foreach (var item in new[] { ("VisualBoostActionPopup", NativeCodeActionMenuTarget.MenuId), ("VisualBoostDeclarationPopup", NativeCodeActionMenuTarget.DeclarationMenuId), ("VisualBoostActionItemStart", (int)NativeCodeActionMenuTarget.ItemStart), ("VisualBoostDeclarationItemStart", (int)NativeCodeActionMenuTarget.ChildStart) })
            Check(Convert.ToInt32((string)doc.Descendants(ns + "IDSymbol").Single(e => (string?)e.Attribute("name") == item.Item1).Attribute("value")!, 16) == item.Item2, "VSCT와 런타임 ID 일치");
        foreach (var id in new[] { "VisualBoostActionItemStart", "VisualBoostDeclarationItemStart" })
            Check(doc.Descendants(ns + "Button").Single(e => (string?)e.Attribute("id") == id).Elements(ns + "CommandFlag").Select(e => e.Value).Contains("DynamicItemStart"), "동적 시작 등록");
        Check(!File.Exists(Path.Combine(root.FullName, "src/VisualBoost.Package/CodeGeneration/DocumentCodeActionMenuStyles.xaml")), "자체 템플릿 제거");
    }
    private static uint Status(NativeCodeActionMenuTarget target, uint id) { var group = NativeCodeActionMenuTarget.CommandSet; var commands = new[] { new OLECMD { cmdID = id } }; target.QueryStatus(ref group, 1, commands, IntPtr.Zero); return commands[0].cmdf; }
    private static int Execute(NativeCodeActionMenuTarget target, uint id) { var group = NativeCodeActionMenuTarget.CommandSet; return target.Exec(ref group, id, 0, IntPtr.Zero, IntPtr.Zero); }
    private static string Text(NativeCodeActionMenuTarget target, int flags, int capacity)
    {
        var memory = Marshal.AllocCoTaskMem(12 + capacity * 2 + 4);
        try
        {
            Marshal.WriteInt32(memory, 0, flags); Marshal.WriteInt32(memory, 4, 0); Marshal.WriteInt32(memory, 8, capacity);
            Marshal.WriteInt32(memory, 12 + capacity * 2, 0x12345678);
            var group = NativeCodeActionMenuTarget.CommandSet;
            target.QueryStatus(ref group, 1, new[] { new OLECMD { cmdID = NativeCodeActionMenuTarget.ItemStart } }, memory);
            Check(Marshal.ReadInt32(memory, 12 + capacity * 2) == 0x12345678, "버퍼 경계 보존");
            return Marshal.PtrToStringUni(IntPtr.Add(memory, 12))!;
        }
        finally { Marshal.FreeCoTaskMem(memory); }
    }
    private sealed class TestHost : INativeCodeMenuHost
    {
        private readonly Action<Point, NativeCodeActionMenuTarget, CancellationToken> show;
        public TestHost(Action<Point, NativeCodeActionMenuTarget, CancellationToken> show) { this.show = show; }
        public Task ShowAsync(Point screen, NativeCodeActionMenuTarget target, CancellationToken token) { show(screen, target, token); return Task.CompletedTask; }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}

// 공개 COM 계약의 경계만 대역으로 두고 실제 명령 라우팅·텍스트 마샬링 코드를 실행합니다.
namespace Microsoft.VisualStudio.OLE.Interop
{
    internal struct OLECMD { public uint cmdID; public uint cmdf; }
    internal interface IOleCommandTarget
    {
        int QueryStatus(ref Guid group, uint count, OLECMD[] commands, IntPtr text);
        int Exec(ref Guid group, uint command, uint options, IntPtr input, IntPtr output);
    }
}
namespace VisualBoost.CodeGeneration
{
    internal sealed class VisualStudioCodeMenuHost : INativeCodeMenuHost
    {
        public Task ShowAsync(Point screen, NativeCodeActionMenuTarget target, CancellationToken token) => throw new NotSupportedException("명시적 Shell 대역을 사용합니다.");
    }
}
