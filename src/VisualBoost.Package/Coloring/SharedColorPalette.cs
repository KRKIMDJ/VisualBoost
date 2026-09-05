using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Classification;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Coloring;

// Fonts and Colors는 읽기만 합니다. 별도 옵션의 색상으로 VS 저장 설정을 덮어쓰지 않습니다.
internal static class SharedColorPalette
{
    private static IEditorFormatMap? map;
    private static Dispatcher? dispatcher;
    private static DispatcherOperation? pending;
    private static int?[] colors = new int?[8];

    public static void Attach(IEditorFormatMap formatMap)
    {
        Detach();
        map = formatMap;
        dispatcher = Dispatcher.CurrentDispatcher;
        map.FormatMappingChanged += OnChanged;
        Refresh();
    }

    public static void Detach()
    {
        if (map is not null) map.FormatMappingChanged -= OnChanged;
        pending?.Abort();
        pending = null;
        map = null;
        dispatcher = null;
        colors = new int?[8];
    }

    public static int? GetColor(SemanticColorKind kind, bool dark) => colors[(dark ? 0 : 4) + (int)kind];

    [SuppressMessage("Usage", "VSTHRD001", Justification = "서식 저장소 변경 이벤트를 비동기로 병합하며 동기 대기를 하지 않습니다.")]
    private static void OnChanged(object? sender, FormatItemsEventArgs e)
    {
        if (e.ChangedItems.Count > 0 && !e.ChangedItems.Any(name => name.StartsWith("VisualBoost.", StringComparison.Ordinal))) return;
        var target = dispatcher;
        if (target is null || target.HasShutdownStarted) return;
        if (!target.CheckAccess()) { _ = target.BeginInvoke(new Action(() => OnChanged(sender, e))); return; }
        if (pending?.Status == DispatcherOperationStatus.Pending) return;
        pending = target.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
    }

    private static void Refresh()
    {
        if (map is null) return;
        var replacement = new int?[8];
        for (var index = 0; index < replacement.Length; index++)
        {
            var properties = map.GetProperties(SemanticFormatNames.Get((SemanticColorKind)(index % 4), index < 4));
            var color = properties[EditorFormatDefinition.ForegroundBrushId] is SolidColorBrush brush ? (Color?)brush.Color
                : properties[EditorFormatDefinition.ForegroundColorId] is Color value ? value : (Color?)null;
            if (color.HasValue && color.Value.A == 255)
                replacement[index] = (color.Value.R << 16) | (color.Value.G << 8) | color.Value.B;
        }
        if (replacement.SequenceEqual(colors)) return;
        colors = replacement;
        ColoringSettings.NotifyColorsChanged();
    }
}
