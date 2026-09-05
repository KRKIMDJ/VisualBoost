using System;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Coloring;

internal sealed class ColoringSettings
{
    public static ColoringSettings Current { get; private set; } = new(false, new string[8]);
    public static event EventHandler? Changed;
    private readonly string[] overrides;

    public ColoringSettings(bool enabled, string[] overrides, ColoringColorSource source = ColoringColorSource.Palette)
    {
        if (overrides.Length != 8) throw new ArgumentException("테마별 색상 8개가 필요합니다.", nameof(overrides));
        Enabled = enabled;
        Source = source;
        this.overrides = (string[])overrides.Clone();
    }

    public bool Enabled { get; }
    public ColoringColorSource Source { get; }

    public int GetColor(SemanticColorKind kind, bool dark)
    {
        if (Source == ColoringColorSource.FontsAndColors)
        {
            var shared = SharedColorPalette.GetColor(kind, dark);
            if (shared.HasValue) return shared.Value;
            SemanticColorPalette.TryParse(SemanticColorPalette.Default(kind, dark), out var fallback);
            return fallback;
        }
        var text = overrides[(dark ? 0 : 4) + (int)kind];
        if (!SemanticColorPalette.TryParse(text, out var rgb))
            SemanticColorPalette.TryParse(SemanticColorPalette.Default(kind, dark), out rgb);
        return rgb;
    }

    public static void Publish(ColoringSettings settings)
    {
        Current = settings;
        NotifyColorsChanged();
    }

    internal static void NotifyColorsChanged()
    {
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
