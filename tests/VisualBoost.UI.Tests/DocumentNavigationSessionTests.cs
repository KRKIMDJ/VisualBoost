using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using VisualBoost.DocumentNavigation;

internal static class DocumentNavigationSessionTests
{
    public static void Run()
    {
        var view = new IWpfTextView();
        const string source = "// document\nvoid First() { Call(); }\nvoid Second() {}";
        view.TextBuffer.Set(source);
        var adapters = new IVsEditorAdaptersFactoryService();
        using var session = new DocumentNavigationSession(view, new IOutliningManagerService(), adapters, new ITextDocumentFactoryService());
        var control = (DocumentNavigationControl)typeof(DocumentNavigationSession).GetField("control", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        var panel = new DockPanel(); DockPanel.SetDock(session.Bar, Dock.Top);
        panel.Children.Add(session.Bar); panel.Children.Add(view.VisualElement);
        var window = new Window { Content = panel, Width = 980, Height = 480, Left = -16000, Top = 20 };
        string Caption() => ((DockPanel)((Button)session.Bar.Child).Content).Children.OfType<TextBlock>().Last().Text;
        try
        {
            window.Show();
            Move(source.IndexOf("Call", StringComparison.Ordinal));
            Until(() => Caption() == "First");
            Move(source.IndexOf("Second", StringComparison.Ordinal));
            Check(Caption() == "Second", "캐럿 이벤트에서 현재 함수 즉시 갱신");
            Move(0); Check(Caption() == "Sample.cpp", "함수 밖에서 파일 표시");
            session.Open(); Until(() => control.Results.Items.Count == 2);
            control.SearchInput.Text = "Sec";
            Move(source.IndexOf("Call", StringComparison.Ordinal));
            Check(control.SearchInput.Text == "Sec" && Caption() == "First", "검색어와 캐럿 표시 독립");
            Until(() => control.Results.Items.Count == 1);
            Exec(VSConstants.VSStd2KCmdID.RETURN);
            Check(view.Caret.Position.BufferPosition.Position == source.IndexOf("Second", StringComparison.Ordinal), "실제 세션 Enter 이동");
            Check(!control.IsOpen && view.TextSnapshot.GetText() == source, "이동 후 원본 불변");
            Check(view.ViewScroller.LastOptions == EnsureSpanVisibleOptions.AlwaysCenter &&
                view.ViewScroller.LastSpan?.Span.Start == view.Caret.Position.BufferPosition.Position, "선택한 함수 라인을 호스트 중앙 스크롤로 표시");
            var registered = adapters.Native.Added;
            for (var i = 0; i < 10; i++)
            {
                session.Open(); Exec(VSConstants.VSStd2KCmdID.CANCEL);
            }
            Check(adapters.Native.Added == registered + 10 && adapters.Native.Active == 1, "반복 열기 명령 필터 중복 없음");
            DocumentNavigationSettings.Publish(false, false);
            Check(session.Bar.Visibility == Visibility.Collapsed, "표시 옵션 즉시 반영");
            session.Open(); Check(control.PlacementTarget == view.VisualElement, "숨김 모드 Alt+M 위치");
            view.Close();
            Check(adapters.Native.Active == 0 && !control.IsOpen && view.TextBuffer.Properties.Value is null, "문서 종료 필터·Popup·분석 세션 해제");
            Move(0); DocumentNavigationSettings.Publish(false, true);
            Check(!control.IsOpen, "종료 후 설정·캐럿 이벤트 재진입 없음");
            Console.WriteLine("PASS: 실제 문서 탐색 세션 캐럿·검색어·이동·표시 옵션·필터 재등록·종료 (SDK 경계 대역)");
        }
        finally { view.Close(); window.Close(); DocumentNavigationSettings.Publish(false, true); }
        void Move(int position) => view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, position));
        void Exec(VSConstants.VSStd2KCmdID command)
        {
            var group = VSConstants.VSStd2K;
            adapters.Native.Target!.Exec(ref group, (uint)command, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Until(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000) throw new Exception("문서 탐색 세션 대기 시간 초과");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}

namespace Microsoft.VisualStudio.Editor
{
    internal sealed class IVsEditorAdaptersFactoryService
    {
        public TextManager.Interop.IVsTextView Native { get; } = new();
        public TextManager.Interop.IVsTextView GetViewAdapter(IWpfTextView view) => Native;
    }
}
namespace Microsoft.VisualStudio.TextManager.Interop
{
    internal sealed class IVsTextView
    {
        public IOleCommandTarget? Target;
        public int Added, Active;
        public int AddCommandFilter(IOleCommandTarget target, out IOleCommandTarget? next)
        { next = Target; Target = target; Added++; Active++; return 0; }
        public int RemoveCommandFilter(IOleCommandTarget target)
        { if (Target == target) { Target = null; Active--; } return 0; }
    }
}
namespace Microsoft.VisualStudio.Text
{
    internal sealed class ITextDocumentFactoryService
    {
        public bool TryGetTextDocument(ITextBuffer buffer, out TestDocument document) { document = new(); return true; }
    }
    internal sealed class TestDocument { public string FilePath => "Sample.cpp"; }
}
namespace Microsoft.VisualStudio.Text.Outlining
{
    internal sealed class IOutliningManagerService
    {
        public IOutliningManagerService GetOutliningManager(IWpfTextView view) => this;
        public void ExpandAll(SnapshotSpan span, Func<object, bool> match) { }
    }
}
