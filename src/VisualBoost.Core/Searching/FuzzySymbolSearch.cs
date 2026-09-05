using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.Searching;

public static class FuzzySymbolSearch
{
    public static IReadOnlyList<SourceSymbolMatch> Search(
        string query,
        IEnumerable<SourceSymbolLocation> symbols,
        int maximumResults = 100,
        CancellationToken cancellationToken = default)
    {
        if (symbols is null)
        {
            throw new ArgumentNullException(nameof(symbols));
        }

        if (string.IsNullOrWhiteSpace(query) || maximumResults <= 0)
        {
            return Array.Empty<SourceSymbolMatch>();
        }

        var tokens = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 0)
            .ToArray();
        var matches = new List<SourceSymbolMatch>(maximumResults);
        foreach (var symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = Score(symbol, tokens);
            if (score < 0)
            {
                continue;
            }

            Insert(matches, new SourceSymbolMatch(symbol, score), maximumResults);
        }

        return matches;
    }

    internal static int Score(SourceSymbolLocation symbol, IReadOnlyList<string> tokens)
    {
        var score = 0;
        foreach (var token in tokens)
        {
            var nameScore = ScoreToken(symbol.Name, token);
            if (nameScore < 0)
            {
                return -1;
            }

            score += nameScore + 160;
            if (string.Equals(symbol.Name, token, StringComparison.OrdinalIgnoreCase))
            {
                score += 500;
            }
            else if (symbol.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 240;
            }
        }

        return score - Math.Min(symbol.Name.Length, 80);
    }

    private static int ScoreToken(string candidate, string token)
    {
        var score = 0;
        var searchOffset = 0;
        var previousMatch = -1;
        foreach (var expected in token)
        {
            var match = FindCharacter(candidate, expected, searchOffset);
            if (match < 0)
            {
                return -1;
            }

            score += 10;
            if (match == 0 || IsBoundary(candidate[match - 1], candidate[match]))
            {
                score += 20;
            }

            if (previousMatch >= 0)
            {
                var gap = match - previousMatch - 1;
                score += gap == 0 ? 15 : -Math.Min(gap, 10);
            }

            previousMatch = match;
            searchOffset = match + 1;
        }

        return score;
    }

    private static int FindCharacter(string candidate, char expected, int startIndex)
    {
        var normalizedExpected = char.ToUpperInvariant(expected);
        for (var index = startIndex; index < candidate.Length; index++)
        {
            if (char.ToUpperInvariant(candidate[index]) == normalizedExpected)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsBoundary(char previous, char current) =>
        previous == '/' ||
        previous == '\\' ||
        previous == '_' ||
        previous == '-' ||
        previous == '.' ||
        previous == ' ' ||
        (char.IsLower(previous) && char.IsUpper(current));

    internal static void Insert(
        List<SourceSymbolMatch> matches,
        SourceSymbolMatch candidate,
        int maximumResults)
    {
        var low = 0;
        var high = matches.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (Compare(candidate, matches[middle]) < 0)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        if (low >= maximumResults)
        {
            return;
        }

        matches.Insert(low, candidate);
        if (matches.Count > maximumResults)
        {
            matches.RemoveAt(matches.Count - 1);
        }
    }

    internal static int Compare(SourceSymbolMatch left, SourceSymbolMatch right)
    {
        var scoreComparison = right.Score.CompareTo(left.Score);
        if (scoreComparison != 0)
        {
            return scoreComparison;
        }

        var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(
            left.Location.Name,
            right.Location.Name);
        if (nameComparison != 0)
        {
            return nameComparison;
        }

        var pathComparison = StringComparer.OrdinalIgnoreCase.Compare(
            left.Location.Path,
            right.Location.Path);
        if (pathComparison != 0)
        {
            return pathComparison;
        }

        var lineComparison = left.Location.Line.CompareTo(right.Location.Line);
        return lineComparison != 0
            ? lineComparison
            : left.Location.Column.CompareTo(right.Location.Column);
    }
}
