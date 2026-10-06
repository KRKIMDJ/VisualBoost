using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VisualBoost.Coloring;
using VisualBoost.Core.Coloring;
using VisualBoost.UI;

/// <summary>정의 후보 창 코드 셀: 창 글꼴 상속, 편집기 구문 색, 대상 심볼 종류 색, 선택 행과 설정 변경 반영.</summary>
internal static class CodePreviewTests
{
    public static void Run(ItemsControl items)
    {
        var settings = ColoringSettings.Current;
        try
        {
            ColoringSettings.Publish(new ColoringSettings(true, new string[8]));
            var keyword = new SolidColorBrush(Colors.Orange);
            keyword.Freeze();
            // 편집기 구문 색 게시는 열린 창에 바로 반영되어야 합니다(SearchPalette 갱신 번호를 거쳐 다시 그림).
            CodePreviewStyle.Publish(new Dictionary<CodePreviewKind, Brush> { [CodePreviewKind.Keyword] = keyword });
            Program.Pump();
            var rows = Program.Descendants<ListViewItem>(items).ToArray();
            var plain = Code(rows.First(row => !row.IsSelected));
            var selected = Code(rows.First(row => row.IsSelected));
            foreach (var block in new[] { plain, selected })
            {
                Check(Joined(block) == "return SetMovementMode();", "코드 셀 전체 문자열");
                Check(Bold(block) == "SetMovementMode", "코드 셀은 일치 구간만 굵게");
                // 코드 줄도 창의 다른 글자와 같은 환경 글꼴·목록 글씨 크기를 상속합니다.
                Check(block.ReadLocalValue(TextBlock.FontFamilyProperty) == DependencyProperty.UnsetValue && Equals(block.FontFamily, items.FontFamily),
                    "코드 셀은 창 글꼴 상속");
                Check(block.ReadLocalValue(TextBlock.FontSizeProperty) == DependencyProperty.UnsetValue && block.FontSize == items.FontSize, "코드 셀은 창 글씨 크기 상속");
            }

            Check(ColorOf(plain, "return") == Colors.Orange, "키워드는 편집기 구문 색");
            Check(ColorOf(plain, "SetMovementMode") == Color.FromRgb(0xF2, 0xCB, 0x8D), "일치 구간은 대상 심볼 종류(함수)의 의미 기반 색");
            Check(ColorOf(plain, "();") is null, "구두점은 행 전경색");
            Check(selected.Inlines.OfType<Run>().All(run => run.ReadLocalValue(TextElement.ForegroundProperty) == DependencyProperty.UnsetValue),
                "선택 행은 색을 빼고 강조 글자색을 상속");

            ColoringSettings.Publish(new ColoringSettings(false, new string[8]));
            Program.Pump();
            Check(ColorOf(plain, "SetMovementMode") is null && ColorOf(plain, "return") == Colors.Orange, "의미 기반 색상을 끄면 식별자 색만 빠짐");
            Console.WriteLine("PASS: 코드 미리보기 창 글꼴 상속·구문 색·대상 심볼 색, 선택 행과 설정 변경 반영");
        }
        finally
        {
            CodePreviewStyle.Publish(new Dictionary<CodePreviewKind, Brush>());
            ColoringSettings.Publish(settings);
            Program.Pump();
        }
    }

    private static TextBlock Code(DependencyObject row) =>
        Program.Descendants<TextBlock>(row).First(text => Joined(text).StartsWith("return ", StringComparison.Ordinal));

    private static string Joined(TextBlock text) => string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text));

    private static string Bold(TextBlock text) =>
        string.Concat(text.Inlines.OfType<Run>().Where(run => run.FontWeight == FontWeights.Bold).Select(run => run.Text));

    /// <summary>해당 글자를 담은 Run에 직접 지정한 색입니다. 상속하면 null입니다.</summary>
    private static Color? ColorOf(TextBlock text, string part)
    {
        var run = text.Inlines.OfType<Run>().First(item => item.Text == part);
        return run.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush brush ? brush.Color : null;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
