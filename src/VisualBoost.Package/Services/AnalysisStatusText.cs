using System;
using System.IO;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Services;

/// <summary>파일 인덱스·소스 분석 상태를 상태 표시줄 문구로 바꿉니다. 보일 것이 없으면 null입니다.</summary>
internal static class AnalysisStatusText
{
    /// <summary>다시 연 Solution의 진행 문구는 이 시간 넘게 이어질 때만 보입니다.</summary>
    internal static readonly TimeSpan RefreshDelay = TimeSpan.FromSeconds(1);

    public static string? Format(SolutionFileIndexSnapshot snapshot) => Format(snapshot, out _);

    /// <param name="refreshing">다시 연 Solution의 문구이면 true입니다. 표시 지연(<see cref="Delay"/>)에 씁니다.</param>
    /// <remarks>
    /// 다시 연 Solution(저장된 파일 목록·분석이 있음)에서는 저장된 결과로 검색이 이미 동작하므로 수집·캐시 확인·인덱스 준비·include 정리를
    /// 띄우지 않고, 바뀐 파일을 실제로 분석하는 동안만 그 수를 보입니다(2026-10-07 사용자 피드백: VS를 열 때마다 색인·파싱이 반복되어 보임).
    /// 처음 열 때와 같은 "파싱 중"을 쓰면 다시 파싱한다는 인상이 남으므로 증분 작업임을 드러내는 문구를 씁니다(UI/UX 담당 제안).
    /// </remarks>
    public static string? Format(SolutionFileIndexSnapshot snapshot, out bool refreshing)
    {
        var value = snapshot.AnalysisProgress;
        refreshing = snapshot.IsRefreshing || value?.Refreshing == true;
        if (snapshot.State == SolutionFileIndexState.Building)
            return refreshing ? null : $"VisualBoost: 소스 파일 수집 중 · {snapshot.FileCount:N0}개";
        if (!snapshot.IsAnalyzing || snapshot.State == SolutionFileIndexState.Faulted) return null;
        if (refreshing && value?.Stage != SourceAnalysisStage.Parsing) return null;
        var stage = refreshing ? "바뀐 파일 분석 중" : value?.Stage switch
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

    /// <summary>
    /// 다시 연 Solution의 문구는 처음 나온 뒤 <see cref="RefreshDelay"/>가 지나야 돌려줍니다. 바뀐 파일이 한두 개면 한 틱만 보였다 사라져
    /// VS를 열 때마다 잡음이 되기 때문입니다. 처음 열기 문구나 null이 오면 기다리던 시각을 지웁니다.
    /// </summary>
    /// <param name="since">다시 열기 문구가 처음 나온 시각입니다. 호출하는 쪽이 틱 사이에 보관합니다.</param>
    public static string? Delay(string? text, bool refreshing, DateTime now, ref DateTime? since)
    {
        if (text is null || !refreshing)
        {
            since = null;
            return text;
        }

        since ??= now;
        return now - since.Value >= RefreshDelay ? text : null;
    }
}
