using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Coloring;
using VisualBoost.Core.SemanticNavigation;

namespace VisualBoost.UI;

/// <summary>결과 목록 한 줄입니다. 코드 미리보기는 clangd가 알려 준 범위를 기준으로 앞·일치·뒤로 나눕니다.</summary>
internal sealed class NavigationResultItem
{
    private const int MaxBefore = 80;
    private const int MaxAfter = 200;

    // 색 구간은 자르기 전 원래 줄로 분류합니다. 잘린 앞부분이 문자열·주석 안에서 시작해도 경계를 바로 알기 위해서입니다.
    private readonly string lineText;
    private readonly int visibleStart;
    private readonly int visibleEnd;
    private readonly int previewShift;
    private IReadOnlyList<CodePreviewSpan>? previewSpans;

    public NavigationResultItem(NavigationLocation location, string lineText, string? solutionDirectory, SourceSymbolKind? symbolKind = null)
    {
        SymbolKind = symbolKind;
        Location = location;
        FullPath = location.Path;
        FileName = Path.GetFileName(location.Path);
        Line = (location.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Folder = RelativeFolder(location.Path, solutionDirectory);
        var start = Math.Max(0, Math.Min(lineText.Length, location.Character));
        var end = location.EndLine == location.Line ? Math.Max(start, Math.Min(lineText.Length, location.EndCharacter)) : start;
        var before = lineText.Substring(0, start).TrimStart().Replace('\t', ' ');
        var after = lineText.Substring(end).TrimEnd().Replace('\t', ' ');
        Before = before.Length > MaxBefore ? "…" + before.Substring(before.Length - MaxBefore) : before;
        Match = lineText.Substring(start, end - start);
        After = after.Length > MaxAfter ? after.Substring(0, MaxAfter) + "…" : after;
        // 원래 줄의 [visibleStart, visibleEnd)가 미리보기에 보이고, 앞을 잘랐으면 미리보기는 줄임표 한 글자만큼 밀립니다.
        this.lineText = lineText;
        visibleStart = start - Math.Min(before.Length, MaxBefore);
        visibleEnd = end + Math.Min(after.Length, MaxAfter);
        previewShift = Before.Length - Math.Min(before.Length, MaxBefore);
        Code = (Before + Match + After).Trim();
        Container = location.Container ?? string.Empty;
    }

    public NavigationLocation Location { get; }

    public string FullPath { get; }

    public string FileName { get; }

    public string Line { get; }

    public string Folder { get; }

    public string Before { get; }

    public string Match { get; }

    public string After { get; }

    public string Code { get; }

    /// <summary>앞뒤 공백만 뺀 원래 코드 줄입니다. 미리보기(<see cref="Code"/>)와 달리 긴 줄을 줄임표로 자르지 않아 복사에 씁니다.</summary>
    public string SourceLine => lineText.Trim();

    /// <summary>참조가 들어 있는 함수·클래스 이름입니다. 모르면 빈 문자열입니다.</summary>
    public string Container { get; }

    /// <summary>일치 구간(<see cref="Match"/>)이 가리키는 심볼의 종류입니다. 이름 색칠에 쓰며 clangd가 판정하지 못하면 null입니다.</summary>
    public SourceSymbolKind? SymbolKind { get; }

    /// <summary>
    /// 미리보기(<see cref="Before"/>+<see cref="Match"/>+<see cref="After"/>) 좌표의 색 구간입니다. 화면에 처음 그릴 때 한 번 만들고,
    /// 행 재활용·선택 변경으로 다시 그릴 때는 그대로 씁니다. 결과는 수천 개일 수 있어 보이는 행만 분류하도록 미룹니다. UI thread에서만 읽습니다.
    /// </summary>
    public IReadOnlyList<CodePreviewSpan> PreviewSpans => previewSpans ??= CreatePreviewSpans();

    public bool Matches(string query) =>
        FileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
        Folder.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
        Code.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
        Container.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private IReadOnlyList<CodePreviewSpan> CreatePreviewSpans()
    {
        var result = new List<CodePreviewSpan>();
        foreach (var span in CppLinePreviewClassifier.Classify(lineText))
        {
            var from = Math.Max(span.Start, visibleStart);
            var to = Math.Min(span.Start + span.Length, visibleEnd);
            if (to > from) result.Add(new CodePreviewSpan(previewShift + from - visibleStart, to - from, span.Kind));
        }

        return result.AsReadOnly();
    }

    private static string RelativeFolder(string path, string? solutionDirectory)
    {
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        if (!string.IsNullOrEmpty(solutionDirectory))
        {
            var root = solutionDirectory!.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return folder.Substring(root.Length);
            if (string.Equals(folder, solutionDirectory!.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return ".";
        }

        return folder;
    }
}

/// <summary>
/// 코드 미리보기를 그립니다. 앞·일치·뒤 구간 중 일치 구간만 굵게 하고, 미리 분류한 색 구간(<see cref="SpansProperty"/>)으로
/// 텍스트 편집기와 같은 계열의 색을 입힙니다. 여기서는 분류하지 않고 Run만 다시 만듭니다. 글꼴은 텍스트 편집기 글꼴 패밀리이며 크기는 목록 글꼴을 따릅니다.
/// 선택 행(<see cref="PlainProperty"/>)과 고대비 모드에서는 색을 빼고 행 전경색을 상속합니다.
/// </summary>
public static class CodeSegments
{
    public static readonly DependencyProperty BeforeProperty = DependencyProperty.RegisterAttached(
        "Before", typeof(string), typeof(CodeSegments), new PropertyMetadata(string.Empty, Refresh));
    public static readonly DependencyProperty MatchProperty = DependencyProperty.RegisterAttached(
        "Match", typeof(string), typeof(CodeSegments), new PropertyMetadata(string.Empty, Refresh));
    public static readonly DependencyProperty AfterProperty = DependencyProperty.RegisterAttached(
        "After", typeof(string), typeof(CodeSegments), new PropertyMetadata(string.Empty, Refresh));

    /// <summary>미리보기 좌표의 색 구간입니다(<see cref="NavigationResultItem.PreviewSpans"/>). 없으면 색 없이 그립니다.</summary>
    public static readonly DependencyProperty SpansProperty = DependencyProperty.RegisterAttached(
        "Spans", typeof(IReadOnlyList<CodePreviewSpan>), typeof(CodeSegments), new PropertyMetadata(null, Refresh));

    /// <summary>일치 구간(탐색 대상 심볼)의 종류입니다. null이면 일치 구간은 행 전경색입니다.</summary>
    public static readonly DependencyProperty MatchKindProperty = DependencyProperty.RegisterAttached(
        "MatchKind", typeof(SourceSymbolKind?), typeof(CodeSegments), new PropertyMetadata(null, Refresh));

    /// <summary>true이면 색을 입히지 않습니다. 선택 행의 강조 글자색이 읽히도록 행의 IsSelected를 연결합니다.</summary>
    public static readonly DependencyProperty PlainProperty = DependencyProperty.RegisterAttached(
        "Plain", typeof(bool), typeof(CodeSegments), new PropertyMetadata(false, Refresh));

    /// <summary>목록 배경입니다. 의미 기반 색상의 어두운/밝은 테마 값을 고르는 데 씁니다.</summary>
    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.RegisterAttached(
        "Surface", typeof(Brush), typeof(CodeSegments), new PropertyMetadata(null, Refresh));

    /// <summary>색 설정·편집기 서식이 바뀌면 증가하는 값(<see cref="SearchPalette.RevisionProperty"/>)을 연결해 다시 그립니다.</summary>
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.RegisterAttached(
        "Revision", typeof(int), typeof(CodeSegments), new PropertyMetadata(0, Refresh));

    public static string GetBefore(DependencyObject target) => (string)target.GetValue(BeforeProperty);
    public static void SetBefore(DependencyObject target, string value) => target.SetValue(BeforeProperty, value);
    public static string GetMatch(DependencyObject target) => (string)target.GetValue(MatchProperty);
    public static void SetMatch(DependencyObject target, string value) => target.SetValue(MatchProperty, value);
    public static string GetAfter(DependencyObject target) => (string)target.GetValue(AfterProperty);
    public static void SetAfter(DependencyObject target, string value) => target.SetValue(AfterProperty, value);
    public static IReadOnlyList<CodePreviewSpan>? GetSpans(DependencyObject target) => (IReadOnlyList<CodePreviewSpan>?)target.GetValue(SpansProperty);
    public static void SetSpans(DependencyObject target, IReadOnlyList<CodePreviewSpan>? value) => target.SetValue(SpansProperty, value);
    public static SourceSymbolKind? GetMatchKind(DependencyObject target) => (SourceSymbolKind?)target.GetValue(MatchKindProperty);
    public static void SetMatchKind(DependencyObject target, SourceSymbolKind? value) => target.SetValue(MatchKindProperty, value);
    public static bool GetPlain(DependencyObject target) => (bool)target.GetValue(PlainProperty);
    public static void SetPlain(DependencyObject target, bool value) => target.SetValue(PlainProperty, value);
    public static Brush? GetSurface(DependencyObject target) => (Brush?)target.GetValue(SurfaceProperty);
    public static void SetSurface(DependencyObject target, Brush? value) => target.SetValue(SurfaceProperty, value);
    public static int GetRevision(DependencyObject target) => (int)target.GetValue(RevisionProperty);
    public static void SetRevision(DependencyObject target, int value) => target.SetValue(RevisionProperty, value);

    private static void Refresh(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock textBlock) return;
        var before = GetBefore(target) ?? string.Empty;
        var match = GetMatch(target) ?? string.Empty;
        var after = GetAfter(target) ?? string.Empty;
        var code = before + match + after;
        var matchEnd = before.Length + match.Length;
        textBlock.FontFamily = CodePreviewStyle.FontFamily;
        textBlock.Inlines.Clear();

        var colored = !GetPlain(target);
        var dark = GetSurface(target) is not SolidColorBrush surface || SemanticColorPalette.IsDark(surface.Color.R, surface.Color.G, surface.Color.B);
        var spans = colored ? GetSpans(target) ?? Array.Empty<CodePreviewSpan>() : Array.Empty<CodePreviewSpan>();
        var matchBrush = colored && GetMatchKind(target) is { } kind ? CodePreviewStyle.BrushFor(kind, dark) : null;

        // 구간 경계와 일치 구간 경계로 나눈 조각마다 굵기·색을 정하고, 같은 모양의 이웃 조각은 하나의 Run으로 합칩니다.
        var position = 0;
        var spanIndex = 0;
        var pending = new StringBuilder();
        var pendingBold = false;
        Brush? pendingBrush = null;
        while (position < code.Length)
        {
            while (spanIndex < spans.Count && spans[spanIndex].Start + spans[spanIndex].Length <= position) spanIndex++;
            var inSpan = spanIndex < spans.Count && spans[spanIndex].Start <= position;
            var bold = position >= before.Length && position < matchEnd;
            var next = code.Length;
            if (position < before.Length) next = before.Length;
            else if (bold) next = matchEnd;
            if (inSpan) next = Math.Min(next, spans[spanIndex].Start + spans[spanIndex].Length);
            else if (spanIndex < spans.Count) next = Math.Min(next, spans[spanIndex].Start);
            var brush = bold ? matchBrush : inSpan ? CodePreviewStyle.BrushFor(spans[spanIndex].Kind, dark) : null;
            if (pending.Length > 0 && (bold != pendingBold || !ReferenceEquals(brush, pendingBrush)))
            {
                textBlock.Inlines.Add(CreateRun(pending.ToString(), pendingBold, pendingBrush));
                pending.Clear();
            }

            pending.Append(code, position, next - position);
            pendingBold = bold;
            pendingBrush = brush;
            position = next;
        }

        if (pending.Length > 0) textBlock.Inlines.Add(CreateRun(pending.ToString(), pendingBold, pendingBrush));
    }

    private static Run CreateRun(string text, bool bold, Brush? brush)
    {
        var run = new Run(text);
        if (bold) run.FontWeight = FontWeights.Bold;
        if (brush is not null) run.Foreground = brush;
        return run;
    }
}
