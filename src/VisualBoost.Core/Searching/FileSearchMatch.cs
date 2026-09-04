using System;

namespace VisualBoost.Core.Searching;

public sealed class FileSearchMatch
{
    public FileSearchMatch(string path, int score)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Score = score;
    }

    public string Path { get; }

    public int Score { get; }
}
