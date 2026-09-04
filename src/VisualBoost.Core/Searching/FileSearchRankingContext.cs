using System;
using System.Collections.Generic;

namespace VisualBoost.Core.Searching;

public sealed class FileSearchRankingContext
{
    private readonly Dictionary<string, int> recentRanks = new(StringComparer.OrdinalIgnoreCase);

    public FileSearchRankingContext(
        string? preferredRoot = null,
        IReadOnlyList<string>? recentPaths = null)
    {
        PreferredRoot = preferredRoot;
        RecentPaths = recentPaths ?? Array.Empty<string>();
        for (var index = 0; index < RecentPaths.Count && index < 20; index++)
        {
            if (!string.IsNullOrWhiteSpace(RecentPaths[index]) && !recentRanks.ContainsKey(RecentPaths[index]))
            {
                recentRanks.Add(RecentPaths[index], index);
            }
        }
    }

    public string? PreferredRoot { get; }

    public IReadOnlyList<string> RecentPaths { get; }

    internal bool TryGetRecentRank(string path, out int rank) => recentRanks.TryGetValue(path, out rank);
}
