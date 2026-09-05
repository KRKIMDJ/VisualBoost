using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Coloring;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Options;

[Guid("1c2de70b-3114-4f3a-b2db-e7dedbc5645c")]
public sealed class ColoringOptionsPage : DialogPage
{
    [Category("일반"), DisplayName("C++ 의미 색상 사용"), DefaultValue(true)]
    [Description("VS가 판별한 타입·변수·함수·매크로에 색상을 적용합니다. 고대비 모드에서는 VS 색상을 유지합니다.")]
    public bool Enabled { get; set; } = true;

    [Category("일반"), DisplayName("색상 설정 기준"), DefaultValue(ColoringColorSource.Palette)]
    [Description("Palette: 아래 테마별 팔레트. FontsAndColors: 환경 > 글꼴 및 색 > 텍스트 편집기의 VisualBoost 항목 전경색. 두 설정은 서로 덮어쓰지 않습니다.")]
    public ColoringColorSource ColorSource { get; set; } = ColoringColorSource.Palette;

    [Category("어두운 테마"), DisplayName("타입"), DefaultValue("")]
    [Editor(typeof(PaletteColorEditor), typeof(UITypeEditor)), PaletteDefault("#68D5C4")]
    [Description("… 버튼으로 색상 팔레트를 엽니다. 기본색으로 돌아가려면 값을 비우거나 항목을 다시 설정하세요.")]
    public string DarkType { get; set; } = "";
    [Category("어두운 테마"), DisplayName("변수·매개변수"), DefaultValue("")]
    [Editor(typeof(PaletteColorEditor), typeof(UITypeEditor)), PaletteDefault("#B4D8FA")]
    [Description("… 버튼으로 색상 팔레트를 엽니다. 기본색으로 돌아가려면 값을 비우거나 항목을 다시 설정하세요.")]
    public string DarkVariable { get; set; } = "";
    [Category("어두운 테마"), DisplayName("함수·메서드"), DefaultValue("")]
    [Editor(typeof(PaletteColorEditor), typeof(UITypeEditor)), PaletteDefault("#F2CB8D")]
    [Description("… 버튼으로 색상 팔레트를 엽니다. 기본색으로 돌아가려면 값을 비우거나 항목을 다시 설정하세요.")]
    public string DarkFunction { get; set; } = "";
    [Category("어두운 테마"), DisplayName("매크로"), DefaultValue("")]
    [Editor(typeof(PaletteColorEditor), typeof(UITypeEditor)), PaletteDefault("#D2AAF5")]
    [Description("… 버튼으로 색상 팔레트를 엽니다. 기본색으로 돌아가려면 값을 비우거나 항목을 다시 설정하세요.")]
    public string DarkMacro { get; set; } = "";

    [Category("밝은 테마"), DisplayName("타입"), DefaultValue("")]
    [Editor(typeof(PaletteColorEditor), typeof(UITypeEditor)), PaletteDefault("#006B5A")]
    [Description("… 버튼으로 색상 팔레트를 엽니다. 기본색으로 돌아가려면 값을 비우거나 항목을 다시 설정하세요.")]
    public string LightType { get; set; } = "";
    [Category("밝은 테마"), DisplayName("변수·매개변수"), DefaultValue("")]
    [Editor(typeof(PaletteColorEditor), typeof(UITypeEditor)), PaletteDefault("#205AA7")]
    [Description("… 버튼으로 색상 팔레트를 엽니다. 기본색으로 돌아가려면 값을 비우거나 항목을 다시 설정하세요.")]
    public string LightVariable { get; set; } = "";
    [Category("밝은 테마"), DisplayName("함수·메서드"), DefaultValue("")]
    [Editor(typeof(PaletteColorEditor), typeof(UITypeEditor)), PaletteDefault("#845114")]
    [Description("… 버튼으로 색상 팔레트를 엽니다. 기본색으로 돌아가려면 값을 비우거나 항목을 다시 설정하세요.")]
    public string LightFunction { get; set; } = "";
    [Category("밝은 테마"), DisplayName("매크로"), DefaultValue("")]
    [Editor(typeof(PaletteColorEditor), typeof(UITypeEditor)), PaletteDefault("#7842A0")]
    [Description("… 버튼으로 색상 팔레트를 엽니다. 기본색으로 돌아가려면 값을 비우거나 항목을 다시 설정하세요.")]
    public string LightMacro { get; set; } = "";

    internal ColoringSettings CreateSettings() => new(Enabled, Values(), ColorSource);
    private string[] Values() => new[] { DarkType, DarkVariable, DarkFunction, DarkMacro,
        LightType, LightVariable, LightFunction, LightMacro };

    protected override void OnApply(PageApplyEventArgs e)
    {
        foreach (var value in Values())
        {
            if (string.IsNullOrWhiteSpace(value) || SemanticColorPalette.TryParse(value, out _)) continue;
            e.ApplyBehavior = ApplyKind.Cancel;
            System.Windows.MessageBox.Show("색상은 #RRGGBB 형식으로 입력하거나 기본색을 사용하도록 비워 주세요.", "VisualBoost Coloring");
            return;
        }
        base.OnApply(e);
        if (e.ApplyBehavior != ApplyKind.Cancel) ColoringSettings.Publish(CreateSettings());
    }
}
