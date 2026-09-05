using System;
using System.Globalization;

namespace VisualBoost.Core.Coloring;

public enum SemanticColorKind { Type, Variable, Function, Macro }

public static class SemanticColorPalette
{
    public static string Default(SemanticColorKind kind, bool dark) => (kind, dark) switch
    {
        (SemanticColorKind.Type, true) => "#68D5C4",
        (SemanticColorKind.Variable, true) => "#B4D8FA",
        (SemanticColorKind.Function, true) => "#F2CB8D",
        (SemanticColorKind.Macro, true) => "#D2AAF5",
        (SemanticColorKind.Type, false) => "#006B5A",
        (SemanticColorKind.Variable, false) => "#205AA7",
        (SemanticColorKind.Function, false) => "#845114",
        (SemanticColorKind.Macro, false) => "#7842A0",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryParse(string? text, out int rgb)
    {
        rgb = 0;
        var value = text?.Trim();
        return value is not null && value.Length == 7 && value[0] == '#' &&
            int.TryParse(value.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }

    public static bool IsDark(byte red, byte green, byte blue) =>
        (red * 299 + green * 587 + blue * 114) < 128000;
}
