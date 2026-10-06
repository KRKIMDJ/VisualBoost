using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using VisualBoost.Coloring;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Coloring;

namespace VisualBoost.UI;

/// <summary>
/// 결과 목록 코드 미리보기의 색과 식별자 이름 판정입니다. 구문 색(키워드·주석 등)은 텍스트 편집기 서식에서 게시받고,
/// 식별자 색은 의미 기반 색상 설정(<see cref="ColoringSettings"/>)을 씁니다. 글꼴은 정하지 않습니다. 코드 줄도 창의 다른 글자와 같이
/// 환경 글꼴·목록 글씨 크기를 상속해 모든 VisualBoost 창이 한 글꼴로 보이게 합니다. VS SDK에 의존하지 않아 테스트에서도 씁니다. UI thread에서만 씁니다.
/// </summary>
internal static class CodePreviewStyle
{
    private static readonly Dictionary<int, Brush> SemanticBrushes = new();
    private static IReadOnlyDictionary<CodePreviewKind, Brush> syntaxBrushes = new Dictionary<CodePreviewKind, Brush>();

    /// <summary>구문 색이 바뀌었습니다. 열린 목록은 <see cref="SearchPalette"/>를 거쳐 다시 그립니다.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// 미리보기 식별자 이름의 종류를 알려 주는 판정입니다(<see cref="CppLinePreviewClassifier.Classify"/>). 줄 형태만으로 알 수 없는
    /// 타입·매크로 이름의 색을 보강합니다. 패키지가 시작할 때 Solution 이름 인덱스로 연결하고, 없으면(테스트·연결 전) 줄 형태만으로 분류합니다.
    /// 항목마다 처음 그릴 때 한 번 쓰므로, 연결이나 인덱스가 바뀌어도 이미 그린 항목은 결과를 다시 받을 때까지 그대로입니다.
    /// </summary>
    public static Func<string, CodePreviewKind?>? NameKind { get; set; }

    /// <summary>텍스트 편집기의 구문 색을 게시합니다. 색이 비어 있는 종류는 행 전경색을 씁니다.</summary>
    public static void Publish(IReadOnlyDictionary<CodePreviewKind, Brush> syntax)
    {
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
