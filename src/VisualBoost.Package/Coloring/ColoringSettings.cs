using System;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Coloring;

internal sealed class ColoringSettings
{
    public static ColoringSettings Current { get; private set; } = new(false, new string[8]);
    public static event EventHandler? Changed;
    private readonly string[] overrides;

    public ColoringSettings(bool enabled, string[] overrides)
    {
        if (overrides.Length != 8) throw new ArgumentException("테마별 색상 8개가 필요합니다.", nameof(overrides));
        Enabled = enabled;
        this.overrides = (string[])overrides.Clone();
    }

    public bool Enabled { get; }

    public int GetColor(SemanticColorKind kind, bool dark)
    {
        var text = overrides[(dark ? 0 : 4) + (int)kind];
        if (!SemanticColorPalette.TryParse(text, out var rgb))
            SemanticColorPalette.TryParse(SemanticColorPalette.Default(kind, dark), out rgb);
        return rgb;
    }

    public static void Publish(ColoringSettings settings)
    {
        Current = settings;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
