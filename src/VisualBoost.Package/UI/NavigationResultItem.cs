using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using VisualBoost.Core.SemanticNavigation;

namespace VisualBoost.UI;

/// <summary>결과 목록 한 줄입니다. 코드 미리보기는 clangd가 알려 준 범위를 기준으로 앞·일치·뒤로 나눕니다.</summary>
internal sealed class NavigationResultItem
{
    private const int MaxBefore = 80;
    private const int MaxAfter = 200;

    public NavigationResultItem(NavigationLocation location, string lineText, string? solutionDirectory)
    {
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
        Code = (Before + Match + After).Trim();
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

    public bool Matches(string query) =>
        FileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
        Folder.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
        Code.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

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

/// <summary>코드 미리보기를 앞·일치·뒤 구간으로 그립니다. 일치 구간만 굵게 하고 색상은 행에서 상속합니다.</summary>
public static class CodeSegments
{
    public static readonly DependencyProperty BeforeProperty = DependencyProperty.RegisterAttached(
        "Before", typeof(string), typeof(CodeSegments), new PropertyMetadata(string.Empty, Refresh));
    public static readonly DependencyProperty MatchProperty = DependencyProperty.RegisterAttached(
        "Match", typeof(string), typeof(CodeSegments), new PropertyMetadata(string.Empty, Refresh));
    public static readonly DependencyProperty AfterProperty = DependencyProperty.RegisterAttached(
        "After", typeof(string), typeof(CodeSegments), new PropertyMetadata(string.Empty, Refresh));

    public static string GetBefore(DependencyObject target) => (string)target.GetValue(BeforeProperty);
    public static void SetBefore(DependencyObject target, string value) => target.SetValue(BeforeProperty, value);
    public static string GetMatch(DependencyObject target) => (string)target.GetValue(MatchProperty);
    public static void SetMatch(DependencyObject target, string value) => target.SetValue(MatchProperty, value);
    public static string GetAfter(DependencyObject target) => (string)target.GetValue(AfterProperty);
    public static void SetAfter(DependencyObject target, string value) => target.SetValue(AfterProperty, value);

    private static void Refresh(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock textBlock) return;
        textBlock.Inlines.Clear();
        textBlock.Inlines.Add(new Run(GetBefore(target) ?? string.Empty));
        textBlock.Inlines.Add(new Run(GetMatch(target) ?? string.Empty) { FontWeight = FontWeights.Bold });
        textBlock.Inlines.Add(new Run(GetAfter(target) ?? string.Empty));
    }
}
