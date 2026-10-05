using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Coloring;

[Export(typeof(IViewTaggerProvider)), ContentType("C/C++"), TextViewRole(PredefinedTextViewRoles.Document)]
[TagType(typeof(ClassificationTag))]
internal sealed class QuickColorTaggerProvider : IViewTaggerProvider
{
    private readonly IClassificationTypeRegistryService registry;
    [ImportingConstructor]
    public QuickColorTaggerProvider(IClassificationTypeRegistryService registry) => this.registry = registry;
    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
    {
        if (textView is not IWpfTextView view || buffer != view.TextBuffer || typeof(T) != typeof(ClassificationTag)) return null;
        return view.Properties.GetOrCreateSingletonProperty(() => new QuickColorTagger(view, registry)) as ITagger<T>;
    }
}

/// <summary>보이는 편집 문서만 비동기로 색칠하며 탐색 인덱스·디스크·프로젝트에는 접근하지 않습니다.</summary>
internal sealed class QuickColorTagger : ITagger<ClassificationTag>, IDisposable
{
    private static readonly SemaphoreSlim Worker = new(1, 1);
    private readonly IWpfTextView view;
    private readonly ClassificationTag?[] tags = new ClassificationTag?[SemanticColorPalette.KindCount];
    private CancellationTokenSource? request;
    private Result? result;
    private bool disposed;
    private sealed class Result
    {
        internal Result(ITextSnapshot snapshot, IReadOnlyList<QuickColorSpan> spans) { Snapshot = snapshot; Spans = spans; }
        internal ITextSnapshot Snapshot { get; }
        internal IReadOnlyList<QuickColorSpan> Spans { get; }
    }

    internal QuickColorTagger(IWpfTextView view, IClassificationTypeRegistryService registry)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.view = view;
        for (var i = 0; i < tags.Length; i++)
        {
            // VS의 Semantic 계층이 준비되면 이 Syntactic 힌트보다 우선합니다.
            var type = registry.GetClassificationType(ClassificationLayer.Syntactic, "VisualBoost.Fast." + (SemanticColorKind)i);
            if (type is not null) tags[i] = new ClassificationTag(type);
        }
        view.TextBuffer.Changed += OnChanged;
        view.Closed += OnClosed;
        view.VisualElement.IsVisibleChanged += OnVisibleChanged;
        ColoringSettings.Changed += OnSettingsChanged;
        SystemParameters.StaticPropertyChanged += OnSystemChanged;
        Schedule(false);
    }

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;
    private bool Enabled => ColoringSettings.Current.Enabled && ColoringSettings.Current.QuickColoring && !SystemParameters.HighContrast;

    private void OnChanged(object sender, TextContentChangedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Volatile.Write(ref result, null);
        Schedule(true);
    }
    private void OnClosed(object? sender, EventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); Dispose(); }
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); Schedule(false); }
    private void OnSystemChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.HighContrast)) OnSettingsChanged(sender, EventArgs.Empty); }
    private void OnSettingsChanged(object? sender, EventArgs e) => RefreshSettingsAsync().FileAndForget("VisualBoost/QuickColorSettings");
    private async Task RefreshSettingsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (disposed) return;
        Notify(); Schedule(false);
    }

    private void Schedule(bool debounce)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        request?.Cancel();
        if (disposed || view.IsClosed || !view.VisualElement.IsVisible || !Enabled) return;
        var snapshot = view.TextSnapshot;
        if (result?.Snapshot == snapshot || snapshot.Length > CppQuickColorScanner.MaximumLength) return;
        var lifetime = new CancellationTokenSource();
        request = lifetime;
        AnalyzeAsync(snapshot, lifetime, debounce).FileAndForget("VisualBoost/QuickColor");
    }

    private async Task AnalyzeAsync(ITextSnapshot snapshot, CancellationTokenSource lifetime, bool debounce)
    {
            var token = lifetime.Token;
            try
            {
                if (debounce) await Task.Delay(45, token);
                var spans = await Task.Run(async () =>
                {
                    await Worker.WaitAsync(token).ConfigureAwait(false);
                    try { return CppQuickColorScanner.Scan(snapshot.GetText(), token); }
                    finally { Worker.Release(); }
                }, token);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (disposed || view.IsClosed || view.TextSnapshot != snapshot || !Enabled) return;
                Volatile.Write(ref result, new Result(snapshot, spans));
                Notify();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                // 편집기 확장 경계에서 기록하고 기본 VS 색상은 계속 사용할 수 있게 합니다.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ActivityLog.LogError("VisualBoost/QuickColor", exception.ToString());
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ReferenceEquals(request, lifetime)) request = null;
                lifetime.Dispose();
            }
    }

    private void Notify()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposed || view.IsClosed) return;
        var snapshot = view.TextSnapshot;
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }

    public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        var current = Volatile.Read(ref result);
        if (disposed || !Enabled || current is null || spans.Count == 0 || spans[0].Snapshot != current.Snapshot) yield break;
        foreach (var span in spans)
        {
            var low = 0; var high = current.Spans.Count;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (current.Spans[mid].Start + current.Spans[mid].Length <= span.Start.Position) low = mid + 1; else high = mid;
            }
            for (var i = low; i < current.Spans.Count && current.Spans[i].Start < span.End.Position; i++)
            {
                var item = current.Spans[i];
                if (tags[(int)item.Kind] is ClassificationTag tag)
                    yield return new TagSpan<ClassificationTag>(new SnapshotSpan(current.Snapshot, item.Start, item.Length), tag);
            }
        }
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposed) return;
        disposed = true; request?.Cancel();
        Volatile.Write(ref result, null);
        view.TextBuffer.Changed -= OnChanged;
        view.Closed -= OnClosed;
        view.VisualElement.IsVisibleChanged -= OnVisibleChanged;
        ColoringSettings.Changed -= OnSettingsChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemChanged;
    }
}
