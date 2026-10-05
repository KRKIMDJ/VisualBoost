using System;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.Searching;

public sealed class SourceSymbolMatch
{
    public SourceSymbolMatch(SourceSymbolLocation location, int score, bool containsAllTokens = false)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        Score = score;
        ContainsAllTokens = containsAllTokens;
    }

    public SourceSymbolLocation Location { get; }

    public int Score { get; }
    public bool ContainsAllTokens { get; }
}
