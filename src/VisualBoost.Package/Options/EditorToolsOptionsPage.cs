using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.Options;

[Guid("b43c2b21-ea8c-49a1-87da-b5d27237c2ab")]
public sealed class EditorToolsOptionsPage : DialogPage
{
    [Category("코드 도구"), DisplayName("빠른 인클루드 사용"), DefaultValue(true)]
    [Description("오류로 표시된 C++ 이름에서 코드 도구(Shift+Alt+Q)를 열면 그 이름을 선언한 헤더의 #include를 추가합니다.")]
    public bool QuickIncludeEnabled { get; set; } = true;
    [Category("주석 탐색"), DisplayName("주석 링크 사용"), DefaultValue(true)]
    [Description("C++·C# 주석에 적힌 파일 이름을 링크로 표시하고 Ctrl+클릭으로 엽니다. '파일명:줄'로 적으면 그 줄로 이동합니다.")]
    public bool CommentLinksEnabled { get; set; } = true;
    protected override void OnApply(PageApplyEventArgs e)
    { base.OnApply(e); if (e.ApplyBehavior != ApplyKind.Cancel) CommentLinks.CommentLinkRuntime.Enabled = CommentLinksEnabled; }
}
