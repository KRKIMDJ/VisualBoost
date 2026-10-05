using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Coloring;

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Dark.EnumMember")]
[Name("VisualBoost.Dark.EnumMember"), UserVisible(true)]
internal sealed class DarkEnumMemberFormat : ClassificationFormatDefinition
{
    public DarkEnumMemberFormat()
    {
        DisplayName = "VisualBoost 어두운 - 이넘 멤버";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.EnumMember, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Light.EnumMember")]
[Name("VisualBoost.Light.EnumMember"), UserVisible(true)]
internal sealed class LightEnumMemberFormat : ClassificationFormatDefinition
{
    public LightEnumMemberFormat()
    {
        DisplayName = "VisualBoost 밝은 - 이넘 멤버";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.EnumMember, false), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Dark.Namespace")]
[Name("VisualBoost.Dark.Namespace"), UserVisible(true)]
internal sealed class DarkNamespaceFormat : ClassificationFormatDefinition
{
    public DarkNamespaceFormat()
    {
        DisplayName = "VisualBoost 어두운 - 네임스페이스";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Namespace, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Light.Namespace")]
[Name("VisualBoost.Light.Namespace"), UserVisible(true)]
internal sealed class LightNamespaceFormat : ClassificationFormatDefinition
{
    public LightNamespaceFormat()
    {
        DisplayName = "VisualBoost 밝은 - 네임스페이스";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Namespace, false), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Fast.Type")]
[Name("VisualBoost.Fast.Type"), UserVisible(false), Order(Before = Priority.Default)]
internal sealed class FastTypeFormat : ClassificationFormatDefinition
{
    public FastTypeFormat()
    {
        DisplayName = "VisualBoost Fast Type";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Type, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Fast.Variable")]
[Name("VisualBoost.Fast.Variable"), UserVisible(false), Order(Before = Priority.Default)]
internal sealed class FastVariableFormat : ClassificationFormatDefinition
{
    public FastVariableFormat()
    {
        DisplayName = "VisualBoost Fast Variable";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Variable, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Fast.Function")]
[Name("VisualBoost.Fast.Function"), UserVisible(false), Order(Before = Priority.Default)]
internal sealed class FastFunctionFormat : ClassificationFormatDefinition
{
    public FastFunctionFormat()
    {
        DisplayName = "VisualBoost Fast Function";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Function, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Fast.Macro")]
[Name("VisualBoost.Fast.Macro"), UserVisible(false), Order(Before = Priority.Default)]
internal sealed class FastMacroFormat : ClassificationFormatDefinition
{
    public FastMacroFormat()
    {
        DisplayName = "VisualBoost Fast Macro";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Macro, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Fast.EnumMember")]
[Name("VisualBoost.Fast.EnumMember"), UserVisible(false), Order(Before = Priority.Default)]
internal sealed class FastEnumMemberFormat : ClassificationFormatDefinition
{
    public FastEnumMemberFormat()
    {
        DisplayName = "VisualBoost Fast EnumMember";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.EnumMember, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = "VisualBoost.Fast.Namespace")]
[Name("VisualBoost.Fast.Namespace"), UserVisible(false), Order(Before = Priority.Default)]
internal sealed class FastNamespaceFormat : ClassificationFormatDefinition
{
    public FastNamespaceFormat()
    {
        DisplayName = "VisualBoost Fast Namespace";
        SemanticColorPalette.TryParse(SemanticColorPalette.Default(SemanticColorKind.Namespace, true), out var rgb);
        ForegroundColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}


