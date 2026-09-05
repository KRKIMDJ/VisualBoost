using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Coloring;

// VS C++가 제공하는 의미 분류의 표시 속성만 변경합니다. 소스 토큰은 재분석하지 않습니다.
internal sealed class CppColorFormatSession
{
    private static readonly (string Name, SemanticColorKind Kind)[] Formats =
    {
        ("CppTypeSemanticTokenFormat", SemanticColorKind.Type),
        ("CppRefTypeSemanticTokenFormat", SemanticColorKind.Type),
        ("CppValueTypeSemanticTokenFormat", SemanticColorKind.Type),
        ("CppEnumSemanticTokenFormat", SemanticColorKind.Type),
        ("CppClassTemplateSemanticTokenFormat", SemanticColorKind.Type),
        ("CppGenericTypeSemanticTokenFormat", SemanticColorKind.Type),
        ("CppGlobalVariableSemanticTokenFormat", SemanticColorKind.Variable),
        ("CppLocalVariableSemanticTokenFormat", SemanticColorKind.Variable),
        ("CppParameterSemanticTokenFormat", SemanticColorKind.Variable),
        ("CppMemberFieldSemanticTokenFormat", SemanticColorKind.Variable),
        ("CppStaticMemberFieldSemanticTokenFormat", SemanticColorKind.Variable),
        ("CppPropertySemanticTokenFormat", SemanticColorKind.Variable),
        ("CppEventSemanticTokenFormat", SemanticColorKind.Variable),
        ("CppFunctionSemanticTokenFormat", SemanticColorKind.Function),
        ("CppMemberFunctionSemanticTokenFormat", SemanticColorKind.Function),
        ("CppStaticMemberFunctionSemanticTokenFormat", SemanticColorKind.Function),
        ("CppFunctionTemplateSemanticTokenFormat", SemanticColorKind.Function),
        ("CppMacroSemanticTokenFormat", SemanticColorKind.Macro),
    };
    private static readonly string[] ForegroundKeys =
        { EditorFormatDefinition.ForegroundColorId, EditorFormatDefinition.ForegroundBrushId };
    private readonly IEditorFormatMap map;
    private readonly Dictionary<string, ResourceDictionary> originals = new();
    private readonly Dictionary<string, Color> applied = new();

    public CppColorFormatSession(IEditorFormatMap map) => this.map = map;
    public bool IsApplying { get; private set; }
    public int AppliedCount => applied.Count;

    public void Update(ColoringSettings settings, bool dark, bool highContrast)
    {
        IsApplying = true;
        map.BeginBatchUpdate();
        try
        {
            foreach (var format in Formats)
            {
                var current = map.GetProperties(format.Name);
                if (applied.TryGetValue(format.Name, out var last) && !HasColor(current, last))
                {
                    // 테마·기본 옵션에서 들어온 새 색상을 복원 기준으로 보존합니다.
                    originals[format.Name] = Clone(current);
                    applied.Remove(format.Name);
                }
                if (!settings.Enabled || highContrast)
                {
                    Restore(format.Name, current);
                    continue;
                }
                // 해당 VS 버전에서 제공하지 않는 분류를 새로 만들어 내지 않습니다.
                if (current.Count == 0) continue;
                if (!originals.ContainsKey(format.Name)) originals[format.Name] = Clone(current);
                var rgb = settings.GetColor(format.Kind, dark);
                var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                if (HasColor(current, color)) continue;
                var replacement = Clone(current);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                replacement[EditorFormatDefinition.ForegroundColorId] = color;
                replacement[EditorFormatDefinition.ForegroundBrushId] = brush;
                applied[format.Name] = color;
                map.SetProperties(format.Name, replacement);
            }
        }
        finally
        {
            try { map.EndBatchUpdate(); }
            finally { IsApplying = false; }
        }
    }

    public void Restore() => Update(new ColoringSettings(false, new string[8]), false, false);

    private void Restore(string name, ResourceDictionary current)
    {
        if (applied.ContainsKey(name) && originals.TryGetValue(name, out var original))
        {
            var replacement = Clone(current);
            foreach (var key in ForegroundKeys)
            {
                replacement.Remove(key);
                if (original.Contains(key)) replacement[key] = original[key];
            }
            map.SetProperties(name, replacement);
        }
        originals.Remove(name);
        applied.Remove(name);
    }

    private static bool HasColor(ResourceDictionary properties, Color color) =>
        properties[EditorFormatDefinition.ForegroundColorId] is Color value && value == color &&
        properties[EditorFormatDefinition.ForegroundBrushId] is SolidColorBrush brush && brush.Color == color;

    private static ResourceDictionary Clone(ResourceDictionary source)
    {
        var result = new ResourceDictionary();
        foreach (DictionaryEntry entry in source) result[entry.Key] = entry.Value;
        return result;
    }
}
