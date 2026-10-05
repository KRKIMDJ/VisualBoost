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

    /// <summary>C++ 프로젝트 목록과 활성 Solution 구성 이름을 모읍니다. 빌드 도구 경로는 호출자가 채웁니다.</summary>
    public static async Task<CompileCommandSources> CollectAsync(DTE2 dte, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var result = new List<MsBuildProjectConfiguration>();
        string? configuration = null;
        try
        {
            configuration = dte.Solution?.SolutionBuild?.ActiveConfiguration?.Name;
        }
        catch (COMException)
        {
            // 폴더 열기 작업 영역 등 구성이 없는 경우입니다.
        }

        if (dte.Solution?.Projects is { } projects)
        {
            foreach (Project project in projects)
            {
                Visit(project, result);
            }
        }

        return new CompileCommandSources { Projects = result, SolutionConfiguration = configuration };
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
