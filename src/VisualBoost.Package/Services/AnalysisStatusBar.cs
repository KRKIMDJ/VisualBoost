using System;
using System.Collections.Generic;
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
    // 최근에 쓴 문구들입니다. VS가 연속 갱신 중 일부를 늦게 반영하거나 건너뛰면 표시된 글자가 마지막으로 쓴 글자와 달라지는데,
    // 이를 다른 기능이 쓴 글자로 오인하면 분석이 끝난 뒤에도 진행 문구가 남습니다(2026-10-07 측정: include 정리 문구가 4분 넘게 남음).
    private readonly Queue<string> recent = new();
    private const int RecentLimit = 64;
    private string? written;
    private string? observed;
    private DateTime externalUntil;
    private DateTime? refreshSince;

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
            var own = AnalysisStatusText.Format(snapshot, out var refreshing);
            var text = AnalysisStatusText.Delay(own, refreshing, DateTime.UtcNow, ref refreshSince) ?? secondary?.Invoke();
            status.IsFrozen(out var frozen);
            if (frozen != 0) return;
            status.GetText(out var current);
            if (text is null)
            {
                // 다른 확장·빌드·탐색 명령이 쓴 상태를 분석 완료 시 지우지 않습니다. VS가 지우기를 늦게 반영하거나 건너뛸 수 있으므로
                // 표시가 자기 문구가 아니게 될 때까지 최근 문구를 두고 다음 틱에 다시 지웁니다(2026-10-07 검토).
                if (written is not null && IsOwn(current))
                {
                    status.Clear();
                    return;
                }

                written = null; observed = current;
                recent.Clear();
                return;
            }
            if (written is not null && current != observed && !IsOwn(current))
                externalUntil = DateTime.UtcNow.AddSeconds(3);
            observed = current;
            if (DateTime.UtcNow < externalUntil) return;
            if (current == text) return;
            status.SetText(text); written = text; observed = text;
            recent.Enqueue(text);
            while (recent.Count > RecentLimit) recent.Dequeue();
        }
        catch (COMException) { /* 호스트 종료·일시적 상태 표시줄 거절은 다음 틱에서 재확인합니다. */ }
    }

    private bool IsOwn(string? current) => current is not null && (current == written || recent.Contains(current));

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
