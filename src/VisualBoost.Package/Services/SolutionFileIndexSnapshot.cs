using System;

namespace VisualBoost.Services;

internal enum SolutionFileIndexState
{
    Empty,
    Building,
    Ready,
    Faulted,
}

internal sealed class SolutionFileIndexSnapshot
{
    public SolutionFileIndexSnapshot(
        SolutionFileIndexState state,
        int fileCount,
        int rootCount,
        TimeSpan lastBuildDuration,
        string? lastError)
    {
        State = state;
        FileCount = fileCount;
        RootCount = rootCount;
        LastBuildDuration = lastBuildDuration;
        LastError = lastError;
    }

    public SolutionFileIndexState State { get; }

    public int FileCount { get; }

    public int RootCount { get; }

    public TimeSpan LastBuildDuration { get; }

    public string? LastError { get; }
}
