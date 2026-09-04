namespace VisualBoost.Core.FilePairing;

public sealed class FilePairMatch
{
    public FilePairMatch(string path, int score, string reason)
    {
        Path = path;
        Score = score;
        Reason = reason;
    }

    public string Path { get; }

    public int Score { get; }

    public string Reason { get; }
}

