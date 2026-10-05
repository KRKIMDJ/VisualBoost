using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Coloring;

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("C/C++")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class CppColorViewListener : IWpfTextViewCreationListener
{
    private readonly IEditorFormatMapService maps;
    private readonly Dictionary<IEditorFormatMap, ViewGroup> groups = new();

    [ImportingConstructor]
    public CppColorViewListener(IEditorFormatMapService maps) => this.maps = maps;

    public void TextViewCreated(IWpfTextView textView)
    {
        var map = maps.GetEditorFormatMap(textView);
        // 여러 문서가 같은 서식 맵을 공유하므로 원본 보관과 갱신을 맵당 한 번만 수행합니다.
        if (!groups.TryGetValue(map, out var group))
        {
            group = new ViewGroup(map, () => groups.Remove(map));
            groups.Add(map, group);
        }
        group.Add(textView);
    }

    private sealed class ViewGroup
    {
        private readonly IEditorFormatMap map;
        private readonly CppColorFormatSession session;
        private readonly Action remove;
        private readonly HashSet<IWpfTextView> views = new();
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        private DispatcherOperation? pending;
        private bool disposed;

        public ViewGroup(IEditorFormatMap map, Action remove)
        {
            this.map = map;
            this.remove = remove;
            session = new CppColorFormatSession(map);
            map.FormatMappingChanged += OnFormatChanged;
            ColoringSettings.Changed += OnSettingsChanged;
            VSColorTheme.ThemeChanged += OnThemeChanged;
            SystemParameters.StaticPropertyChanged += OnSystemPropertyChanged;
        }

        public void Add(IWpfTextView view)
        {
            if (!views.Add(view)) return;
            view.Closed += OnClosed;
            QueueUpdate();
        }

        private void OnFormatChanged(object sender, FormatItemsEventArgs e)
        {
            if (!session.IsApplying && (e.ChangedItems.Count == 0 || e.ChangedItems.Any(CppColorFormatSession.IsRelevant))) QueueUpdate();
        }
        private void OnSettingsChanged(object? sender, EventArgs e) => QueueUpdate();
        private void OnThemeChanged(ThemeChangedEventArgs e) => QueueUpdate();
        private void OnSystemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) QueueUpdate();
        }

        [SuppressMessage("Usage", "VSTHRD001", Justification = "서식 갱신을 병합하고 Background 대기열보다 우선 적용하며 동기 대기를 하지 않습니다.")]
        private void QueueUpdate()
        {
            if (!dispatcher.CheckAccess())
            {
                if (!dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(new Action(QueueUpdate));
                return;
            }
            if (disposed || views.Count == 0 || pending?.Status == DispatcherOperationStatus.Pending) return;
            pending = dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                pending = null;
                if (disposed) return;
                var view = views.FirstOrDefault(item => !item.IsClosed);
                if (view is null) return;
                var background = (view.Background as SolidColorBrush)?.Color ?? Colors.White;
                session.Update(ColoringSettings.Current,
                    SemanticColorPalette.IsDark(background.R, background.G, background.B), SystemParameters.HighContrast);
            }));
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (sender is not IWpfTextView view) return;
            view.Closed -= OnClosed;
            views.Remove(view);
            pending?.Abort();
            pending = null;
            if (views.Count > 0) { QueueUpdate(); return; }
            disposed = true;
            map.FormatMappingChanged -= OnFormatChanged;
            ColoringSettings.Changed -= OnSettingsChanged;
            VSColorTheme.ThemeChanged -= OnThemeChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemPropertyChanged;
            session.Restore();
            remove();
        }
    }
}
