using System;
using System.Collections.Generic;

namespace VisualBoost.UI;

internal sealed class FileSearchTextSegment
{
    public FileSearchTextSegment(string text, bool isMatch)
    {
        Text = text;
        IsMatch = isMatch;
    }

    public string Text { get; }

    public bool IsMatch { get; }

    public static IReadOnlyList<FileSearchTextSegment> Create(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query))
        {
            return new[] { new FileSearchTextSegment(text, isMatch: false) };
        }

        var matched = new bool[text.Length];
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var positions = FindPositions(text, token);
            if (positions is null)
            {
                continue;
            }

            foreach (var position in positions)
            {
                matched[position] = true;
            }
        }

        var segments = new List<FileSearchTextSegment>();
        var segmentStart = 0;
        for (var index = 1; index <= text.Length; index++)
        {
            if (index < text.Length && matched[index] == matched[segmentStart])
            {
                continue;
            }

            segments.Add(new FileSearchTextSegment(
                text.Substring(segmentStart, index - segmentStart),
                matched[segmentStart]));
            segmentStart = index;
        }

        return segments;
    }

    private static IReadOnlyList<int>? FindPositions(string text, string token)
    {
        var positions = new List<int>(token.Length);
        var searchOffset = 0;
        foreach (var expected in token)
        {
            var found = -1;
            for (var index = searchOffset; index < text.Length; index++)
            {
                if (AreEquivalent(text[index], expected))
                {
                    found = index;
                    break;
                }
            }

            if (found < 0)
            {
                return null;
            }

            positions.Add(found);
            searchOffset = found + 1;
        }

        return positions;
    }

    private static bool AreEquivalent(char first, char second) =>
        (IsDirectorySeparator(first) && IsDirectorySeparator(second)) ||
        char.ToUpperInvariant(first) == char.ToUpperInvariant(second);

    private static bool IsDirectorySeparator(char value) => value == '\\' || value == '/';
}
