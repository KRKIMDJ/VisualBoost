using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.Searching;

internal sealed class SymbolSearchSnapshot
{
    private readonly Entry[] entries;

    public SymbolSearchSnapshot(IEnumerable<SourceSymbolLocation> symbols)
    {
        // 대소문자 경계의 점수는 보존하면서 동일 이름의 선언·정의를 한 번만 평가합니다.
        entries = symbols.GroupBy(symbol => symbol.Name, StringComparer.Ordinal)
            .Select(group => new Entry(group.Key, group.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Line).ThenBy(s => s.Column).ToArray())).ToArray();
    }

    public IReadOnlyList<SourceSymbolMatch> Search(string query, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return Array.Empty<SourceSymbolMatch>();
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var normalizedTokens = tokens.Select(text => text.ToUpperInvariant()).ToArray();
        var required = Mask(string.Concat(tokens));
        var matches = new List<SourceSymbolMatch>(limit);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            // 충돌을 허용하는 비트 필터입니다. 불가능한 후보만 제외하고 나머지는 정확히 채점합니다.
            if ((entry.Characters & required) != required) continue;
            var score = Score(entry, normalizedTokens);
            if (score < 0) continue;
            foreach (var location in entry.Locations)
            {
                token.ThrowIfCancellationRequested();
                var candidate = new SourceSymbolMatch(location, score);
                if (matches.Count == limit && FuzzySymbolSearch.Compare(candidate, matches[matches.Count - 1]) >= 0)
                    break;
                FuzzySymbolSearch.Insert(matches, candidate, limit);
            }
        }
        return matches;
    }

    private static ulong Mask(string text)
    {
        ulong mask = 0;
        foreach (var character in text) mask |= 1UL << (char.ToUpperInvariant(character) & 63);
        return mask;
    }

    private static int Score(Entry entry, string[] tokens)
    {
        var name = entry.Locations[0].Name;
        var normalized = entry.NormalizedName;
        var score = 0;
        foreach (var token in tokens)
        {
            var offset = 0;
            var previous = -1;
            var tokenScore = 0;
            foreach (var character in token)
            {
                var match = normalized.IndexOf(character, offset);
                if (match < 0) return -1;
                tokenScore += 10;
                if (match == 0 || IsBoundary(name[match - 1], name[match])) tokenScore += 20;
                if (previous >= 0)
                {
                    var gap = match - previous - 1;
                    tokenScore += gap == 0 ? 15 : -Math.Min(gap, 10);
                }
                previous = match;
                offset = match + 1;
            }
            if (tokenScore < 0) return -1;
            score += tokenScore + 160;
            if (normalized == token) score += 500;
            else if (normalized.StartsWith(token, StringComparison.Ordinal)) score += 240;
        }
        return score - Math.Min(name.Length, 80);
    }

    private static bool IsBoundary(char previous, char current) =>
        previous == '/' || previous == '\\' || previous == '_' || previous == '-' ||
        previous == '.' || previous == ' ' || (char.IsLower(previous) && char.IsUpper(current));

    private sealed class Entry
    {
        public Entry(string name, SourceSymbolLocation[] locations)
        {
            Characters = Mask(name);
            NormalizedName = name.ToUpperInvariant();
            Locations = locations;
        }
        public ulong Characters { get; }
        public string NormalizedName { get; }
        public SourceSymbolLocation[] Locations { get; }
    }
}
