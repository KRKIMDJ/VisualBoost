using System;

namespace VisualBoost.Core.Searching;

public enum FileNameMatchQuality
{
    PathOnly,
    Fuzzy,
    ContainsAllTokens,
}

public sealed class FileSearchMatch
{
    public FileSearchMatch(string path, int score, FileNameMatchQuality nameMatchQuality = FileNameMatchQuality.PathOnly)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Score = score;
        NameMatchQuality = nameMatchQuality;
    }

    public string Path { get; }

    public int Score { get; }

    public FileNameMatchQuality NameMatchQuality { get; }
}
