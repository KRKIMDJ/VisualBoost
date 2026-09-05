using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Text.Outlining;
using VisualBoost.DocumentNavigation;

namespace VisualBoost.Commands;

internal static class OpenDocumentMembersCommand
{
    public static async Task InitializeAsync(VisualBoostPackage package, CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var service = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        Assumes.Present(service);
        service.AddCommand(new OleMenuCommand((sender, args) => SearchCommandRunner.Run(package, "OpenDocumentMembers", () => OpenAsync(package)),
            new CommandID(new Guid("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e"), CommandIds.OpenDocumentMembers)));
    }
    private static async Task OpenAsync(VisualBoostPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var manager = await package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
        var components = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        if (manager is not null && components is not null && manager.GetActiveView(1, null, out var nativeView) == 0)
        {
            var view = components.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(nativeView);
            if (view is not null && !view.IsClosed && view.TextBuffer.ContentType.IsOfType("C/C++"))
            {
                var session = view.Properties.GetOrCreateSingletonProperty(() =>
                    new DocumentNavigationSession(view, components.GetService<IOutliningManagerService>()));
                session.Open();
                return;
            }
        }
        if (await package.GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar status)
            status.SetText("문서 함수 탐색은 C++ 코드 편집기에서 사용할 수 있습니다.");
    }
}
