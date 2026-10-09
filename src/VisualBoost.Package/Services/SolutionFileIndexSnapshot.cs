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
        SourceAnalysisProgress? analysisProgress = null,
        bool isRefreshing = false,
        bool hasCachedSymbols = false)
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
        IsRefreshing = isRefreshing;
        HasCachedSymbols = hasCachedSymbols;
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

    /// <summary>저장된 파일 목록이 있는 Solution을 다시 수집 중입니다(파일 검색은 그동안 수집된 목록으로 동작).</summary>
    public bool IsRefreshing { get; }

    /// <summary>지난 세션에 마친 소스 분석을 불러와 심볼을 공개했습니다(분석 중이라도 Solution 전체의 이름을 담음).</summary>
    public bool HasCachedSymbols { get; }
}
