using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace VisualBoost.Services;

internal static class CppProjectSearchRootLocator
{
    private static readonly HashSet<string> IncludePropertyNames = new(StringComparer.Ordinal)
    {
        "AdditionalIncludeDirectories",
        "IncludePath",
        "IncludeSearchPath",
    };

    public static IReadOnlyList<string> Find(
        string? solutionDirectory,
        IEnumerable<string> indexedFiles)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in indexedFiles.Where(path =>
                     string.Equals(Path.GetExtension(path), ".vcxproj", StringComparison.OrdinalIgnoreCase)))
        {
            ReadProject(projectFile, solutionDirectory, results);
        }

        return results.ToArray();
    }

    private static void ReadProject(
        string projectFile,
        string? solutionDirectory,
        ISet<string> results)
    {
        try
        {
            var projectDirectory = Path.GetDirectoryName(projectFile) ?? string.Empty;
            var document = XDocument.Load(projectFile, LoadOptions.None);
            foreach (var element in document.Descendants().Where(element =>
                         IncludePropertyNames.Contains(element.Name.LocalName)))
            {
                AddPathList(results, element.Value, projectDirectory, solutionDirectory);
            }
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is SecurityException ||
            exception is XmlException)
        {
            // 읽을 수 없는 프로젝트 하나가 전체 인덱스 생성을 중단시키지 않게 건너뜁니다.
        }
    }

    private static void AddPathList(
        ISet<string> results,
        string value,
        string projectDirectory,
        string? solutionDirectory)
    {
        foreach (var item in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Environment.ExpandEnvironmentVariables(item.Trim().Trim('"'))
                .Replace("$(ProjectDir)", EnsureTrailingSeparator(projectDirectory))
                .Replace("$(MSBuildProjectDirectory)", projectDirectory);
            if (!string.IsNullOrWhiteSpace(solutionDirectory))
            {
                candidate = candidate.Replace("$(SolutionDir)", EnsureTrailingSeparator(solutionDirectory!));
            }

            if (string.IsNullOrWhiteSpace(candidate) ||
                candidate.IndexOf("$(", StringComparison.Ordinal) >= 0 ||
                candidate.IndexOf("%(", StringComparison.Ordinal) >= 0)
            {
                continue;
            }

            try
            {
                var path = Path.IsPathRooted(candidate)
                    ? candidate
                    : Path.Combine(projectDirectory, candidate);
                if (Directory.Exists(path))
                {
                    results.Add(Path.GetFullPath(path));
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is PathTooLongException ||
                exception is SecurityException)
            {
                // 잘못되었거나 현재 환경에서 해석할 수 없는 경로는 제외합니다.
            }
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
