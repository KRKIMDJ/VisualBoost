using System;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Core.DocumentNavigation;

namespace VisualBoost.DocumentNavigation;

internal sealed class DocumentMemberBuffer
{
    private readonly ITextBuffer buffer;
    private readonly Dispatcher dispatcher;
    private readonly DocumentAnalysisSession session = new(new CppDocumentMemberProvider());
    private int users;
    private bool closed;
    private ITextSnapshot requested;
    public ITextSnapshot? Snapshot { get; private set; }
    public DocumentMemberSnapshot? Members { get; private set; }
    public string Status { get; private set; } = "분석 중…";
    public event EventHandler? Changed;

    private DocumentMemberBuffer(ITextBuffer buffer, Dispatcher dispatcher)
    {
        this.buffer = buffer; this.dispatcher = dispatcher; requested = buffer.CurrentSnapshot;
        buffer.Changed += OnBufferChanged;
        session.Completed += OnCompleted;
        Refresh();
    }
    public static DocumentMemberBuffer Attach(ITextBuffer buffer, Dispatcher dispatcher)
    {
        dispatcher.VerifyAccess();
        var shared = buffer.Properties.GetOrCreateSingletonProperty(() => new DocumentMemberBuffer(buffer, dispatcher));
        shared.users++;
        return shared;
    }
    [SuppressMessage("Usage", "VSTHRD001", Justification = "버퍼 작업 스레드에서 WPF 소유 Dispatcher에 비동기 알림만 게시하며 동기 대기를 하지 않습니다.")]
    private void OnBufferChanged(object? sender, TextContentChangedEventArgs args)
    {
        if (dispatcher.CheckAccess()) Refresh();
        else _ = dispatcher.BeginInvoke(new Action(() => { if (!closed) Refresh(); }));
    }
    private void Refresh()
    {
        requested = buffer.CurrentSnapshot;
        Snapshot = null; Members = null; Status = "분석 갱신 중…";
        Changed?.Invoke(this, EventArgs.Empty);
        var snapshot = requested;
        // 예상 밖 오류도 관찰하여 진단 로그에 남기고, 다음 편집에서 다시 분석할 수 있게 합니다.
        ObserveAsync(snapshot).FileAndForget("VisualBoost/DocumentMembers");
    }
    [SuppressMessage("Usage", "VSTHRD001", Justification = "WPF 소유 Dispatcher에 오류 표시만 비동기로 전달합니다.")]
    private async System.Threading.Tasks.Task ObserveAsync(ITextSnapshot snapshot)
    {
        try { await session.RequestAsync(snapshot.Version.VersionNumber, snapshot.GetText); }
        catch (Exception exception)
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (closed || requested != snapshot) return;
                Status = "함수 분석 실패 · 다음 편집 시 재시도";
                Changed?.Invoke(this, EventArgs.Empty);
            });
            ActivityLog.LogError("VisualBoost.DocumentMembers", exception.ToString());
        }
    }
    [SuppressMessage("Usage", "VSTHRD001", Justification = "불변 분석 결과를 WPF 소유 Dispatcher에 게시하며 UI 동기 대기를 하지 않습니다.")]
    private void OnCompleted(object? sender, DocumentAnalysisResult result)
    {
        if (dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            if (closed || requested != buffer.CurrentSnapshot || result.Version != requested.Version.VersionNumber) return;
            Snapshot = requested; Members = result.Snapshot;
            Status = result.Error ?? $"{Members?.Members.Count ?? 0:N0}개 함수";
            Changed?.Invoke(this, EventArgs.Empty);
        }));
    }
    public void Detach()
    {
        dispatcher.VerifyAccess();
        if (--users != 0) return;
        closed = true;
        buffer.Changed -= OnBufferChanged;
        session.Completed -= OnCompleted;
        session.Dispose();
        Changed = null;
        buffer.Properties.RemoveProperty(typeof(DocumentMemberBuffer));
    }
}
