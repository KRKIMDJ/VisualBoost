using System;
using System.ComponentModel.Composition;
using System.Windows;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using Microsoft.VisualStudio.Utilities;

namespace VisualBoost.DocumentNavigation;

[Export(typeof(IWpfTextViewMarginProvider))]
[Name(MarginName)]
[MarginContainer(PredefinedMarginNames.Top)]
[ContentType("C/C++")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class DocumentNavigationMarginProvider : IWpfTextViewMarginProvider
{
    internal const string MarginName = "VisualBoost.DocumentNavigation";
    [Import] internal IOutliningManagerService Outlining = null!;
    [Import] internal IVsEditorAdaptersFactoryService Adapters = null!;
    [Import] internal ITextDocumentFactoryService Documents = null!;
    public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin container)
    {
        var session = host.TextView.Properties.GetOrCreateSingletonProperty(() =>
            new DocumentNavigationSession(host.TextView, Outlining, Adapters, Documents));
        return new Margin(session.Bar);
    }
    private sealed class Margin : IWpfTextViewMargin
    {
        private bool disposed;
        private readonly DocumentNavigationBar bar;
        public Margin(DocumentNavigationBar bar) => this.bar = bar;
        public FrameworkElement VisualElement => bar;
        public double MarginSize => Enabled ? bar.ActualHeight : 0;
        public bool Enabled => !disposed && bar.Visibility == Visibility.Visible;
        public ITextViewMargin? GetTextViewMargin(string name) => string.Equals(name, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;
        // 세션은 뷰 수명에 묶여 있습니다. margin 재배치가 분석 세션을 파괴하지 않습니다.
        public void Dispose() => disposed = true;
    }
}
