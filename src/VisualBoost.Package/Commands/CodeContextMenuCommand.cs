using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;

namespace VisualBoost.Commands;

/// <summary>
/// 편집기 문맥 메뉴의 <c>Visual Boost</c> 하위 메뉴를 C++ 문서에서만 보이게 합니다. 하위 메뉴의 정의로 이동·참조 찾기·
/// 헤더/구현 전환·코드 도구는 C++ 전용이거나 다른 언어에서는 Visual Studio 기본 명령과 겹치기 때문입니다.
/// 단축키와 Tools 메뉴의 같은 명령은 그대로 둡니다. 패키지가 로드되기 전에는 VSCT 기본값대로 보입니다.
/// </summary>
internal sealed class CodeContextMenuCommand
{
    private static readonly Guid CommandSet = new("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e");

    private readonly IVsTextManager textManager;
    private readonly IVsEditorAdaptersFactoryService adapters;

    private CodeContextMenuCommand(OleMenuCommandService commandService, IVsTextManager textManager, IVsEditorAdaptersFactoryService adapters)
    {
        this.textManager = textManager;
        this.adapters = adapters;
        // 하위 메뉴는 실행되지 않으며, 표시 여부 질의에만 응답합니다.
        var menu = new OleMenuCommand((_, _) => { }, new CommandID(CommandSet, CommandIds.CodeContextMenu));
        menu.BeforeQueryStatus += OnBeforeQueryStatus;
        commandService.AddCommand(menu);
    }

    public static async Task InitializeAsync(VisualBoostPackage package, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        var textManager = await package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
        var components = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        Assumes.Present(commandService);
        Assumes.Present(textManager);
        Assumes.Present(components);
        _ = new CodeContextMenuCommand(commandService, textManager, components.GetService<IVsEditorAdaptersFactoryService>());
    }

    private void OnBeforeQueryStatus(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (sender is not OleMenuCommand menu) return;
        // 문맥 메뉴는 활성 편집기에서 열리므로 그 문서의 content type으로 판단합니다.
        var visible = textManager.GetActiveView(1, null, out var native) == 0 && native is not null &&
                      adapters.GetWpfTextView(native) is { IsClosed: false } view &&
                      view.TextBuffer.ContentType.IsOfType("C/C++");
        menu.Visible = visible;
        menu.Enabled = visible;
    }
}
