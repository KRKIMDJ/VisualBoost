using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VisualBoost.Services;

internal static class UnrealEngineSourceLocator
{
    private static readonly Regex EngineAssociationPattern = new(
        "\\\"EngineAssociation\\\"\\s*:\\s*\\\"(?<value>[^\\\"]+)\\\"",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Find(
        string? solutionDirectory,
        IEnumerable<string> discoveredPaths)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in discoveredPaths)
        {
            AddEngineSourceFromPath(results, path);
        }

        if (string.IsNullOrWhiteSpace(solutionDirectory) || !Directory.Exists(solutionDirectory))
        {
            return results.ToArray();
        }

        var existingSolutionDirectory = solutionDirectory!;
        AddNearbySourceBuild(results, existingSolutionDirectory);
        foreach (var projectFile in EnumerateProjectDescriptors(existingSolutionDirectory))
        {
            var association = ReadEngineAssociation(projectFile);
            if (string.IsNullOrWhiteSpace(association))
            {
                continue;
            }

            var knownAssociation = association!;
            AddRegisteredBuild(results, knownAssociation);
            AddLauncherBuild(results, knownAssociation);
        }

        return results.ToArray();
    }

    private static IEnumerable<string> EnumerateProjectDescriptors(string solutionDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(solutionDirectory, "*.uproject", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? ReadEngineAssociation(string projectFile)
    {
        try
        {
            var match = EngineAssociationPattern.Match(File.ReadAllText(projectFile));
            return match.Success ? match.Groups["value"].Value.Trim() : null;
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is SecurityException)
        {
            return null;
        }
    }

    private static void AddNearbySourceBuild(ISet<string> results, string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            AddIfEngineSource(results, Path.Combine(current.FullName, "Engine", "Source"));
            current = current.Parent;
        }
    }

    private static void AddRegisteredBuild(ISet<string> results, string association)
    {
        try
        {
            using var builds = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
            if (builds?.GetValue(association) is string root)
            {
                AddIfEngineSource(results, Path.Combine(root, "Engine", "Source"));
            }
        }
        catch (Exception exception) when (
            exception is SecurityException ||
            exception is UnauthorizedAccessException ||
            exception is IOException)
        {
            // 등록 정보를 읽지 못해도 프로젝트와 include 경로 기반 탐색은 계속 사용합니다.
        }
    }

    private static void AddLauncherBuild(ISet<string> results, string association)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return;
        }

        AddIfEngineSource(
            results,
            Path.Combine(programFiles, "Epic Games", $"UE_{association}", "Engine", "Source"));
    }

    private static void AddEngineSourceFromPath(ISet<string> results, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var marker = $"{Path.DirectorySeparatorChar}Engine{Path.DirectorySeparatorChar}Source";
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return;
        }

        AddIfEngineSource(results, normalized.Substring(0, markerIndex + marker.Length));
    }

    private static void AddIfEngineSource(ISet<string> results, string sourceDirectory)
    {
        if (Directory.Exists(sourceDirectory))
        {
            results.Add(Path.GetFullPath(sourceDirectory));
        }
    }
}
