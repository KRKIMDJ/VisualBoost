using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.Services;

internal sealed class SolutionProjectInfo
{
    public SolutionProjectInfo(string name, string rootPath)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
    }

    public string Name { get; }

    public string RootPath { get; }
}

internal static class SolutionProjectCatalog
{
    public static IReadOnlyList<SolutionProjectInfo> Collect(DTE2 dte, string? preferredRoot)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var projects = new List<SolutionProjectInfo>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (Project project in dte.Solution.Projects)
            {
                CollectProject(project, projects, visited);
            }
        }
        catch (COMException)
        {
            // 로드 중인 프로젝트는 이번 팝업에서만 생략하고 파일 검색 자체는 유지합니다.
        }

        AddActiveProjectRoot(dte, preferredRoot, projects, visited);
        return projects
            .OrderByDescending(project => project.RootPath.Length)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void CollectProject(
        Project project,
        ICollection<SolutionProjectInfo> projects,
        ISet<string> visited)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (string.Equals(project.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase))
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.SubProject is not null)
                    {
                        CollectProject(item.SubProject, projects, visited);
                    }
                }

                return;
            }

            Add(project.Name, Path.GetDirectoryName(project.FullName), projects, visited);
        }
        catch (COMException)
        {
            // 일부 비표준 또는 언로드된 프로젝트는 이름이나 경로 조회를 지원하지 않습니다.
        }
    }

    private static void AddActiveProjectRoot(
        DTE2 dte,
        string? preferredRoot,
        ICollection<SolutionProjectInfo> projects,
        ISet<string> visited)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(preferredRoot))
        {
            return;
        }

        try
        {
            Add(
                dte.ActiveDocument?.ProjectItem?.ContainingProject?.Name,
                preferredRoot,
                projects,
                visited);
        }
        catch (COMException)
        {
            // 활성 문서가 전환되는 중이면 일반 프로젝트 루트 정보만 사용합니다.
        }
    }

    private static void Add(
        string? name,
        string? rootPath,
        ICollection<SolutionProjectInfo> projects,
        ISet<string> visited)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(rootPath!).TrimEnd('\\', '/');
        }
        catch (Exception exception) when (
            exception is ArgumentException ||
            exception is NotSupportedException ||
            exception is PathTooLongException)
        {
            return;
        }

        var key = name + "\0" + normalizedRoot;
        if (visited.Add(key))
        {
            projects.Add(new SolutionProjectInfo(name!, normalizedRoot));
        }
    }
}
