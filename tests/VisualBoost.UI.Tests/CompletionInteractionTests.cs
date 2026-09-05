using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using VisualBoost.Completion;
using VisualBoost.Core.Analysis;

internal static class CompletionInteractionTests
{
    public static void Run(string root)
    {
        var view = new IWpfTextView();
        var native = new ICompletionBroker();
        var modern = new IAsyncCompletionBroker();
        var classifiers = new IClassifierAggregatorService();
        var undo = new ITextUndoHistoryRegistry();
        var listener = new CompletionViewListener(native, modern, classifiers, undo, new IEditorOperationsFactoryService());
        var window = new Window { Content = view.VisualElement, Width = 720, Height = 350, ShowInTaskbar = false };
        var snapshot = new SymbolCompletionSnapshot(new[] { new SourceSymbolLocation("SetMovementMode", "Sample.h", 1, 1, SourceSymbolKind.Function, "Game::Actor", "(int mode)") });
        CompletionRuntime.GetSnapshot = () => snapshot;
        CompletionRuntime.Enabled = true;
        CompletionRuntime.DelayMilliseconds = 1;
        listener.TextViewCreated(view);
        var session = view.Properties.Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var popup = (Popup)session.GetType().GetField("popup", flags)!.GetValue(session)!;
        var list = (ListBox)session.GetType().GetField("list", flags)!.GetValue(session)!;
        window.Show();
        try
        {
            TypePrefix();
            Until(() => popup.IsOpen);
            Pump(10);
            var surface = (FrameworkElement)popup.Child;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(root, "artifacts", "ui-validation", "CompletionSuggestions.png"))) encoder.Save(stream);
            Check(list.SelectedIndex == -1, "최초 후보 자동 선택 없음");
            Press(Key.Enter);
            Check(!popup.IsOpen && view.TextBuffer.Edits == 0, "Enter는 이름을 삽입하지 않음");
            TypePrefix(); Until(() => popup.IsOpen);
            Check(!Press(Key.Tab).Handled && view.TextBuffer.Edits == 0, "미선택 Tab은 기본 편집기 처리");
            TypePrefix(); Until(() => popup.IsOpen);
            Press(Key.Down);
            Check(Press(Key.Tab).Handled && view.TextSnapshot.Text == "SetMovementMode" && undo.History.Completed == 1, "명시적 선택+Tab은 단일 Undo 편집");
            native.Active = true;
            TypePrefix(); Pump(30);
            Check(!popup.IsOpen, "기존 IntelliSense 활성 시 추천 억제");
            native.Active = false;
            TypePrefix(); Until(() => popup.IsOpen);
            modern.Active = true;
            Check(!Press(Key.Tab).Handled && !popup.IsOpen, "지연된 IntelliSense에 키 처리 양보");
            modern.Active = false;
            classifiers.Classifier.Span.ClassificationType.Classification = "comment";
            TypePrefix(); Pump(30);
            Check(!popup.IsOpen, "여러 줄 주석 분류 제외");
            classifiers.Classifier.Span.ClassificationType.Classification = "identifier";
            TypePrefix(); Until(() => popup.IsOpen);
            CompletionRuntime.Enabled = false;
            Until(() => !popup.IsOpen);
            CompletionRuntime.Enabled = true;
            TypePrefix(); Until(() => popup.IsOpen);
            Press(Key.Down); view.TextBuffer.ReadOnly = true;
            Check(!Press(Key.Tab).Handled && view.TextBuffer.Edits == 1, "읽기 전용 전환 시 삽입 취소");
            view.TextBuffer.ReadOnly = false;
            TypePrefix(); Press(Key.Escape); Pump(30);
            Check(!popup.IsOpen, "Esc는 대기 중 요청도 취소");
            TypePrefix(); CompletionRuntime.GetSnapshot = () => SymbolCompletionSnapshot.Empty; Pump(30);
            Check(!popup.IsOpen, "솔루션 교체 시 이전 요청 폐기");
            CompletionRuntime.GetSnapshot = () => snapshot;
            TypePrefix(); Until(() => popup.IsOpen);
            view.LoseFocus(); Check(!popup.IsOpen, "포커스 이탈 시 닫기");
            view.HasAggregateFocus = true;
            TypePrefix(); view.Close(); Pump(30);
            Check(!popup.IsOpen, "문서 종료 시 지연 요청·창 해제");
            Console.WriteLine("PASS: 실제 자동완성 구현의 키·타이머·네이티브 충돌·옵션·문서 수명 검증 (SDK 경계 대역)");
        }
        finally { if (!view.IsClosed) view.Close(); window.Close(); CompletionRuntime.GetSnapshot = null; }

        void TypePrefix()
        {
            view.TextBuffer.Set("SetMov");
            view.VisualElement.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new TextComposition(InputManager.Current, view.VisualElement, "e")) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            view.TextBuffer.Type('e');
            view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, 7));
        }
        KeyEventArgs Press(Key key)
        {
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view.VisualElement)!, 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            view.VisualElement.RaiseEvent(e);
            return e;
        }
    }
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Yield(); }
    }
    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        { Pump(2); if (watch.ElapsedMilliseconds > 3000) throw new TimeoutException("자동완성 수명 테스트 시간 초과"); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
