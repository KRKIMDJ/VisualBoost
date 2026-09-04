using System;
using System.IO;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.Services;

internal static class SolutionSearchRootCollector
{
    public static SolutionIndexDiscoveryResult Collect(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // DTE 프로젝트 트리는 대규모 Solution에서 파일 하나마다 COM 호출을 발생시킬 수 있습니다.
        // UI 스레드에서는 Solution 경로만 캡처하고 실제 파일 및 프로젝트 탐색은 백그라운드에서 수행합니다.
        var solutionFile = dte.Solution?.FullName ?? string.Empty;
        var solutionDirectory = string.IsNullOrWhiteSpace(solutionFile)
            ? null
            : Path.GetDirectoryName(solutionFile);
        var roots = string.IsNullOrWhiteSpace(solutionDirectory)
            ? Array.Empty<string>()
            : new[] { solutionDirectory! };

        return new SolutionIndexDiscoveryResult(solutionFile, roots, Array.Empty<string>());
    }
}
