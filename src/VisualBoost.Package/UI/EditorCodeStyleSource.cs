using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;
using VisualBoost.Core.Coloring;

namespace VisualBoost.UI;

/// <summary>
/// 텍스트 편집기 서식(<c>Tools &gt; Options &gt; 환경 &gt; 글꼴 및 색 &gt; 텍스트 편집기</c>)의 글꼴과 구문 색을
/// 코드 미리보기(<see cref="CodePreviewStyle"/>)에 게시합니다. 사용자가 서식을 바꾸면 다시 게시해 열린 목록에 반영합니다.
/// 코드 미리보기를 보이는 창을 처음 만들 때 시작하며, 이후에는 VS 종료까지 서식 맵 하나만 구독합니다. UI thread에서만 씁니다.
/// </summary>
internal static class EditorCodeStyleSource
{
    // 편집기 기본 서식 맵의 이름입니다. 일반 텍스트 편집기 보기가 쓰는 글꼴·색이 여기에 있습니다.
    private const string TextAppearanceCategory = "text";

    private static readonly (CodePreviewKind Kind, string Classification)[] SyntaxClassifications =
    {
        (CodePreviewKind.Keyword, PredefinedClassificationTypeNames.Keyword),
        (CodePreviewKind.Comment, PredefinedClassificationTypeNames.Comment),
        (CodePreviewKind.String, PredefinedClassificationTypeNames.String),
        (CodePreviewKind.Number, PredefinedClassificationTypeNames.Number),
        (CodePreviewKind.Preprocessor, PredefinedClassificationTypeNames.PreprocessorKeyword),
    };

    private static IClassificationFormatMap? formatMap;
    private static IClassificationTypeRegistryService? registry;

    public static void EnsureStarted()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (formatMap is not null) return;
        // MEF 구성을 쓸 수 없으면 기본 코드 글꼴과 의미 기반 색상만으로 표시합니다. 다음 창을 만들 때 다시 시도합니다.
        if (ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) is not IComponentModel model) return;
        registry = model.GetService<IClassificationTypeRegistryService>();
        formatMap = model.GetService<IClassificationFormatMapService>().GetClassificationFormatMap(TextAppearanceCategory);
        formatMap.ClassificationFormatMappingChanged += (_, _) => Publish();
        Publish();
    }

    private static void Publish()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (formatMap is null || registry is null) return;
        var brushes = new Dictionary<CodePreviewKind, Brush>();
        foreach (var (kind, name) in SyntaxClassifications)
        {
            var type = registry.GetClassificationType(name);
            if (type is null) continue;
            var properties = formatMap.GetTextProperties(type);
            if (properties.ForegroundBrushEmpty) continue;
            var brush = properties.ForegroundBrush.CloneCurrentValue();
            brush.Freeze();
            brushes[kind] = brush;
        }

        CodePreviewStyle.Publish(formatMap.DefaultTextProperties.Typeface.FontFamily, brushes);
    }
}
