using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VisualBoost.Commands;

internal static class SearchCommandRunner
{
    private static readonly HashSet<string> Running = new(StringComparer.Ordinal);

    public static void Run(VisualBoostPackage package, string name, Func<Task> action)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!Running.Add(name)) return;
        package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                // 명령 경계에서 기록해야 창 생성 실패가 무반응으로 보이지 않습니다.
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                ActivityLog.LogError("VisualBoost/" + name, exception.ToString());
                var status = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
                status?.SetText("VisualBoost 탐색을 실행하지 못했습니다: " + exception.Message);
            }
            finally
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Running.Remove(name);
            }
        }).FileAndForget("VisualBoost/" + name);
    }
}
