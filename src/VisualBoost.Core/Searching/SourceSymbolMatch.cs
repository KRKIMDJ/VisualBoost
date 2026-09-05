using System;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.Searching;

public sealed class SourceSymbolMatch
{
    public SourceSymbolMatch(SourceSymbolLocation location, int score)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        Score = score;
    }

    public SourceSymbolLocation Location { get; }

    public int Score { get; }
}
