using System;
using VisualBoost.Core.Searching;

namespace VisualBoost.Services;

internal static class ProjectNameResolver
{
    public static string Resolve(string fullPath, System.Collections.Generic.IReadOnlyList<SolutionProjectInfo> projects)
    {
        foreach (var project in projects)
        {
            if (FileSearchScopeFilter.IsInside(fullPath, project.RootPath))
            {
                return project.Name;
            }
        }

        return TryInferSourceModule(fullPath, out var moduleName) ? moduleName : "—";
    }

    private static bool TryInferSourceModule(string fullPath, out string moduleName)
    {
        moduleName = string.Empty;
        var segments = fullPath
            .Replace('/', '\\')
            .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (var index = segments.Length - 2; index >= 0; index--)
        {
            if (!string.Equals(segments[index], "Source", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidateOffset = index + 1;
            if (candidateOffset >= segments.Length - 1)
            {
                return false;
            }

            if (IsSourceCategory(segments[candidateOffset]) && candidateOffset + 1 < segments.Length - 1)
            {
                candidateOffset++;
            }

            moduleName = segments[candidateOffset];
            return moduleName.Length > 0;
        }

        return false;
    }

    private static bool IsSourceCategory(string value) =>
        string.Equals(value, "Runtime", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Editor", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Developer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Programs", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "ThirdParty", StringComparison.OrdinalIgnoreCase);
}
