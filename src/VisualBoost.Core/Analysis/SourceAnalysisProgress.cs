using System;

namespace VisualBoost.Core.Analysis;

public enum SourceAnalysisStage { CacheLoading, Waiting, CacheChecking, Parsing, Indexing, Saving }

/// <summary>백그라운드 분석의 최신 상태입니다. 완료 수는 읽기 실패·상한 제외 파일도 포함합니다.</summary>
public sealed class SourceAnalysisProgress
{
    public SourceAnalysisProgress(SourceAnalysisStage stage, int completed, int total, string? path = null, bool refreshing = false)
    { Stage = stage; Completed = Math.Max(0, completed); Total = Math.Max(0, total); Path = path; Refreshing = refreshing; }
    public SourceAnalysisStage Stage { get; }

    /// <summary>
    /// 저장된 분석이 있어 그 결과를 이미 검색에 쓰는 다시 열기 분석입니다. 이때 파싱 단계의 수는 바뀐(저장된 분석을 쓸 수 없는) 파일만 셉니다.
    /// </summary>
    public bool Refreshing { get; }
    public int Completed { get; }
    public int Total { get; }
    public string? Path { get; }
}
