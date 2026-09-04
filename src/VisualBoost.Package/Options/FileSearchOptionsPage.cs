using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.Options;

[Guid("2f7d9eb0-2012-43f2-8a4c-11533c74a85e")]
public sealed class FileSearchOptionsPage : DialogPage
{
    [Category("검색")]
    [DisplayName("최대 결과 수")]
    [Description("파일 탐색 창에 표시할 최대 결과 수입니다. 10~500 범위로 적용됩니다.")]
    [DefaultValue(100)]
    public int MaximumResults { get; set; } = 100;

    [Category("검색")]
    [DisplayName("입력 지연 시간(ms)")]
    [Description("연속 입력이 끝난 뒤 검색을 시작할 때까지 기다리는 시간입니다. 0~500ms 범위로 적용됩니다.")]
    [DefaultValue(80)]
    public int InputDelayMilliseconds { get; set; } = 80;

    [Category("검색")]
    [DisplayName("빈 검색에서 추천 표시")]
    [Description("검색어가 없을 때 최근 파일과 현재 프로젝트 파일을 표시합니다.")]
    [DefaultValue(true)]
    public bool ShowSuggestionsForEmptyQuery { get; set; } = true;

    [Browsable(false)]
    [DefaultValue("All")]
    public string FileSearchScope { get; set; } = "All";

    [Browsable(false)]
    [DefaultValue(760d)]
    public double FileSearchWidth { get; set; } = 760d;

    [Browsable(false)]
    [DefaultValue(520d)]
    public double FileSearchHeight { get; set; } = 520d;

    [Browsable(false)]
    [DefaultValue(-1d)]
    public double FileSearchLeft { get; set; } = -1d;

    [Browsable(false)]
    [DefaultValue(-1d)]
    public double FileSearchTop { get; set; } = -1d;

    [Browsable(false)]
    [DefaultValue(false)]
    public bool FileSearchPlacementSaved { get; set; }

    [Browsable(false)]
    [DefaultValue(false)]
    public bool LegacySettingsMigrated { get; set; }

    internal int GetMaximumResults() => Math.Max(10, Math.Min(500, MaximumResults));

    internal int GetInputDelayMilliseconds() =>
        Math.Max(0, Math.Min(500, InputDelayMilliseconds));
}
