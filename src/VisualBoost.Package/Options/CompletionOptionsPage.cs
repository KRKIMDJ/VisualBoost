using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Completion;

namespace VisualBoost.Options;

[Guid("71ce980b-bc48-4ff0-a1dc-cf016cc142c6")]
public sealed class CompletionOptionsPage : DialogPage
{
    [Category("입력 보조"), DisplayName("인덱스 기반 이름 자동완성"), DefaultValue(true)]
    [Description("기본 IntelliSense가 열려 있지 않을 때 C++ 이름을 제안합니다. ↓로 선택한 뒤 Tab으로 삽입하며 Enter와 문장부호는 확정하지 않습니다.")]
    public bool Enabled { get; set; } = true;

    [Category("입력 보조"), DisplayName("최소 입력 글자 수"), DefaultValue(3)]
    public int MinimumLength { get; set; } = 3;

    [Category("입력 보조"), DisplayName("입력 대기 시간(ms)"), DefaultValue(250)]
    public int DelayMilliseconds { get; set; } = 250;

    internal void Publish()
    {
        CompletionRuntime.Enabled = Enabled;
        CompletionRuntime.MinimumLength = Math.Max(3, Math.Min(10, MinimumLength));
        CompletionRuntime.DelayMilliseconds = Math.Max(150, Math.Min(1000, DelayMilliseconds));
    }

    protected override void OnApply(PageApplyEventArgs e)
    {
        MinimumLength = Math.Max(3, Math.Min(10, MinimumLength));
        DelayMilliseconds = Math.Max(150, Math.Min(1000, DelayMilliseconds));
        base.OnApply(e);
        if (e.ApplyBehavior != ApplyKind.Cancel) Publish();
    }
}
