using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Coloring;

internal static class SemanticClassificationTypes
{
    [Export, Name("VisualBoost.Dark.Type"), BaseDefinition("identifier")]
    internal static ClassificationTypeDefinition DarkType = null!;
    [Export, Name("VisualBoost.Dark.Variable"), BaseDefinition("identifier")]
    internal static ClassificationTypeDefinition DarkVariable = null!;
    [Export, Name("VisualBoost.Dark.Function"), BaseDefinition("identifier")]
    internal static ClassificationTypeDefinition DarkFunction = null!;
    [Export, Name("VisualBoost.Dark.Macro"), BaseDefinition("identifier")]
    internal static ClassificationTypeDefinition DarkMacro = null!;
    [Export, Name("VisualBoost.Light.Type"), BaseDefinition("identifier")]
    internal static ClassificationTypeDefinition LightType = null!;
    [Export, Name("VisualBoost.Light.Variable"), BaseDefinition("identifier")]
    internal static ClassificationTypeDefinition LightVariable = null!;
    [Export, Name("VisualBoost.Light.Function"), BaseDefinition("identifier")]
    internal static ClassificationTypeDefinition LightFunction = null!;
    [Export, Name("VisualBoost.Light.Macro"), BaseDefinition("identifier")]
    internal static ClassificationTypeDefinition LightMacro = null!;
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Dark.Type")]
[Name("VisualBoost.Dark.Type"), UserVisible(true)]
internal sealed class DarkTypeFormat : ClassificationFormatDefinition
{
    public DarkTypeFormat()
    {
        DisplayName = "VisualBoost 어두운 - 타입";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Type, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Dark.Variable")]
[Name("VisualBoost.Dark.Variable"), UserVisible(true)]
internal sealed class DarkVariableFormat : ClassificationFormatDefinition
{
    public DarkVariableFormat()
    {
        DisplayName = "VisualBoost 어두운 - 변수·매개변수";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Variable, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Dark.Function")]
[Name("VisualBoost.Dark.Function"), UserVisible(true)]
internal sealed class DarkFunctionFormat : ClassificationFormatDefinition
{
    public DarkFunctionFormat()
    {
        DisplayName = "VisualBoost 어두운 - 함수·메서드";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Function, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Dark.Macro")]
[Name("VisualBoost.Dark.Macro"), UserVisible(true)]
internal sealed class DarkMacroFormat : ClassificationFormatDefinition
{
    public DarkMacroFormat()
    {
        DisplayName = "VisualBoost 어두운 - 매크로";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Macro, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Light.Type")]
[Name("VisualBoost.Light.Type"), UserVisible(true)]
internal sealed class LightTypeFormat : ClassificationFormatDefinition
{
    public LightTypeFormat()
    {
        DisplayName = "VisualBoost 밝은 - 타입";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Type, false), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Light.Variable")]
[Name("VisualBoost.Light.Variable"), UserVisible(true)]
internal sealed class LightVariableFormat : ClassificationFormatDefinition
{
    public LightVariableFormat()
    {
        DisplayName = "VisualBoost 밝은 - 변수·매개변수";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Variable, false), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Light.Function")]
[Name("VisualBoost.Light.Function"), UserVisible(true)]
internal sealed class LightFunctionFormat : ClassificationFormatDefinition
{
    public LightFunctionFormat()
    {
        DisplayName = "VisualBoost 밝은 - 함수·메서드";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Function, false), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Light.Macro")]
[Name("VisualBoost.Light.Macro"), UserVisible(true)]
internal sealed class LightMacroFormat : ClassificationFormatDefinition
{
    public LightMacroFormat()
    {
        DisplayName = "VisualBoost 밝은 - 매크로";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Macro, false), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}
