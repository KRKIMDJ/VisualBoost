using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VisualBoost.CodeGeneration;
using VisualBoost.Core.DocumentNavigation;
using VisualBoost.Services;

namespace VisualBoost.CommentLinks;

internal static class CommentLinkRuntime
{
    private static bool enabled = true;
    public static bool Enabled { get => enabled; set { if (enabled == value) return; enabled = value; Changed?.Invoke(); } }
    public static event Action? Changed;
    public static SolutionFileIndexService? Index { get; set; }
}

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("C/C++")]
[ContentType("CSharp")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class CommentLinkViewListener : IWpfTextViewCreationListener
{
    internal const string Layer = "VisualBoost.CommentFileLinks";
    [Export(typeof(AdornmentLayerDefinition)), Name(Layer), Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
    public AdornmentLayerDefinition? Definition = null;
    private readonly IClassifierAggregatorService classifiers;
    private readonly ITextDocumentFactoryService documents;
    [ImportingConstructor]
    public CommentLinkViewListener(IClassifierAggregatorService classifiers, ITextDocumentFactoryService documents)
    { this.classifiers = classifiers; this.documents = documents; }
    public void TextViewCreated(IWpfTextView view) => view.Properties.GetOrCreateSingletonProperty(() => new Session(view, classifiers.GetClassifier(view.TextBuffer), documents));

    private sealed class Session
    {
        private readonly IWpfTextView view;
        private readonly IClassifier classifier;
        private readonly ITextDocumentFactoryService documents;
        private readonly IAdornmentLayer layer;
        private readonly CommentLinkTooltip tooltip;
        private readonly DispatcherTimer refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        private readonly List<(SnapshotSpan span, CommentFileReference reference)> links = new();
        private readonly CancellationTokenSource lifetime = new();
        private bool navigating;
        private Cursor? previousCursor;
        private bool ownsCursor;
        public Session(IWpfTextView view, IClassifier classifier, ITextDocumentFactoryService documents)
        {
            this.view = view; this.classifier = classifier; this.documents = documents; layer = view.GetAdornmentLayer(Layer);
            tooltip = new CommentLinkTooltip(view.VisualElement);
            view.LayoutChanged += OnLayout; classifier.ClassificationChanged += OnClassification;
            view.TextBuffer.Changed += OnBuffer; view.Closed += OnClosed;
            view.VisualElement.PreviewMouseLeftButtonDown += OnClick;
            view.VisualElement.MouseMove += OnMouse; view.VisualElement.MouseLeave += OnLeave;
            view.VisualElement.PreviewKeyUp += OnKeyUp;
            view.VisualElement.PreviewKeyDown += OnKeyDown; view.LostAggregateFocus += OnLostFocus;
            CommentLinkRuntime.Changed += OnOptions;
            refresh.Tick += OnRefresh; refresh.Start();
        }
        private void OnLayout(object? sender, TextViewLayoutChangedEventArgs e) { tooltip.Hide(); Schedule(); }
        [SuppressMessage("Usage", "VSTHRD001", Justification = "분류 이벤트는 UI 대기 없이 Dispatcher에 표시 갱신만 예약합니다.")]
        private void OnClassification(object? sender, ClassificationChangedEventArgs e)
        { if (!view.VisualElement.Dispatcher.HasShutdownStarted) _ = view.VisualElement.Dispatcher.BeginInvoke(new Action(Schedule)); }
        private void OnBuffer(object? sender, TextContentChangedEventArgs e)
        { if (view.VisualElement.Dispatcher.CheckAccess()) { tooltip.Hide(); links.Clear(); layer.RemoveAllAdornments(); Schedule(); } }
        private void Schedule() { if (!view.IsClosed && !refresh.IsEnabled) refresh.Start(); }
        private void OnOptions() { tooltip.Hide(); RestoreCursor(); Schedule(); }
        private void OnRefresh(object? sender, EventArgs e)
        {
            refresh.Stop(); links.Clear(); layer.RemoveAllAdornments();
            if (view.IsClosed || !CommentLinkRuntime.Enabled || view.TextViewLines is null) return;
            try
            {
                // 보이는 줄만 분류하며 디스크·솔루션 검색은 클릭할 때만 작업 스레드에서 수행합니다.
                var seen = new HashSet<Span>();
                foreach (var line in view.TextViewLines.Take(200).Where(l => l.Extent.Length <= 16384))
                foreach (var classified in classifier.GetClassificationSpans(line.Extent))
                {
                    if (!classified.ClassificationType.IsOfType("comment") && classified.ClassificationType.Classification.IndexOf("comment", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var intersection = classified.Span.Intersection(line.Extent);
                    if (intersection is null || !seen.Add(intersection.Value.Span)) continue;
                    foreach (var reference in CommentFileReferences.Parse(intersection.Value.GetText()))
                    {
                        if (links.Count >= 256) return;
                        var span = new SnapshotSpan(view.TextSnapshot, intersection.Value.Start.Position + reference.Start, reference.Length);
                        links.Add((span, reference));
                        foreach (var bounds in line.GetNormalizedTextBounds(span))
                        {
                            var dark = view.Background is SolidColorBrush brush && brush.Color.R + brush.Color.G + brush.Color.B < 384;
                            var underline = new Line { X1 = bounds.Left, X2 = bounds.Right, Y1 = bounds.Bottom - 1, Y2 = bounds.Bottom - 1,
                                Stroke = dark ? Brushes.LightSkyBlue : Brushes.RoyalBlue, StrokeThickness = 1, IsHitTestVisible = false };
                            layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, underline, null);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is System.Text.RegularExpressions.RegexMatchTimeoutException)
            { links.Clear(); layer.RemoveAllAdornments(); ActivityLog.LogWarning("VisualBoost/CommentLinks", exception.Message); }
        }
        private (SnapshotSpan span, CommentFileReference reference)? Hit(Point position)
        {
            if (!CommentLinkRuntime.Enabled || view.IsClosed || view.TextViewLines is null) return null;
            var line = view.TextViewLines.GetTextViewLineContainingYCoordinate(position.Y + view.ViewportTop);
            var point = line?.GetBufferPositionFromXCoordinate(position.X + view.ViewportLeft);
            if (point is null) return null;
            foreach (var link in links) if (link.span.Snapshot == view.TextSnapshot && link.span.Contains(point.Value)) return link;
            return null;
        }
        private void OnMouse(object sender, MouseEventArgs e)
        {
            var position = e.GetPosition(view.VisualElement); var link = Hit(position);
            tooltip.Update(link is null ? null : link.Value.span.Start.Position + ":" + link.Value.span.Length, position);
            var active = Keyboard.Modifiers == ModifierKeys.Control && link is not null;
            if (active && !ownsCursor) { previousCursor = view.VisualElement.Cursor; view.VisualElement.Cursor = Cursors.Hand; ownsCursor = true; }
            else if (!active) RestoreCursor();
        }
        private void RestoreCursor()
        { if (ownsCursor && view.VisualElement.Cursor == Cursors.Hand) view.VisualElement.Cursor = previousCursor; ownsCursor = false; }
        private void OnLeave(object sender, MouseEventArgs e) { tooltip.Hide(); RestoreCursor(); }
        private void OnLostFocus(object? sender, EventArgs e) { tooltip.Hide(); RestoreCursor(); }
        private void OnKeyDown(object sender, KeyEventArgs e) => tooltip.Hide();
        private void OnKeyUp(object sender, KeyEventArgs e) { if (Keyboard.Modifiers != ModifierKeys.Control) RestoreCursor(); }
        private void OnClick(object sender, MouseButtonEventArgs e) { tooltip.Hide(); ClickAsync(e).FileAndForget("VisualBoost/CommentLinks"); }
        private async Task ClickAsync(MouseButtonEventArgs e)
        {
            if (e.Handled || navigating || e.ClickCount != 1 || Keyboard.Modifiers != ModifierKeys.Control) return;
            var position = e.GetPosition(view.VisualElement); var link = Hit(position);
            if (link is null) return;
            e.Handled = true; navigating = true;
            try { await NavigateAsync(link.Value.span, link.Value.reference, position); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception exception)
            {
                // 입력 이벤트의 비동기 경계에서 실패를 숨기지 않고 로그에 남깁니다.
                ActivityLog.LogWarning("VisualBoost/CommentLinks", exception.ToString());
                await StatusAsync("파일 링크를 열지 못했습니다: " + exception.Message);
            }
            finally { navigating = false; if (view.IsClosed) lifetime.Dispose(); }
        }
        private async Task NavigateAsync(SnapshotSpan original, CommentFileReference reference, Point position)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(lifetime.Token);
            var index = CommentLinkRuntime.Index;
            if (index is null || !documents.TryGetTextDocument(view.TextBuffer, out var document)) { await StatusAsync("열린 솔루션과 저장 경로가 있는 코드 문서가 필요합니다."); return; }
            var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SDTE)) as DTE2;
            if (dte is null) return;
            var sourcePath = document.FilePath; var solution = dte.Solution.FullName;
            var paths = await Task.Run(() =>
            {
                var local = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath)!, reference.Path.Replace('/', '\\')));
                var candidates = index.FindByStem(System.IO.Path.GetFileNameWithoutExtension(reference.Path));
                return CommentFileReferences.Resolve(sourcePath, reference.Path, candidates.Concat(File.Exists(local) ? new[] { local } : Array.Empty<string>())).Where(File.Exists).ToArray();
            }, lifetime.Token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(lifetime.Token);
            if (view.TextSnapshot != original.Snapshot || document.FilePath != sourcePath || dte.Solution.FullName != solution || CommentLinkRuntime.Index != index) return;
            if (paths.Length == 0) { await StatusAsync("파일을 찾지 못했습니다: " + reference.Path + " · 인덱싱 상태와 경로를 확인하세요."); return; }
            var path = paths[0];
            if (paths.Length > 1)
            {
                var actions = paths.Select((p, i) => new DocumentCodeAction(i.ToString(), p, "이 파일의 " + reference.Line + "번째 라인으로 이동")).ToArray();
                var selected = await new DocumentCodeActionMenu().ShowAsync(view.VisualElement, position, actions, lifetime.Token);
                if (selected is null) return;
                path = paths[int.Parse(selected.Id)];
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(lifetime.Token);
            if (!CommentLinkRuntime.Enabled || view.TextSnapshot != original.Snapshot || document.FilePath != sourcePath || dte.Solution.FullName != solution) return;
            // 실행 가능한 파일 형식은 파서에서 제외하고 항상 텍스트 편집기로 엽니다.
            var window = dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView); window?.Activate();
            if (dte.ActiveDocument?.Selection is TextSelection selection)
            {
                var end = selection.BottomPoint.CreateEditPoint(); end.EndOfDocument();
                var target = Math.Min(reference.Line, end.Line); selection.GotoLine(target, false);
                if (target != reference.Line) await StatusAsync("요청한 라인이 파일 범위를 넘어 마지막 라인으로 이동했습니다.");
            }
        }
        private static async Task StatusAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var status = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            status?.SetText("VisualBoost: " + message);
        }
        private void OnClosed(object? sender, EventArgs e)
        {
            lifetime.Cancel(); refresh.Stop(); refresh.Tick -= OnRefresh;
            CommentLinkRuntime.Changed -= OnOptions;
            view.LayoutChanged -= OnLayout; classifier.ClassificationChanged -= OnClassification; view.TextBuffer.Changed -= OnBuffer;
            view.Closed -= OnClosed; view.VisualElement.PreviewMouseLeftButtonDown -= OnClick;
            view.VisualElement.MouseMove -= OnMouse; view.VisualElement.MouseLeave -= OnLeave; view.VisualElement.PreviewKeyUp -= OnKeyUp;
            view.VisualElement.PreviewKeyDown -= OnKeyDown; view.LostAggregateFocus -= OnLostFocus; tooltip.Dispose();
            RestoreCursor(); links.Clear(); layer.RemoveAllAdornments(); (classifier as IDisposable)?.Dispose();
            if (!navigating) lifetime.Dispose();
        }
    }
}
