using System.IO;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Services;

/// <summary>파일 인덱스·소스 분석 상태를 상태 표시줄 문구로 바꿉니다. 보일 것이 없으면 null입니다.</summary>
internal static class AnalysisStatusText
{
    /// <remarks>
    /// 다시 연 Solution(저장된 파일 목록·분석이 있음)에서는 저장된 결과로 검색이 이미 동작하므로 수집·캐시 확인·인덱스 준비·include 정리를
    /// 띄우지 않고, 바뀐 파일을 실제로 파싱하는 동안만 그 수를 보입니다(2026-10-07 사용자 피드백: VS를 열 때마다 색인·파싱이 반복되어 보임).
    /// </remarks>
    public static string? Format(SolutionFileIndexSnapshot snapshot)
    {
        var value = snapshot.AnalysisProgress;
        var refreshing = snapshot.IsRefreshing || value?.Refreshing == true;
        if (snapshot.State == SolutionFileIndexState.Building)
            return refreshing ? null : $"VisualBoost: 소스 파일 수집 중 · {snapshot.FileCount:N0}개";
        if (!snapshot.IsAnalyzing || snapshot.State == SolutionFileIndexState.Faulted) return null;
        if (refreshing && value?.Stage != SourceAnalysisStage.Parsing) return null;
        var stage = value?.Stage switch
        {
            SourceAnalysisStage.CacheLoading => "심볼 캐시 읽는 중",
            SourceAnalysisStage.Waiting => "파싱 준비 중",
            SourceAnalysisStage.CacheChecking => "파일 캐시 확인 중",
            SourceAnalysisStage.Parsing => "파싱 중",
            SourceAnalysisStage.Indexing => "심볼 검색 준비 중",
            SourceAnalysisStage.Linking => "include 정리 중",
            SourceAnalysisStage.Saving => "분석 캐시 저장 중",
            _ => "분석 준비 중",
        };
        var count = value is not null && value.Total > 0 ? $" · {value.Completed:N0}/{value.Total:N0}" : "";
        var file = string.IsNullOrEmpty(value?.Path) ? "" : " · " + Path.GetFileName(value!.Path);
        return "VisualBoost: " + stage + count + file;
    }
}
