using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Services;

/// <summary>최신 분석 상태만 UI에서 샘플링하여 파일마다 UI 작업을 쌓지 않습니다.</summary>
internal sealed class AnalysisStatusBar : IDisposable
{
    private readonly SolutionFileIndexService index;
    private readonly IVsStatusbar status;
    private readonly DispatcherTimer timer;
    private readonly Func<string?>? secondary;
    private string? written;
    private string? observed;
    private DateTime externalUntil;

    /// <param name="secondary">파일 인덱스가 조용할 때 보여 줄 다른 진행 상태(정의·참조 색인 등).</param>
    internal AnalysisStatusBar(SolutionFileIndexService index, IVsStatusbar status, Func<string?>? secondary = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.index = index; this.status = status; this.secondary = secondary;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += OnTick;
        timer.Start();
    }

    private void OnTick(object sender, EventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var snapshot = index.GetSnapshot();
            var text = Format(snapshot) ?? secondary?.Invoke();
            status.IsFrozen(out var frozen);
            if (frozen != 0) return;
            status.GetText(out var current);
            if (text is null)
            {
                // 다른 확장·빌드·탐색 명령이 쓴 상태를 분석 완료 시 지우지 않습니다.
                if (written is not null && current == written) status.Clear();
                written = null; observed = current;
                return;
            }
            if (written is not null && current != written && current != observed)
                externalUntil = DateTime.UtcNow.AddSeconds(3);
            observed = current;
            if (DateTime.UtcNow < externalUntil) return;
            if (current == text) return;
            status.SetText(text); written = text; observed = text;
        }
        catch (COMException) { /* 호스트 종료·일시적 상태 표시줄 거절은 다음 틱에서 재확인합니다. */ }
    }

    internal static string? Format(SolutionFileIndexSnapshot snapshot)
    {
        if (snapshot.State == SolutionFileIndexState.Building) return $"VisualBoost: 소스 파일 수집 중 · {snapshot.FileCount:N0}개";
        if (!snapshot.IsAnalyzing || snapshot.State == SolutionFileIndexState.Faulted) return null;
        var value = snapshot.AnalysisProgress;
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

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        timer.Stop(); timer.Tick -= OnTick;
        try
        {
            status.GetText(out var current);
            if (written is not null && current == written) status.Clear();
        }
        catch (COMException) { /* 종료 중 호스트가 이미 해제되었으면 정리를 종료합니다. */ }
    }
}
