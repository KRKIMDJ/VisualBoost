using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace VisualBoost.Core.Searching;

public static class FuzzyFileSearch
{
    public static IReadOnlyList<FileSearchMatch> Search(
        string query,
        IEnumerable<string> paths,
        int maximumResults = 50,
        CancellationToken cancellationToken = default) =>
        Search(query, paths, null, maximumResults, cancellationToken);

    public static IReadOnlyList<FileSearchMatch> Search(
        string query,
        IEnumerable<string> paths,
        FileSearchRankingContext? rankingContext,
        int maximumResults = 50,
        CancellationToken cancellationToken = default)
    {
        if (paths is null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        if (string.IsNullOrWhiteSpace(query) || maximumResults <= 0)
        {
            return Array.Empty<FileSearchMatch>();
        }

        var tokens = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeSeparators)
            .Where(token => token.Length > 0)
            .ToArray();
        if (tokens.Length == 0)
        {
            return Array.Empty<FileSearchMatch>();
        }

        var bestMatches = new List<FileSearchMatch>(maximumResults);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var score = ScorePath(path, tokens);
            if (score < 0)
            {
                continue;
            }

            score += ScoreRankingContext(path, rankingContext);

            InsertMatch(bestMatches, new FileSearchMatch(path, score), maximumResults);
        }

        return bestMatches;
    }

    private static int ScoreRankingContext(string path, FileSearchRankingContext? context)
    {
        if (context is null)
        {
            return 0;
        }

        var score = 0;
        if (IsInside(path, context.PreferredRoot))
        {
            score += 120;
        }

        if (context.TryGetRecentRank(path, out var recentRank))
        {
            score += 220 - (recentRank * 10);
        }

        return score;
    }

    private static bool IsInside(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedPath = NormalizeSeparators(path);
        var normalizedRoot = NormalizeSeparators(root!).TrimEnd('/');
        return normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScorePath(string path, IReadOnlyList<string> tokens)
    {
        var candidate = NormalizeSeparators(path);
        var fileNameOffset = candidate.LastIndexOf('/') + 1;
        var fileName = candidate.Substring(fileNameOffset);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var score = 0;

        foreach (var token in tokens)
        {
            var tokenScore = ScoreToken(candidate, token, fileNameOffset);
            if (tokenScore < 0)
            {
                return -1;
            }

            score += tokenScore;
            if (string.Equals(fileName, token, StringComparison.OrdinalIgnoreCase))
            {
                score += 400;
            }
            else if (string.Equals(stem, token, StringComparison.OrdinalIgnoreCase))
            {
                score += 350;
            }
            else if (fileName.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 180;
            }
        }

        // 점수가 같으면 짧고 직접적인 경로가 앞서도록 작은 길이 페널티를 적용합니다.
        return score - Math.Min(candidate.Length, 200);
    }

    private static int ScoreToken(string candidate, string token, int fileNameOffset)
    {
        var score = 0;
        var previousMatch = -1;
        var searchOffset = 0;

        foreach (var queryCharacter in token)
        {
            var match = FindCharacter(candidate, queryCharacter, searchOffset);
            if (match < 0)
            {
                return -1;
            }

            score += 12;
            if (match >= fileNameOffset)
            {
                score += 10;
            }

            if (IsBoundary(candidate, match))
            {
                score += 18;
            }

            if (previousMatch >= 0)
            {
                var gap = match - previousMatch - 1;
                if (gap == 0)
                {
                    score += 16;
                }
                else
                {
                    score -= Math.Min(gap, 12);
                }
            }

            if (candidate[match] == queryCharacter)
            {
                score += 1;
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

    private static bool IsBoundary(string candidate, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = candidate[index - 1];
        var current = candidate[index];
        return previous == '/' ||
               previous == '\\' ||
               previous == '_' ||
               previous == '-' ||
               previous == '.' ||
               (char.IsLower(previous) && char.IsUpper(current));
    }

    private static void InsertMatch(
        List<FileSearchMatch> matches,
        FileSearchMatch candidate,
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

    private static int Compare(FileSearchMatch left, FileSearchMatch right)
    {
        var scoreComparison = right.Score.CompareTo(left.Score);
        if (scoreComparison != 0)
        {
            return scoreComparison;
        }

        var lengthComparison = left.Path.Length.CompareTo(right.Path.Length);
        return lengthComparison != 0
            ? lengthComparison
            : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
    }

    private static string NormalizeSeparators(string value) => value.Replace('\\', '/');
}
