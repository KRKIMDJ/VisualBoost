using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace VisualBoost.SemanticNavigation;

internal static class SemanticNavigationRuntime
{
    public static SemanticNavigationService? Service { get; set; }
}

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("C/C++")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class SemanticDocumentListener : IWpfTextViewCreationListener
{
    private readonly ITextDocumentFactoryService documents;

    [ImportingConstructor]
    public SemanticDocumentListener(ITextDocumentFactoryService documents) => this.documents = documents;

    public void TextViewCreated(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (documents.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var document))
        {
            SemanticDocumentTracker.Attach(view, document);
        }
    }
}

/// <summary>편집기에 열린 C++ 문서를 추적해 clangd에 활성 문서 예열, 유휴 시 편집 내용, 저장, 닫기를 알립니다.</summary>
/// <remarks>
/// UI thread에서만 접근합니다. 문서 내용 문자열은 작업 스레드의 알림 큐에서 스냅샷으로 만들어 UI를 막지 않습니다.
/// 리비전은 버퍼마다 늘어나는 일련번호와 스냅샷 버전을 합쳐, 같은 경로를 다시 연 뒤의 내용이 이전 버퍼보다 새로 취급되게 합니다.
/// </remarks>
internal static class SemanticDocumentTracker
{
    private static readonly Dictionary<ITextBuffer, Tracked> Buffers = new();
    private static long nextSerial;
    private static DispatcherTimer? idleTimer;
    private static DispatcherTimer? warmTimer;
    private static Tracked? pendingWarm;

    public static void Attach(IWpfTextView view, ITextDocument document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var buffer = document.TextBuffer;
        if (!Buffers.TryGetValue(buffer, out var item))
        {
            item = new Tracked(document, ++nextSerial);
            document.FileActionOccurred += item.OnFileAction;
            buffer.Changed += item.OnChanged;
            Buffers[buffer] = item;
        }

        item.Views++;
        EventHandler focused = (_, _) => ScheduleWarm(item);
        EventHandler? closed = null;
        closed = (_, _) =>
        {
            view.GotAggregateFocus -= focused;
            view.Closed -= closed;
            if (--item.Views == 0) Detach(item);
        };
        view.GotAggregateFocus += focused;
        view.Closed += closed;
        if (view.HasAggregateFocus) ScheduleWarm(item);
    }

    /// <summary>요청 문서의 리비전입니다. 추적하지 않는 문서는 0입니다.</summary>
    public static long RevisionOf(ITextSnapshot snapshot)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Buffers.TryGetValue(snapshot.TextBuffer, out var item) ? item.Revision(snapshot) : 0;
    }

    /// <summary>추적 중인 다른 문서의 현재 스냅샷입니다(경로, 스냅샷, 리비전).</summary>
    public static IReadOnlyList<(string Path, ITextSnapshot Snapshot, long Revision)> OpenDocuments()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Buffers.Values.Select(item =>
        {
            var snapshot = item.Document.TextBuffer.CurrentSnapshot;
            return (item.Path, snapshot, item.Revision(snapshot));
        }).ToArray();
    }

    private static void ScheduleWarm(Tracked item)
    {
        pendingWarm = item;
        warmTimer ??= CreateTimer(TimeSpan.FromMilliseconds(800), OnWarm);
        warmTimer.Stop();
        warmTimer.Start();
    }

    private static void ScheduleIdle()
    {
        idleTimer ??= CreateTimer(TimeSpan.FromMilliseconds(1500), OnIdle);
        idleTimer.Stop();
        idleTimer.Start();
    }

    private static DispatcherTimer CreateTimer(TimeSpan interval, EventHandler tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += tick;
        return timer;
    }

    private static void OnWarm(object? sender, EventArgs args)
    {
        warmTimer?.Stop();
        var item = pendingWarm;
        pendingWarm = null;
        if (item is null || item.Views == 0) return;
        var snapshot = item.Document.TextBuffer.CurrentSnapshot;
        SemanticNavigationRuntime.Service?.Warm(item.Path, snapshot.GetText, item.Revision(snapshot));
    }

    private static void OnIdle(object? sender, EventArgs args)
    {
        idleTimer?.Stop();
        var service = SemanticNavigationRuntime.Service;
        foreach (var item in Buffers.Values.Where(i => i.Dirty))
        {
            item.Dirty = false;
            var snapshot = item.Document.TextBuffer.CurrentSnapshot;
            // clangd에 열려 있지 않은 문서는 탐색기 쪽에서 내용을 만들지 않고 무시합니다.
            service?.Update(item.Path, snapshot.GetText, item.Revision(snapshot));
        }
    }

    private static void Detach(Tracked item)
    {
        item.Document.FileActionOccurred -= item.OnFileAction;
        item.Document.TextBuffer.Changed -= item.OnChanged;
        Buffers.Remove(item.Document.TextBuffer);
        if (ReferenceEquals(pendingWarm, item)) pendingWarm = null;
        SemanticNavigationRuntime.Service?.Closed(item.Path);
    }

    private sealed class Tracked
    {
        private readonly long serial;

        public Tracked(ITextDocument document, long serial)
        {
            Document = document;
            Path = document.FilePath;
            this.serial = serial;
        }

        public ITextDocument Document { get; }

        public string Path { get; private set; }

        public int Views { get; set; }

        public bool Dirty { get; set; }

        public long Revision(ITextSnapshot snapshot) => (serial << 32) + snapshot.Version.VersionNumber;

        public void OnChanged(object? sender, TextContentChangedEventArgs args)
        {
            Dirty = true;
            ScheduleIdle();
        }

        public void OnFileAction(object? sender, TextDocumentFileActionEventArgs args)
        {
            var service = SemanticNavigationRuntime.Service;
            if ((args.FileActionType & FileActionTypes.DocumentRenamed) != 0)
            {
                service?.Closed(Path);
                Path = args.FilePath;
            }

            if ((args.FileActionType & FileActionTypes.ContentSavedToDisk) != 0)
            {
                Dirty = false;
                var snapshot = Document.TextBuffer.CurrentSnapshot;
                service?.Saved(Path, snapshot.GetText, Revision(snapshot));
            }
        }
    }
}
