using System;
using VisualBoost.Core.Analysis;

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
        bool isAnalyzing,
        int symbolCount,
        int includeEdgeCount,
        TimeSpan lastBuildDuration,
        string? lastError,
        string? analysisError,
        SourceAnalysisProgress? analysisProgress = null)
    {
        State = state;
        FileCount = fileCount;
        RootCount = rootCount;
        IsAnalyzing = isAnalyzing;
        SymbolCount = symbolCount;
        IncludeEdgeCount = includeEdgeCount;
        LastBuildDuration = lastBuildDuration;
        LastError = lastError;
        AnalysisError = analysisError;
        AnalysisProgress = analysisProgress;
    }

    public SolutionFileIndexState State { get; }

    public int FileCount { get; }

    public int RootCount { get; }

    public bool IsAnalyzing { get; }

    public int SymbolCount { get; }

    public int IncludeEdgeCount { get; }

    public TimeSpan LastBuildDuration { get; }

    public string? LastError { get; }

    public string? AnalysisError { get; }
    public SourceAnalysisProgress? AnalysisProgress { get; }
}
