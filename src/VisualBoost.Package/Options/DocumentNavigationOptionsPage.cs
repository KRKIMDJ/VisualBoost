using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VisualBoost.DocumentNavigation;

namespace VisualBoost.Options;

[Guid("9ff75a13-c1e9-4dc6-bbe2-7b7a66a20382")]
public sealed class DocumentNavigationOptionsPage : DialogPage
{
    [Category("현재 문서"), DisplayName("상단 함수 탐색 줄 표시"), DefaultValue(true)]
    [Description("현재 함수와 검색 줄을 편집기 위에 표시합니다. 숨겨도 Alt+M 검색은 사용할 수 있습니다.")]
    public bool ShowBar { get; set; } = true;
    [Category("현재 문서"), DisplayName("함수 목록 이름순 정렬"), DefaultValue(false)]
    [Description("기본은 문서 등장 순서입니다. 검색어가 있으면 이름 일치 품질을 우선합니다.")]
    public bool NameOrder { get; set; }
    internal void Publish() => DocumentNavigationSettings.Publish(NameOrder, ShowBar);
    protected override void OnApply(PageApplyEventArgs e)
    { base.OnApply(e); if (e.ApplyBehavior != ApplyKind.Cancel) Publish(); }
}
