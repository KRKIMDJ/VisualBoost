using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.Services;

internal static class SolutionSearchRootCollector
{
    public static IReadOnlyList<string> Collect(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var solutionFile = dte.Solution?.FullName;
        if (!string.IsNullOrWhiteSpace(solutionFile))
        {
            AddDirectory(roots, Path.GetDirectoryName(solutionFile));
        }

        if (dte.Solution is null)
        {
            return new List<string>(roots);
        }

        foreach (Project project in dte.Solution.Projects)
        {
            CollectProjectRoots(project, roots);
        }

        return new List<string>(roots);
    }

    private static void CollectProjectRoots(Project project, ISet<string> roots)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!string.IsNullOrWhiteSpace(project.FullName))
            {
                AddDirectory(roots, Path.GetDirectoryName(project.FullName));
            }

            var projectItems = project.ProjectItems;
            if (projectItems is null)
            {
                return;
            }

            foreach (ProjectItem item in projectItems)
            {
                if (item.SubProject is not null)
                {
                    CollectProjectRoots(item.SubProject, roots);
                }
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException ||
            exception is NotImplementedException ||
            exception is COMException)
        {
            // 일부 비표준 프로젝트는 DTE 속성을 구현하지 않으므로 Solution 루트 탐색으로 대체합니다.
        }
    }

    private static void AddDirectory(ISet<string> roots, string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            roots.Add(Path.GetFullPath(directory));
        }
    }
}
