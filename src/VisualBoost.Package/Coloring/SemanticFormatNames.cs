using VisualBoost.Core.Coloring;

namespace VisualBoost.Coloring;

internal static class SemanticFormatNames
{
    public static string Get(SemanticColorKind kind, bool dark) => "VisualBoost." + (dark ? "Dark." : "Light.") + kind;
}
