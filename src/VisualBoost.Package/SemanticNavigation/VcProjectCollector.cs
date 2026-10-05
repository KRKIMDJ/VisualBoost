using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Core.SemanticNavigation;

namespace VisualBoost.SemanticNavigation;

/// <summary>Solution의 C++ 프로젝트(vcxproj)와 활성 Solution 구성에 대응하는 프로젝트 구성을 모읍니다.</summary>
internal static class VcProjectCollector
{
    private const string VcProjectKind = "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";
    private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

    /// <summary>현재 VS 설치의 MSBuild입니다. 프로젝트를 연 VS와 같은 C++ 도구 집합을 쓰기 위해 다른 설치로 대체하지 않습니다.</summary>
    public static string? FindMsBuild()
    {
        var install = ClangdLocator.CurrentInstallDirectory();
        return install is null ? null : MsBuildCompileCommands.FindMsBuild(install);
    }

    public static async Task<IReadOnlyList<MsBuildProjectConfiguration>> CollectAsync(DTE2 dte, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var result = new List<MsBuildProjectConfiguration>();
        if (dte.Solution?.Projects is not { } projects) return result;
        foreach (Project project in projects)
        {
            Visit(project, result);
        }

        return result;
    }

    private static void Visit(Project project, List<MsBuildProjectConfiguration> result)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (string.Equals(project.Kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase))
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.SubProject is { } nested) Visit(nested, result);
                }

                return;
            }

            if (!string.Equals(project.Kind, VcProjectKind, StringComparison.OrdinalIgnoreCase) ||
                !project.FullName.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var active = project.ConfigurationManager?.ActiveConfiguration;
            if (active is not null) result.Add(new MsBuildProjectConfiguration(project.FullName, active.ConfigurationName, active.PlatformName));
        }
        catch (Exception exception) when (exception is COMException || exception is NotImplementedException || exception is ArgumentException)
        {
            // 언로드됐거나 구성 정보를 주지 않는 프로젝트는 건너뜁니다.
        }
    }
}
