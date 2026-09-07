using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using VisualBoost.Core.DocumentNavigation;

namespace VisualBoost.DocumentNavigation;

// 열린 C++ 뷰의 현재 버퍼만 분석하며 분할 뷰는 DocumentMemberBuffer를 공유합니다.
internal sealed class DocumentNavigationSession : IDisposable
{
    private readonly IWpfTextView view;
    private readonly IOutliningManagerService outlining;
    private readonly DocumentNavigationControl control;
    private readonly DocumentMemberBuffer buffer;
    private readonly IVsEditorAdaptersFactoryService adapters;
    private readonly ITextDocumentFactoryService documents;
    private readonly DocumentNavigationCommandFilter filter;
    private IVsTextView? nativeView;
    internal DocumentNavigationBar Bar { get; } = new();
    private bool disposed;
    public DocumentNavigationSession(IWpfTextView view, IOutliningManagerService outlining,
        IVsEditorAdaptersFactoryService adapters, ITextDocumentFactoryService documents)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.view = view; this.outlining = outlining;
        this.adapters = adapters; this.documents = documents;
        control = new DocumentNavigationControl { EditorAnchor = view.VisualElement, BarAnchor = Bar, DescribeMember = Describe };
        filter = new DocumentNavigationCommandFilter(control);
        buffer = DocumentMemberBuffer.Attach(view.TextBuffer, control.Dispatcher);
        control.Navigate += Navigate;
        control.ReturnFocus += FocusEditor;
        buffer.Changed += OnDocumentChanged;
        view.Closed += OnViewClosed;
        view.Caret.PositionChanged += OnCaretChanged;
        Bar.OpenRequested += OpenFromBar;
        DocumentNavigationSettings.Changed += OnSettingsChanged;
        ApplySettings(); Refresh();
    }
    public void Open()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposed) return;
        // 명령을 열 때 최신 체인 앞에 등록합니다. 등록 실패 시 안전하지 않은 입력 창을 열지 않습니다.
        if (nativeView is not null) ErrorHandler.ThrowOnFailure(nativeView.RemoveCommandFilter(filter));
        nativeView = null;
        var adapter = adapters.GetViewAdapter(view);
        if (adapter is null) return;
        ErrorHandler.ThrowOnFailure(adapter.AddCommandFilter(filter, out var next));
        filter.Next = next; nativeView = adapter;
        Refresh(); control.Open();
    }
    private void OpenFromBar()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { Open(); }
        catch (COMException exception)
        {
            control.IsOpen = false;
            Bar.SetCurrent("탐색 입력 준비 실패 · Alt+M으로 재시도", null, null);
            ActivityLog.LogError("VisualBoost.DocumentMembers", exception.ToString());
        }
    }
    private string Describe(DocumentMember member)
    {
        var snapshot = buffer.Snapshot;
        if (snapshot is null || member.Start >= snapshot.Length) return member.Name;
        var text = snapshot.GetText(member.Start, Math.Min(300, member.End - member.Start));
        var body = text.IndexOf('{');
        return (body >= 0 ? text.Substring(0, body) : text).Trim() + $"\n라인 {member.Line}";
    }
    private void OnDocumentChanged(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        if (disposed) return;
        var valid = buffer.Snapshot == view.TextSnapshot;
        control.SetDocument(valid ? buffer.Members : null, buffer.Snapshot?.Version.VersionNumber ?? -1, buffer.Status);
        control.SetCaret(view.Caret.Position.BufferPosition.Position);
        UpdateCurrent();
    }
    private void OnCaretChanged(object? sender, CaretPositionChangedEventArgs e)
    {
        control.SetCaret(view.Caret.Position.BufferPosition.Position);
        UpdateCurrent();
    }
    private void UpdateCurrent()
    {
        if (disposed) return;
        var member = buffer.Snapshot == view.TextSnapshot ? buffer.Members?.FindContaining(view.Caret.Position.BufferPosition.Position) : null;
        var path = documents.TryGetTextDocument(view.TextBuffer, out var document) ? document.FilePath : null;
        // 클래스 끝 범위를 추측하지 않습니다. 함수 밖에서는 문서 이름으로 명확하게 복귀합니다.
        Bar.SetCurrent(member?.Name ?? (path is null ? "현재 문서" : Path.GetFileName(path)), path, member?.Kind);
    }
    private void Navigate(DocumentMember member, long version)
    {
        if (disposed || view.IsClosed) return;
        var snapshot = view.TextSnapshot;
        // 편집 직후 이벤트 전달 순서와 무관하게 오래된 결과로 이동하지 않습니다.
        if (snapshot != buffer.Snapshot || snapshot.Version.VersionNumber != version || member.NameOffset >= snapshot.Length)
        { Refresh(); FocusEditor(); return; }
        var point = new SnapshotPoint(snapshot, member.NameOffset);
        outlining.GetOutliningManager(view)?.ExpandAll(new SnapshotSpan(point, 0), region => true);
        view.Selection.Clear();
        view.Caret.MoveTo(point);
        view.Caret.EnsureVisible();
        // 접힌 영역을 펼친 뒤 호스트 스크롤 정책으로 중앙 정렬합니다. 문서 경계는 VS가 처리합니다.
        view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point, 0), EnsureSpanVisibleOptions.AlwaysCenter);
        FocusEditor();
    }
    private void FocusEditor() { if (!view.IsClosed) view.VisualElement.Focus(); }
    [SuppressMessage("Usage", "VSTHRD001", Justification = "설정 알림에서 WPF 소유 Dispatcher로 비동기 갱신만 게시합니다.")]
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (control.Dispatcher.CheckAccess()) ApplySettings();
        else _ = control.Dispatcher.BeginInvoke(new Action(ApplySettings));
    }
    private void ApplySettings()
    {
        if (disposed) return;
        if (control.IsOpen) control.Cancel();
        Bar.Visibility = DocumentNavigationSettings.ShowBar ? Visibility.Visible : Visibility.Collapsed;
        control.Configure(DocumentNavigationSettings.NameOrder);
    }
    private void OnViewClosed(object? sender, EventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); Dispose(); }
    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposed) return;
        disposed = true;
        view.Closed -= OnViewClosed;
        view.Caret.PositionChanged -= OnCaretChanged;
        Bar.OpenRequested -= OpenFromBar;
        if (nativeView is not null) nativeView.RemoveCommandFilter(filter);
        nativeView = null; filter.Next = null;
        DocumentNavigationSettings.Changed -= OnSettingsChanged;
        buffer.Changed -= OnDocumentChanged;
        buffer.Detach();
        control.Dispose();
        view.Properties.RemoveProperty(typeof(DocumentNavigationSession));
    }
}
