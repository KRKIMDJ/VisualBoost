using System;
using System.Globalization;

namespace VisualBoost.Core.Coloring;

// 기존 네 종류의 숫자는 저장 설정과 표시 코드의 호환성을 위해 유지합니다.
public enum SemanticColorKind { Type, Variable, Function, Macro, EnumMember, Namespace }

public static class SemanticColorPalette
{
    public const int KindCount = 6;
    public static string Default(SemanticColorKind kind, bool dark) => (kind, dark) switch
    {
        (SemanticColorKind.Type, true) => "#68D5C4",
        (SemanticColorKind.Variable, true) => "#B4D8FA",
        (SemanticColorKind.Function, true) => "#F2CB8D",
        (SemanticColorKind.Macro, true) => "#D2AAF5",
        (SemanticColorKind.EnumMember, true) => "#E6B98B",
        (SemanticColorKind.Namespace, true) => "#91B8D9",
        (SemanticColorKind.Type, false) => "#006B5A",
        (SemanticColorKind.Variable, false) => "#205AA7",
        (SemanticColorKind.Function, false) => "#845114",
        (SemanticColorKind.Macro, false) => "#7842A0",
        (SemanticColorKind.EnumMember, false) => "#855321",
        (SemanticColorKind.Namespace, false) => "#386887",
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
