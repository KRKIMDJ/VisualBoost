using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using VisualBoost.Coloring;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Coloring;

namespace VisualBoost.UI;

/// <summary>
/// 결과 목록 코드 미리보기의 글꼴과 색입니다. 구문 색(키워드·주석 등)과 글꼴은 텍스트 편집기 서식에서 게시받고,
/// 식별자 색은 의미 기반 색상 설정(<see cref="ColoringSettings"/>)을 씁니다. VS SDK에 의존하지 않아 테스트에서도 씁니다. UI thread에서만 씁니다.
/// </summary>
internal static class CodePreviewStyle
{
    /// <summary>편집기 서식을 아직 읽지 못했을 때의 코드 글꼴입니다.</summary>
    public static readonly FontFamily DefaultFontFamily = new("Consolas");

    private static readonly Dictionary<int, Brush> SemanticBrushes = new();
    private static IReadOnlyDictionary<CodePreviewKind, Brush> syntaxBrushes = new Dictionary<CodePreviewKind, Brush>();

    /// <summary>글꼴이나 구문 색이 바뀌었습니다. 열린 목록은 <see cref="SearchPalette"/>를 거쳐 다시 그립니다.</summary>
    public static event EventHandler? Changed;

    public static FontFamily FontFamily { get; private set; } = DefaultFontFamily;

    /// <summary>텍스트 편집기의 글꼴과 구문 색을 게시합니다. 색이 비어 있는 종류는 행 전경색을 씁니다.</summary>
    public static void Publish(FontFamily? fontFamily, IReadOnlyDictionary<CodePreviewKind, Brush> syntax)
    {
        FontFamily = fontFamily ?? DefaultFontFamily;
        syntaxBrushes = syntax ?? throw new ArgumentNullException(nameof(syntax));
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>구간 종류의 색입니다. 고대비 모드이거나 정할 색이 없으면 null(행 전경색 상속)입니다.</summary>
    public static Brush? BrushFor(CodePreviewKind kind, bool darkBackground)
    {
        if (SystemParameters.HighContrast) return null;
        return kind switch
        {
            CodePreviewKind.Type => Semantic(SemanticColorKind.Type, darkBackground),
            CodePreviewKind.Variable => Semantic(SemanticColorKind.Variable, darkBackground),
            CodePreviewKind.Function => Semantic(SemanticColorKind.Function, darkBackground),
            CodePreviewKind.Macro => Semantic(SemanticColorKind.Macro, darkBackground),
            CodePreviewKind.EnumMember => Semantic(SemanticColorKind.EnumMember, darkBackground),
            CodePreviewKind.Namespace => Semantic(SemanticColorKind.Namespace, darkBackground),
            _ => syntaxBrushes.TryGetValue(kind, out var brush) ? brush : null,
        };
    }

    /// <summary>탐색 대상 심볼 종류의 색입니다. 의미 기반 색상 그룹에 대응하지 않으면 null입니다.</summary>
    public static Brush? BrushFor(SourceSymbolKind kind, bool darkBackground) =>
        SystemParameters.HighContrast || SymbolColorConverter.ColorKindOf(kind) is not { } colorKind ? null : Semantic(colorKind, darkBackground);

    private static Brush? Semantic(SemanticColorKind kind, bool dark)
    {
        var settings = ColoringSettings.Current;
        if (!settings.Enabled) return null;
        var rgb = settings.GetColor(kind, dark);
        if (SemanticBrushes.TryGetValue(rgb, out var cached)) return cached;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        // 사용자가 색을 바꿀 때마다 늘어나지만 6그룹 × 2테마 단위라 실사용에서 작게 머뭅니다.
        SemanticBrushes[rgb] = brush;
        return brush;
    }
}
