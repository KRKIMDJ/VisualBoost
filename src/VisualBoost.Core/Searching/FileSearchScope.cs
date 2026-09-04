using System;
using System.Collections.Generic;

namespace VisualBoost.Core.Searching;

public enum FileSearchScope
{
    All,
    CurrentProject,
    OpenFiles,
    ExternalSources,
}

public static class FileSearchScopeFilter
{
    public static bool Includes(
        string path,
        FileSearchScope scope,
        string? preferredRoot,
        string? solutionRoot,
        ISet<string>? openFiles = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return scope switch
        {
            FileSearchScope.All => true,
            FileSearchScope.CurrentProject => IsInside(path, preferredRoot),
            FileSearchScope.OpenFiles => openFiles?.Contains(path) == true,
            FileSearchScope.ExternalSources => IsExternal(path, preferredRoot, solutionRoot),
            _ => true,
        };
    }

    public static bool IsInside(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = root!.TrimEnd('\\', '/');
        if (normalizedRoot.Length == 0)
        {
            return false;
        }

        return string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExternal(string path, string? preferredRoot, string? solutionRoot)
    {
        var boundary = string.IsNullOrWhiteSpace(solutionRoot) ? preferredRoot : solutionRoot;
        return !string.IsNullOrWhiteSpace(boundary) && !IsInside(path, boundary);
    }
}
