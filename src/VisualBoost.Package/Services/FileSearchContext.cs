using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.Services;

internal sealed class FileSearchContext
{
    private FileSearchContext(
        string? preferredRoot,
        string? solutionRoot,
        IReadOnlyCollection<string> openFiles,
        IReadOnlyList<SolutionProjectInfo> projects)
    {
        PreferredRoot = preferredRoot;
        SolutionRoot = solutionRoot;
        OpenFiles = openFiles;
        Projects = projects;
    }

    public string? PreferredRoot { get; }

    public string? SolutionRoot { get; }

    public IReadOnlyCollection<string> OpenFiles { get; }

    public IReadOnlyList<SolutionProjectInfo> Projects { get; }

    public static FileSearchContext Collect(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var preferredRoot = GetActiveProjectDirectory(dte);
        return new FileSearchContext(
            preferredRoot,
            GetSolutionDirectory(dte),
            GetOpenDocumentPaths(dte),
            SolutionProjectCatalog.Collect(dte, preferredRoot));
    }

    private static string? GetActiveProjectDirectory(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var activeDocument = dte.ActiveDocument;
            var projectFile = activeDocument?.ProjectItem?.ContainingProject?.FullName;
            var projectDirectory = string.IsNullOrWhiteSpace(projectFile)
                ? null
                : Path.GetDirectoryName(projectFile);
            var activeDocumentPath = activeDocument?.FullName;
            var documentDirectory = string.IsNullOrWhiteSpace(activeDocumentPath)
                ? null
                : Path.GetDirectoryName(activeDocumentPath);
            return FindCommonDirectory(projectDirectory, documentDirectory) ?? projectDirectory;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? GetSolutionDirectory(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return string.IsNullOrWhiteSpace(dte.Solution.FullName)
                ? null
                : Path.GetDirectoryName(dte.Solution.FullName);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static IReadOnlyCollection<string> GetOpenDocumentPaths(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (EnvDTE.Document document in dte.Documents)
            {
                if (!string.IsNullOrWhiteSpace(document.FullName))
                {
                    paths.Add(Path.GetFullPath(document.FullName));
                }
            }
        }
        catch (COMException)
        {
            // 일부 문서 공급자는 로드 중 컬렉션 열거를 지원하지 않을 수 있습니다.
        }

        return paths;
    }

    private static string? FindCommonDirectory(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return null;
        }

        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = new DirectoryInfo(first!); current is not null; current = current.Parent)
        {
            ancestors.Add(current.FullName);
        }

        for (var current = new DirectoryInfo(second!); current is not null; current = current.Parent)
        {
            if (ancestors.Contains(current.FullName))
            {
                // 드라이브 루트는 프로젝트 범위로 의미가 없으며 거의 모든 후보를 같은 값으로 올립니다.
                return current.Parent is null ? null : current.FullName;
            }
        }

        return null;
    }
}
