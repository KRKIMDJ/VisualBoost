using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VisualBoost.UI;

internal static class SearchHighlightTests
{
    public static void Run()
    {
        var block = new TextBlock { Foreground = Brushes.Teal, TextTrimming = TextTrimming.CharacterEllipsis };
        foreach (var (text, query, expected) in new[]
        {
            ("AlphaBetaGamma", "abg", "ABG"),
            ("AlphaBetaGamma", "Beta", "Beta"),
            ("AlphaBeta", "Beta Alpha", "AlphaBeta"),
            ("FooBarFoo", "Foo", "Foo"),
            ("AlphaBeta", "ZZ", ""),
            ("AlphaBeta", "", ""),
            ("", "Alpha", ""),
        })
        {
            // 같은 TextBlock을 재사용하여 검색어 교체와 가상화 행 재활용을 확인합니다.
            SearchTextHighlight.SetText(block, text);
            SearchTextHighlight.SetQuery(block, query);
            var runs = block.Inlines.OfType<Run>().ToArray();
            Check(string.Concat(runs.Select(run => run.Text)) == text, "원본 텍스트 보존");
            Check(string.Concat(runs.Where(run => run.FontWeight == FontWeights.Bold).Select(run => run.Text)) == expected, "퍼지/복수 토큰 일치 굵기");
            Check(string.Concat(runs.Where(run => run.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true).Select(run => run.Text)) == expected, "일치 밑줄");
            Check(runs.All(run => run.Foreground == block.Foreground), "색상 상속");
        }
        SearchTextHighlight.SetText(block, "Move");
        SearchTextHighlight.SetQuery(block, "Move");
        SearchTextHighlight.SetSuffix(block, "(MoveType value) · Game::MoveOwner");
        var detail = block.Inlines.OfType<Run>().Last();
        Check(detail.FontWeight == FontWeights.Normal && detail.TextDecorations?.Count is null or 0, "인수·소속의 같은 이름에는 굵기·밑줄을 붙이지 않음");
        Check(string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text)) == "Move(MoveType value) · Game::MoveOwner", "한 행의 이름·인수·소속 보존");
        SearchTextHighlight.SetSuffix(block, "");
        Check(string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text)) == "Move", "행 재활용 시 이전 상세 정보 제거");
        Console.WriteLine("PASS: 일치 문자 굵기·밑줄, 퍼지/복수 토큰, 검색어 교체·빈 검색·색상 상속 및 심볼 상세 정보");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
