using System;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace VisualBoost.CodeGeneration;

[Export(typeof(IWpfTextViewCreationListener)), ContentType("C/C++"), TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class QuickIncludeDiagnosticListener : IWpfTextViewCreationListener
{
    private readonly IViewTagAggregatorFactoryService factory;
    [ImportingConstructor]
    public QuickIncludeDiagnosticListener(IViewTagAggregatorFactoryService factory) => this.factory = factory;
    public void TextViewCreated(IWpfTextView view) => QuickIncludeDiagnostics.Get(view, factory);
}

internal sealed class QuickIncludeDiagnostics
{
    private readonly ITagAggregator<IErrorTag> tags;
    private QuickIncludeDiagnostics(IWpfTextView view, IViewTagAggregatorFactoryService factory)
    {
        // 명령을 누르는 순간 새로 생성하지 않아 비동기 진단 태그가 준비될 시간을 확보합니다.
        tags = factory.CreateTagAggregator<IErrorTag>(view);
        view.Closed += OnClosed;
        void OnClosed(object sender, EventArgs e) { view.Closed -= OnClosed; tags.Dispose(); }
    }
    public static QuickIncludeDiagnostics Get(IWpfTextView view, IViewTagAggregatorFactoryService factory) =>
        view.Properties.GetOrCreateSingletonProperty(() => new QuickIncludeDiagnostics(view, factory));
    public bool HasError(SnapshotSpan span) => tags.GetTags(span).Any(t =>
        t.Tag.ErrorType.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 && t.Span.GetSpans(span.Snapshot).Any(s => s.IntersectsWith(span)));
}
