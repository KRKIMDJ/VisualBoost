using System;

namespace VisualBoost.Core.Analysis;

public enum SourceAnalysisStage { CacheLoading, Waiting, CacheChecking, Parsing, Indexing, Linking, Saving }

/// <summary>백그라운드 분석의 최신 상태입니다. 완료 수는 읽기 실패·상한 제외 파일도 포함합니다.</summary>
public sealed class SourceAnalysisProgress
{
    public SourceAnalysisProgress(SourceAnalysisStage stage, int completed, int total, string? path = null)
    { Stage = stage; Completed = Math.Max(0, completed); Total = Math.Max(0, total); Path = path; }
    public SourceAnalysisStage Stage { get; }
    public int Completed { get; }
    public int Total { get; }
    public string? Path { get; }
}
