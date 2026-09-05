using System;
using System.Threading;
using System.Threading.Tasks;

namespace VisualBoost.Core.DocumentNavigation;

public sealed class DocumentAnalysisResult : EventArgs
{
    public DocumentAnalysisResult(long version, DocumentMemberSnapshot? snapshot, string? error)
    { Version = version; Snapshot = snapshot; Error = error; }
    public long Version { get; }
    public DocumentMemberSnapshot? Snapshot { get; }
    public string? Error { get; }
}

// 불변 버퍼를 읽는 작업만 받습니다. 결과를 UI에 전달하는 쪽에서도 버전을 다시 확인해야 합니다.
public sealed class DocumentAnalysisSession : IDisposable
{
    private readonly object gate = new();
    private readonly IDocumentMemberProvider provider;
    private CancellationTokenSource? active;
    private bool disposed;
    public DocumentAnalysisSession(IDocumentMemberProvider provider) { this.provider = provider; }
    public event EventHandler<DocumentAnalysisResult>? Completed;

    public Task RequestAsync(long version, Func<string> readSnapshot, int delayMilliseconds = 200)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DocumentAnalysisSession));
            active?.Cancel();
            var request = active = new CancellationTokenSource();
            return Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMilliseconds, request.Token).ConfigureAwait(false);
                    DocumentAnalysisResult result;
                    try { result = new DocumentAnalysisResult(version, provider.Analyze(readSnapshot(), request.Token), null); }
                    catch (ArgumentException exception) { result = new DocumentAnalysisResult(version, null, exception.Message); }
                    EventHandler<DocumentAnalysisResult>? handler;
                    lock (gate)
                    {
                        if (disposed || active != request || request.IsCancellationRequested) return;
                        handler = Completed;
                    }
                    handler?.Invoke(this, result);
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                finally
                {
                    lock (gate) { if (active == request) active = null; request.Dispose(); }
                }
            });
        }
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; active?.Cancel(); Completed = null; }
    }
}
