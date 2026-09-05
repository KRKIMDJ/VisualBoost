using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using VisualBoost.Coloring;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Coloring;

namespace VisualBoost.UI;

public static class SearchPalette
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(SearchPalette), new PropertyMetadata(false, OnTrackChanged));
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.RegisterAttached(
        "Revision", typeof(int), typeof(SearchPalette), new PropertyMetadata(0));
    private static readonly DependencyProperty MonitorProperty = DependencyProperty.RegisterAttached(
        "Monitor", typeof(PaletteMonitor), typeof(SearchPalette));
    public static bool GetTrack(DependencyObject target) => (bool)target.GetValue(TrackProperty);
    public static void SetTrack(DependencyObject target, bool value) => target.SetValue(TrackProperty, value);
    public static int GetRevision(DependencyObject target) => (int)target.GetValue(RevisionProperty);
    public static void SetRevision(DependencyObject target, int value) => target.SetValue(RevisionProperty, value);

    private static void OnTrackChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        (element.GetValue(MonitorProperty) as PaletteMonitor)?.Dispose();
        element.SetValue(MonitorProperty, (bool)e.NewValue ? new PaletteMonitor(element) : null);
    }

    private sealed class PaletteMonitor : IDisposable
    {
        private readonly FrameworkElement element;
        private bool attached;

        public PaletteMonitor(FrameworkElement element)
        {
            this.element = element;
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            if (element.IsLoaded) Attach();
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => Attach();
        private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();
        private void Attach()
        {
            if (attached) return;
            attached = true;
            ColoringSettings.Changed += OnChanged;
            SystemParameters.StaticPropertyChanged += OnSystemChanged;
            Refresh();
        }
        private void Detach()
        {
            attached = false;
            ColoringSettings.Changed -= OnChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemChanged;
        }
        private void OnChanged(object? sender, EventArgs e) => Refresh();
        private void OnSystemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) Refresh();
        }

        [SuppressMessage("Usage", "VSTHRD001", Justification = "WPF 리소스 변경의 재평가를 비동기로 알리며 동기 대기를 하지 않습니다.")]
        private void Refresh()
        {
            if (!element.Dispatcher.CheckAccess())
            {
                if (!element.Dispatcher.HasShutdownStarted) _ = element.Dispatcher.BeginInvoke(new Action(Refresh));
                return;
            }
            if (attached) SetRevision(element, unchecked(GetRevision(element) + 1));
        }
        public void Dispose()
        {
            Detach();
            element.Loaded -= OnLoaded;
            element.Unloaded -= OnUnloaded;
        }
    }
}

public sealed class SymbolColorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var fallback = values.Length > 2 && values[2] is Brush foreground ? foreground : SystemColors.ControlTextBrush;
        if (values.Length < 5 || values[1] is true || !ColoringSettings.Current.Enabled || SystemParameters.HighContrast)
            return fallback;
        var kind = values[0] is SourceSymbolKind sourceKind ? sourceKind switch
        {
            SourceSymbolKind.Type => (SemanticColorKind?)SemanticColorKind.Type,
            SourceSymbolKind.Function => SemanticColorKind.Function,
            SourceSymbolKind.Variable => SemanticColorKind.Variable,
            SourceSymbolKind.Macro => SemanticColorKind.Macro,
            _ => null,
        } : null;
        if (!kind.HasValue) return fallback;
        var background = values[3] is SolidColorBrush brush ? brush.Color : Colors.White;
        var rgb = ColoringSettings.Current.GetColor(kind.Value, SemanticColorPalette.IsDark(background.R, background.G, background.B));
        var result = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        result.Freeze();
        return result;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
