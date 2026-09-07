using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

// 편집기 경계만 대체하고 실제 추천·키 처리·타이머·WPF 창 코드는 그대로 실행합니다.
namespace System.ComponentModel.Composition
{
    [AttributeUsage(AttributeTargets.Class)] internal sealed class ExportAttribute(Type type) : Attribute { public Type Type => type; }
    [AttributeUsage(AttributeTargets.Constructor)] internal sealed class ImportingConstructorAttribute : Attribute { }
}
namespace Microsoft.VisualStudio.Utilities
{
    [AttributeUsage(AttributeTargets.Class)] internal sealed class ContentTypeAttribute(string name) : Attribute { public string Name => name; }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)] internal sealed class TextViewRoleAttribute(string role) : Attribute { public string Role => role; }
    internal sealed class PropertyCollection
    {
        public object? Value;
        public T GetOrCreateSingletonProperty<T>(Func<T> create) => (T)(Value ??= create()!);
        public void RemoveProperty(object key) { if (Value?.GetType() == key as Type) Value = null; }
    }
}
namespace Microsoft.VisualStudio.Shell
{
    internal static class ActivityLog
    {
        public static readonly List<string> Errors = new();
        public static void LogWarning(string source, string message) { }
        public static void LogError(string source, string message) { Errors.Add(source + ": " + message); }
    }
    internal static class TaskLogStub
    {
        public static void FileAndForget(this System.Threading.Tasks.Task task, string name)
            => _ = task.ContinueWith(t => ActivityLog.LogError(name, t.Exception!.ToString()), System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
    }
    internal static class VsBrushes
    {
        public static object ToolWindowBackgroundKey => SystemColors.WindowBrushKey;
        public static object ToolWindowTextKey => SystemColors.WindowTextBrushKey;
        public static object ToolWindowBorderKey => SystemColors.ActiveBorderBrushKey;
    }
    internal static class VsResourceKeys
    {
        public static object ButtonStyleKey => "VS.ButtonStyle";
    }
}
namespace Microsoft.VisualStudio.Text
{
    internal readonly struct Span(int start, int length)
    { public int Start => start; public int Length => length; public int End => start + length; }
    internal sealed class ITextSnapshot(string text)
    {
        private static int nextVersion;
        public SnapshotVersion Version { get; } = new(System.Threading.Interlocked.Increment(ref nextVersion));
        public string GetText() => text;
        public int Length => text.Length;
        public string Text => text;
        public string GetText(Span span) => text.Substring(span.Start, span.Length);
        public string GetText(int start, int length) => text.Substring(start, length);
        public SnapshotLine GetLineFromLineNumber(int number)
        {
            var start = 0;
            for (var i = 0; i < number; i++) { var next = text.IndexOf('\n', start); if (next < 0) throw new ArgumentOutOfRangeException(nameof(number)); start = next + 1; }
            if (number < 0) throw new ArgumentOutOfRangeException(nameof(number));
            var end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
            if (end > start && text[end - 1] == '\r') end--;
            return new SnapshotLine(this, start, end - start);
        }
    }
    internal sealed class SnapshotVersion(int number) { public int VersionNumber => number; }
    internal interface ITextBuffer
    {
        ITextSnapshot CurrentSnapshot { get; }
        Microsoft.VisualStudio.Utilities.PropertyCollection Properties { get; }
        event EventHandler<TextContentChangedEventArgs>? Changed;
    }
    internal readonly struct SnapshotSpan(ITextSnapshot snapshot, Span span)
    {
        public SnapshotSpan(SnapshotPoint point, int length) : this(point.Snapshot, new Span(point.Position, length)) { }
        public ITextSnapshot Snapshot => snapshot; public Span Span => span;
    }
    internal readonly struct SnapshotPoint(ITextSnapshot snapshot, int position)
    {
        public ITextSnapshot Snapshot => snapshot;
        public int Position => position;
        public SnapshotLine GetContainingLine() => new(snapshot);
    }
    internal sealed class SnapshotLine(ITextSnapshot snapshot, int start = 0, int length = -1)
    { public SnapshotPoint Start => new(snapshot, start); public int Length => length < 0 ? snapshot.Length : length; public string GetText() => snapshot.Text.Substring(start, Length); }
    internal sealed class TextChange(string newText, int oldLength, int position)
    { public string NewText => newText; public int OldLength => oldLength; public int NewPosition => position; }
    internal sealed class TextContentChangedEventArgs(ITextSnapshot after, TextChange change) : EventArgs
    { public ITextSnapshot After => after; public List<TextChange> Changes => new() { change }; }
    internal sealed class TestBuffer : ITextBuffer
    {
        public ITextSnapshot CurrentSnapshot => Snapshot;
        public Microsoft.VisualStudio.Utilities.PropertyCollection Properties { get; } = new();
        public ITextSnapshot Snapshot = new("");
        public bool ReadOnly;
        public int Edits;
        public event EventHandler<TextContentChangedEventArgs>? Changed;
        public void Set(string text) => Snapshot = new(text);
        public void Type(char c)
        {
            var position = Snapshot.Text.Length;
            Snapshot = new(Snapshot.Text + c);
            Changed?.Invoke(this, new(Snapshot, new(c.ToString(), 0, position)));
        }
        public bool IsReadOnly(Span span) => ReadOnly;
        public TestEdit CreateEdit() => new(this);
        public sealed class TestEdit(TestBuffer buffer) : IDisposable
        {
            private Span span;
            private string text = "";
            public bool Canceled => false;
            public bool Replace(Span span, string text) { this.span = span; this.text = text; return !buffer.ReadOnly; }
            public void Apply()
            {
                buffer.Snapshot = new(buffer.Snapshot.Text.Substring(0, span.Start) + text + buffer.Snapshot.Text.Substring(span.End));
                buffer.Edits++;
                buffer.Changed?.Invoke(buffer, new(buffer.Snapshot, new(text, span.Length, span.Start)));
            }
            public void Dispose() { }
        }
    }
}
namespace Microsoft.VisualStudio.Text.Editor
{
    using Microsoft.VisualStudio.Utilities;
    internal enum EnsureSpanVisibleOptions { AlwaysCenter = 4 }
    internal sealed class TestViewScroller
    {
        public SnapshotSpan? LastSpan;
        public EnsureSpanVisibleOptions LastOptions;
        public void EnsureSpanVisible(SnapshotSpan span, EnsureSpanVisibleOptions options) { LastSpan = span; LastOptions = options; }
    }
    internal interface IWpfTextViewCreationListener { void TextViewCreated(IWpfTextView view); }
    internal static class PredefinedTextViewRoles { public const string Editable = "Editable", Document = "Document"; }
    internal sealed class CaretPositionChangedEventArgs : EventArgs { }
    internal sealed class TextViewLayoutChangedEventArgs : EventArgs { }
    internal sealed class CaretPosition { public SnapshotPoint BufferPosition { get; set; } }
    internal sealed class TestCaret
    {
        public CaretPosition Position { get; } = new();
        public bool InVirtualSpace { get; set; }
        public double Left => 10;
        public double Bottom => 25;
        public event EventHandler<CaretPositionChangedEventArgs>? PositionChanged;
        public void MoveTo(SnapshotPoint point) { Position.BufferPosition = point; PositionChanged?.Invoke(this, new()); }
        public void EnsureVisible() { }
    }
    internal sealed class TestSelection { public bool IsEmpty = true; public void Clear() => IsEmpty = true; }
    internal sealed class IWpfTextView
    {
        public TestViewScroller ViewScroller { get; } = new();
        public PropertyCollection Properties { get; } = new();
        public TestBuffer TextBuffer { get; } = new();
        public ITextSnapshot TextSnapshot => TextBuffer.Snapshot;
        public TestCaret Caret { get; } = new();
        public TestSelection Selection { get; } = new();
        public Border VisualElement { get; } = new() { Focusable = true, MinWidth = 700, MinHeight = 300 };
        public bool HasAggregateFocus = true;
        public bool IsClosed;
        public double ViewportLeft => 0;
        public double ViewportTop => 0;
        public event EventHandler? LostAggregateFocus;
        public event EventHandler<TextViewLayoutChangedEventArgs>? LayoutChanged;
        public event EventHandler? Closed;
        public void LoseFocus() { HasAggregateFocus = false; LostAggregateFocus?.Invoke(this, EventArgs.Empty); }
        public void Layout() => LayoutChanged?.Invoke(this, new());
        public void Close() { IsClosed = true; Closed?.Invoke(this, EventArgs.Empty); }
    }
}
namespace Microsoft.VisualStudio.Language.Intellisense
{
    using Microsoft.VisualStudio.Text.Editor;
    internal sealed class ICompletionBroker { public bool Active; public bool IsCompletionActive(IWpfTextView view) => Active; }
}
namespace Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion
{
    using Microsoft.VisualStudio.Text.Editor;
    internal sealed class IAsyncCompletionBroker { public bool Active; public bool IsCompletionActive(IWpfTextView view) => Active; }
}
namespace Microsoft.VisualStudio.Text.Classification
{
    internal sealed class ClassificationType
    {
        public string Classification = "identifier";
        public bool IsOfType(string name) => Classification == name;
    }
    internal sealed class ClassificationSpan { public ClassificationType ClassificationType { get; } = new(); }
    internal sealed class IClassifier : IDisposable
    {
        public void Dispose() { }
        public ClassificationSpan Span { get; } = new();
        public List<ClassificationSpan> GetClassificationSpans(SnapshotSpan span) => new() { Span };
    }
    internal sealed class IClassifierAggregatorService
    {
        public IClassifier Classifier { get; } = new();
        public IClassifier GetClassifier(TestBuffer buffer) => Classifier;
    }
}
namespace Microsoft.VisualStudio.Text.Operations
{
    using Microsoft.VisualStudio.Text.Editor;
    internal sealed class ITextUndoHistoryRegistry
    {
        public TestHistory History { get; } = new();
        public bool Available = true;
        public bool TryGetHistory(TestBuffer buffer, out TestHistory history) { history = History; history.Buffer = buffer; return Available; }
    }
    internal sealed class TestHistory
    {
        public int Completed;
        public int Canceled;
        public TestBuffer? Buffer;
        public string Before = "", After = "";
        public void Undo() => Buffer!.Set(Before);
        public void Redo() => Buffer!.Set(After);
        public TestTransaction CreateTransaction(string text) => new(this);
    }
    internal sealed class TestTransaction(TestHistory history) : IDisposable
    {
        private readonly string before = history.Buffer!.Snapshot.Text;
        public void Complete() { history.Before = before; history.After = history.Buffer!.Snapshot.Text; history.Completed++; }
        public void Cancel() { history.Buffer!.Set(before); history.Canceled++; }
        public void Dispose() { }
    }
    internal sealed class IEditorOperations
    {
        public bool FailAfter;
        public void AddBeforeTextBufferChangePrimitive() { }
        public void AddAfterTextBufferChangePrimitive() { if (FailAfter) throw new InvalidOperationException("Simulated failure after edit"); }
    }
    internal sealed class IEditorOperationsFactoryService
    { public IEditorOperations GetEditorOperations(IWpfTextView view) => new(); }
}
namespace Microsoft.VisualStudio.PlatformUI
{
    internal class DialogWindow : Window { public bool? ShowModal() => ShowDialog(); }
}
