using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using VisualBoost.Core.DocumentNavigation;

namespace VisualBoost.DocumentNavigation;

// 명령을 처음 실행할 때만 생성하며 편집기 margin이나 기본 탐색 바에 UI를 추가하지 않습니다.
internal sealed class DocumentNavigationSession : IDisposable
{
    private readonly IWpfTextView view;
    private readonly IOutliningManagerService outlining;
    private readonly DocumentNavigationControl control;
    private readonly DocumentMemberBuffer buffer;
    private bool disposed;
    public DocumentNavigationSession(IWpfTextView view, IOutliningManagerService outlining)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.view = view; this.outlining = outlining;
        control = new DocumentNavigationControl { EditorAnchor = view.VisualElement, DescribeMember = Describe };
        buffer = DocumentMemberBuffer.Attach(view.TextBuffer, control.Dispatcher);
        control.Navigate += Navigate;
        control.ReturnFocus += FocusEditor;
        buffer.Changed += OnDocumentChanged;
        view.Closed += OnViewClosed;
        DocumentNavigationSettings.Changed += OnSettingsChanged;
        ApplySettings(); Refresh();
    }
    public void Open() { if (!disposed) { Refresh(); control.Open(); } }
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
        FocusEditor();
    }
    private void FocusEditor() { if (!view.IsClosed) view.VisualElement.Focus(); }
    [SuppressMessage("Usage", "VSTHRD001", Justification = "설정 알림에서 WPF 소유 Dispatcher로 비동기 갱신만 게시합니다.")]
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (control.Dispatcher.CheckAccess()) ApplySettings();
        else _ = control.Dispatcher.BeginInvoke(new Action(ApplySettings));
    }
    private void ApplySettings() { if (!disposed) control.Configure(DocumentNavigationSettings.NameOrder); }
    private void OnViewClosed(object sender, EventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); Dispose(); }
    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposed) return;
        disposed = true;
        view.Closed -= OnViewClosed;
        DocumentNavigationSettings.Changed -= OnSettingsChanged;
        buffer.Changed -= OnDocumentChanged;
        buffer.Detach();
        control.Dispose();
        view.Properties.RemoveProperty(typeof(DocumentNavigationSession));
    }
}
