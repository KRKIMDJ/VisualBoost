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
    }
}
namespace Microsoft.VisualStudio.Shell
{
    internal static class ActivityLog { public static void LogWarning(string source, string message) { } }
    internal static class VsBrushes
    {
        public static object ToolWindowBackgroundKey => SystemColors.WindowBrushKey;
        public static object ToolWindowTextKey => SystemColors.WindowTextBrushKey;
        public static object ToolWindowBorderKey => SystemColors.ActiveBorderBrushKey;
    }
}
namespace Microsoft.VisualStudio.Text
{
    internal readonly struct Span(int start, int length)
    { public int Start => start; public int Length => length; public int End => start + length; }
    internal sealed class ITextSnapshot(string text)
    {
        public string Text => text;
        public string GetText(Span span) => text.Substring(span.Start, span.Length);
    }
    internal readonly struct SnapshotSpan(ITextSnapshot snapshot, Span span) { public ITextSnapshot Snapshot => snapshot; public Span Span => span; }
    internal readonly struct SnapshotPoint(ITextSnapshot snapshot, int position)
    {
        public ITextSnapshot Snapshot => snapshot;
        public int Position => position;
        public SnapshotLine GetContainingLine() => new(snapshot);
    }
    internal sealed class SnapshotLine(ITextSnapshot snapshot)
    { public SnapshotPoint Start => new(snapshot, 0); public string GetText() => snapshot.Text; }
    internal sealed class TextChange(string newText, int oldLength, int position)
    { public string NewText => newText; public int OldLength => oldLength; public int NewPosition => position; }
    internal sealed class TextContentChangedEventArgs(ITextSnapshot after, TextChange change) : EventArgs
    { public ITextSnapshot After => after; public List<TextChange> Changes => new() { change }; }
    internal sealed class TestBuffer
    {
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
    }
    internal sealed class TestSelection { public bool IsEmpty = true; }
    internal sealed class IWpfTextView
    {
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
        public bool TryGetHistory(TestBuffer buffer, out TestHistory history) { history = History; return true; }
    }
    internal sealed class TestHistory
    {
        public int Completed;
        public TestTransaction CreateTransaction(string text) => new(this);
    }
    internal sealed class TestTransaction(TestHistory history) : IDisposable
    { public void Complete() => history.Completed++; public void Dispose() { } }
    internal sealed class IEditorOperations
    { public void AddBeforeTextBufferChangePrimitive() { } public void AddAfterTextBufferChangePrimitive() { } }
    internal sealed class IEditorOperationsFactoryService
    { public IEditorOperations GetEditorOperations(IWpfTextView view) => new(); }
}
