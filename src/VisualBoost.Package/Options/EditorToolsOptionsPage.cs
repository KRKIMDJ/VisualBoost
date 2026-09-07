using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.Options;

[Guid("b43c2b21-ea8c-49a1-87da-b5d27237c2ab")]
public sealed class EditorToolsOptionsPage : DialogPage
{
    [Category("코드 도구"), DisplayName("빠른 인클루드 사용"), DefaultValue(true)]
    public bool QuickIncludeEnabled { get; set; } = true;
    [Category("주석 탐색"), DisplayName("주석 파일 링크 사용"), DefaultValue(true)]
    public bool CommentLinksEnabled { get; set; } = true;
    protected override void OnApply(PageApplyEventArgs e)
    { base.OnApply(e); if (e.ApplyBehavior != ApplyKind.Cancel) CommentLinks.CommentLinkRuntime.Enabled = CommentLinksEnabled; }
}
