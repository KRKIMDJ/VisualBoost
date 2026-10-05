using System;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Coloring;

internal sealed class ColoringSettings
{
    public static ColoringSettings Current { get; private set; } = new(false, new string[8]);
    public static event EventHandler? Changed;
    private readonly string[] overrides;

    public ColoringSettings(bool enabled, string[] overrides, ColoringColorSource source = ColoringColorSource.Palette, bool quickColoring = true)
    {
        if (overrides.Length != 8 && overrides.Length != 12) throw new ArgumentException("테마별 색상 12개가 필요합니다.", nameof(overrides));
        Enabled = enabled;
        Source = source;
        QuickColoring = quickColoring;
        this.overrides = new string[12];
        if (overrides.Length == 8)
        {
            // 구 팔레트의 밝은 테마가 새 그룹 슬롯으로 밀려 들어가지 않게 이관합니다.
            Array.Copy(overrides, 0, this.overrides, 0, 4);
            Array.Copy(overrides, 4, this.overrides, 6, 4);
        }
        else Array.Copy(overrides, this.overrides, 12);
    }

    public bool Enabled { get; }
    public bool QuickColoring { get; }
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
        var text = overrides[(dark ? 0 : SemanticColorPalette.KindCount) + (int)kind];
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
