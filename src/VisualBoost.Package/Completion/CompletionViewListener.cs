using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Completion;

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("C/C++")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class CompletionViewListener : IWpfTextViewCreationListener
{
    private readonly ICompletionBroker legacy;
    private readonly IAsyncCompletionBroker modern;
    private readonly IClassifierAggregatorService classifiers;
    private readonly ITextUndoHistoryRegistry undo;
    private readonly IEditorOperationsFactoryService operations;

    [ImportingConstructor]
    public CompletionViewListener(ICompletionBroker legacy, IAsyncCompletionBroker modern, IClassifierAggregatorService classifiers,
        ITextUndoHistoryRegistry undo, IEditorOperationsFactoryService operations)
    { this.legacy = legacy; this.modern = modern; this.classifiers = classifiers; this.undo = undo; this.operations = operations; }

    public void TextViewCreated(IWpfTextView textView)
    {
        textView.Properties.GetOrCreateSingletonProperty(() => new Session(textView, legacy, modern, classifiers.GetClassifier(textView.TextBuffer), undo, operations.GetEditorOperations(textView)));
    }

    private sealed class Session
    {
        private readonly IWpfTextView view;
        private readonly ICompletionBroker legacy;
        private readonly IAsyncCompletionBroker modern;
        private readonly IClassifier classifier;
        private readonly ITextUndoHistoryRegistry undo;
        private readonly IEditorOperations operations;
        private readonly DispatcherTimer delay = new(DispatcherPriority.Background);
        private readonly DispatcherTimer guard = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(75) };
        private readonly Popup popup;
        private readonly ListBox list;
        private ITextSnapshot? requestedSnapshot;
        private Span replacement;
        private string prefix = string.Empty;
        private bool selected;
        private bool committing;
        private bool typed;
        private int requestedCaret;
        private Func<SymbolCompletionSnapshot>? requestedProvider;

        public Session(IWpfTextView view, ICompletionBroker legacy, IAsyncCompletionBroker modern, IClassifier classifier,
            ITextUndoHistoryRegistry undo, IEditorOperations operations)
        {
            this.view = view; this.legacy = legacy; this.modern = modern; this.classifier = classifier;
            this.undo = undo; this.operations = operations;
            list = new ListBox { Focusable = false, IsTabStop = false, MaxHeight = 200, MinWidth = 320, MaxWidth = 620,
                BorderThickness = new Thickness(0), DisplayMemberPath = "Description", FontSize = 12 };
            list.SetResourceReference(Control.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            list.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            list.ItemContainerStyle = new Style(typeof(ListBoxItem));
            list.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
            list.ItemContainerStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            AutomationProperties.SetName(list, "VisualBoost 인덱스 이름 제안");
            var panel = new StackPanel();
            panel.Children.Add(list);
            var hint = new TextBlock { Text = "VisualBoost · 이름 제안    ↓ 선택 · Tab 삽입 · Esc 닫기", Margin = new Thickness(6), FontSize = 11 };
            hint.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            panel.Children.Add(hint);
            var border = new Border { Child = panel, BorderThickness = new Thickness(1) };
            border.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            border.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
            popup = new Popup { Child = border, PlacementTarget = view.VisualElement, Placement = PlacementMode.Relative,
                StaysOpen = true, Focusable = false, AllowsTransparency = false };
            delay.Tick += OnDelay;
            guard.Tick += OnGuard;
            view.TextBuffer.Changed += OnChanged;
            view.Caret.PositionChanged += OnCaretChanged;
            view.LostAggregateFocus += OnLostFocus;
            view.LayoutChanged += OnLayout;
            view.Closed += OnClosed;
            view.VisualElement.PreviewKeyDown += OnKey;
            view.VisualElement.PreviewTextInput += OnTextInput;
            view.VisualElement.PreviewMouseDown += OnMouse;
            list.PreviewMouseLeftButtonDown += OnPick;
        }

        private bool NativeActive() => legacy.IsCompletionActive(view) || modern.IsCompletionActive(view);

        private void OnChanged(object? sender, TextContentChangedEventArgs e)
        {
            if (!view.VisualElement.Dispatcher.CheckAccess()) return;
            var isTyping = typed;
            typed = false;
            Hide(); delay.Stop(); requestedSnapshot = null;
            // 붙여넣기·Undo·자동 서식에는 추천을 열지 않습니다.
            if (!isTyping || committing || !CompletionRuntime.Enabled || e.Changes.Count != 1 ||
                e.Changes[0].NewText.Length != 1 || e.Changes[0].OldLength != 0 ||
                !CompletionInput.IsPart(e.Changes[0].NewText[0])) return;
            requestedSnapshot = e.After;
            requestedCaret = e.Changes[0].NewPosition + 1;
            requestedProvider = CompletionRuntime.GetSnapshot;
            delay.Interval = TimeSpan.FromMilliseconds(CompletionRuntime.DelayMilliseconds);
            delay.Start();
        }

        private void OnDelay(object? sender, EventArgs e)
        {
            delay.Stop();
            try { ShowSuggestions(); }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException || exception is COMException)
            {
                // 뷰나 언어 서비스가 교체되는 경계에서는 입력을 중단시키지 않습니다.
                Hide();
                ActivityLog.LogWarning("VisualBoost/Completion", exception.Message);
            }
        }

        private void ShowSuggestions()
        {
            if (!CanShow() || requestedSnapshot != view.TextSnapshot || NativeActive()) return;
            var point = view.Caret.Position.BufferPosition;
            if (point.Position != requestedCaret) return;
            var line = point.GetContainingLine();
            if (!CompletionInput.TryGetPrefix(line.GetText(), point.Position - line.Start.Position,
                CompletionRuntime.MinimumLength, out var start, out prefix)) return;
            replacement = new Span(line.Start.Position + start, prefix.Length);
            var classified = classifier.GetClassificationSpans(new SnapshotSpan(point.Snapshot, replacement));
            if (classified.Any(item => item.ClassificationType.IsOfType("comment") || item.ClassificationType.IsOfType("string") ||
                item.ClassificationType.Classification.IndexOf("string", StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.ClassificationType.Classification.IndexOf("comment", StringComparison.OrdinalIgnoreCase) >= 0)) return;
            var items = CompletionRuntime.GetSnapshot?.Invoke().Find(prefix);
            if (items is null || items.Count == 0 || NativeActive()) return;
            list.ItemsSource = items;
            list.SelectedIndex = -1; selected = false;
            var caret = view.Caret;
            popup.HorizontalOffset = Math.Max(0, caret.Left - view.ViewportLeft);
            popup.VerticalOffset = caret.Bottom - view.ViewportTop;
            popup.IsOpen = true;
            guard.Start();
        }

        private bool CanShow() => !view.IsClosed && view.HasAggregateFocus && view.Selection.IsEmpty &&
            !view.Caret.InVirtualSpace && CompletionRuntime.Enabled && CompletionRuntime.GetSnapshot is not null &&
            ReferenceEquals(requestedProvider, CompletionRuntime.GetSnapshot);

        private void OnGuard(object? sender, EventArgs e)
        {
            if (!CanShow() || NativeActive() || requestedSnapshot != view.TextSnapshot) Hide();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            var modifiers = Keyboard.Modifiers;
            typed = (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift) &&
                ((e.Key >= Key.A && e.Key <= Key.Z) || (e.Key >= Key.D0 && e.Key <= Key.D9) || e.Key == Key.OemMinus);
            if (popup.IsOpen && NativeActive()) { delay.Stop(); Hide(); return; }
            if (e.Key == Key.Escape) { delay.Stop(); if (popup.IsOpen) { Hide(); e.Handled = true; } return; }
            if (!popup.IsOpen) return;
            if (!CanShow() || NativeActive()) { Hide(); return; }
            if (Keyboard.Modifiers != ModifierKeys.None) { Hide(); return; }
            if (e.Key == Key.Down || (e.Key == Key.Up && selected))
            {
                list.SelectedIndex = Math.Max(0, Math.Min(list.Items.Count - 1, list.SelectedIndex + (e.Key == Key.Down ? 1 : -1)));
                selected = true; list.ScrollIntoView(list.SelectedItem); e.Handled = true;
            }
            else if (e.Key == Key.Tab && selected) { e.Handled = Commit(); }
            else if (e.Key is Key.Tab or Key.Enter or Key.Left or Key.Right or Key.Up or Key.Home or Key.End) Hide();
        }

        private void OnPick(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem item)
            { list.SelectedItem = item.Content; selected = true; e.Handled = Commit(); }
        }

        private bool Commit()
        {
            if (!CanShow() || NativeActive() || requestedSnapshot != view.TextSnapshot ||
                list.SelectedItem is not SourceSymbolLocation item ||
                view.Caret.Position.BufferPosition.Position != replacement.End ||
                view.TextSnapshot.GetText(replacement) != prefix || view.TextBuffer.IsReadOnly(replacement)) { Hide(); return false; }
            committing = true;
            try
            {
                if (!undo.TryGetHistory(view.TextBuffer, out var history)) return false;
                using var transaction = history.CreateTransaction("VisualBoost 이름 자동완성");
                operations.AddBeforeTextBufferChangePrimitive();
                // 선택한 이름만 단일 편집으로 삽입합니다. 인수·include·다른 파일은 변경하지 않습니다.
                using var edit = view.TextBuffer.CreateEdit();
                if (!edit.Replace(replacement, item.Name)) return false;
                edit.Apply();
                if (edit.Canceled) return false;
                view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, replacement.Start + item.Name.Length));
                operations.AddAfterTextBufferChangePrimitive();
                transaction.Complete();
                return true;
            }
            finally { committing = false; Hide(); }
        }

        private void Hide() { popup.IsOpen = false; guard.Stop(); selected = false; list.ItemsSource = null; }
        private void OnTextInput(object sender, TextCompositionEventArgs e)
        {
            // IME 조합·붙여넣기·Undo와 실제 단일 영문 식별자 입력을 구분합니다.
            typed = e.Text.Length == 1 && e.Text[0] < 128 && CompletionInput.IsPart(e.Text[0]);
        }
        private void OnCaretChanged(object? sender, CaretPositionChangedEventArgs e) => Hide();
        private void OnLostFocus(object? sender, EventArgs e) { delay.Stop(); Hide(); }
        private void OnMouse(object sender, MouseButtonEventArgs e) { delay.Stop(); Hide(); }
        private void OnLayout(object? sender, TextViewLayoutChangedEventArgs e) { if (popup.IsOpen) Hide(); }
        private void OnClosed(object? sender, EventArgs e)
        {
            delay.Stop(); Hide();
            delay.Tick -= OnDelay; guard.Tick -= OnGuard;
            view.TextBuffer.Changed -= OnChanged;
            view.Caret.PositionChanged -= OnCaretChanged;
            view.LostAggregateFocus -= OnLostFocus;
            view.LayoutChanged -= OnLayout;
            view.Closed -= OnClosed;
            view.VisualElement.PreviewKeyDown -= OnKey;
            view.VisualElement.PreviewTextInput -= OnTextInput;
            view.VisualElement.PreviewMouseDown -= OnMouse;
            list.PreviewMouseLeftButtonDown -= OnPick;
            (classifier as IDisposable)?.Dispose();
            requestedSnapshot = null;
        }
    }
}
