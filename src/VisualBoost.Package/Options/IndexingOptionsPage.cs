using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Services;

namespace VisualBoost.Options;

[Guid("f6259759-777e-45af-9b99-55a6c7e70434")]
public sealed class IndexingOptionsPage : DialogPage
{
    [Category("파일 인덱스")]
    [DisplayName("영구 캐시 사용")]
    [Description("Solution을 다시 열 때 초기 파일 목록을 빠르게 복원하도록 LocalAppData에 파일 인덱스를 저장합니다. 변경 사항은 다음 Solution 로드부터 적용됩니다.")]
    [DefaultValue(true)]
    public bool UsePersistentFileCache { get; set; } = true;

    [Category("소스 분석")]
    [DisplayName("C++ 소스 분석 사용")]
    [Description("include 관계와 심볼 후보를 백그라운드에서 분석합니다. 변경 사항은 다음 Solution 로드부터 적용됩니다.")]
    [DefaultValue(true)]
    public bool EnableSourceAnalysis { get; set; } = true;

    [Category("소스 분석")]
    [DisplayName("분석 시작 지연 시간(ms)")]
    [Description("Solution 파일 인덱싱 완료 후 소스 분석을 시작하기 전의 대기 시간입니다. 0~30000ms 범위로 적용됩니다.")]
    [DefaultValue(2000)]
    public int SourceAnalysisDelayMilliseconds { get; set; } = 2000;

    [Category("진단")]
    [DisplayName("탐색 실패 시 인덱스 크기 표시")]
    [Description("대응 파일을 찾지 못했을 때 상태 표시줄에 인덱싱된 파일 개수를 표시합니다.")]
    [DefaultValue(true)]
    public bool ShowIndexCountOnFailure { get; set; } = true;

    [Browsable(false)]
    [DefaultValue(false)]
    public bool LegacySettingsMigrated { get; set; }

    internal SolutionFileIndexConfiguration CreateConfiguration() =>
        new(
            UsePersistentFileCache,
            EnableSourceAnalysis,
            TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(30_000, SourceAnalysisDelayMilliseconds))));
}
