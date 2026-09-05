using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace VisualBoost.UI;

public static class SearchTextHighlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(SearchTextHighlight), new PropertyMetadata(string.Empty, Refresh));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(SearchTextHighlight), new PropertyMetadata(string.Empty, Refresh));
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.RegisterAttached(
        "Suffix", typeof(string), typeof(SearchTextHighlight), new PropertyMetadata(string.Empty, Refresh));
    public static string GetSuffix(DependencyObject target) => (string)target.GetValue(SuffixProperty);
    public static void SetSuffix(DependencyObject target, string value) => target.SetValue(SuffixProperty, value);

    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static string GetQuery(DependencyObject target) => (string)target.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject target, string value) => target.SetValue(QueryProperty, value);

    private static void Refresh(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock textBlock) return;
        textBlock.Inlines.Clear();
        // 파일 탐색과 같은 퍼지 일치 구간을 사용합니다. 색상은 부모 TextBlock에서
        // 상속하여 팔레트·선택 색상을 유지하고 말줄임도 기존 TextBlock에 맡깁니다.
        foreach (var segment in FileSearchTextSegment.Create(GetText(target) ?? string.Empty, GetQuery(target) ?? string.Empty))
        {
            textBlock.Inlines.Add(new Run(segment.Text)
            {
                FontWeight = segment.IsMatch ? FontWeights.Bold : FontWeights.Normal,
                TextDecorations = segment.IsMatch ? TextDecorations.Underline : null,
            });
        }
        textBlock.Inlines.Add(new Run(GetSuffix(target) ?? string.Empty) { FontWeight = FontWeights.Normal });
    }
}
