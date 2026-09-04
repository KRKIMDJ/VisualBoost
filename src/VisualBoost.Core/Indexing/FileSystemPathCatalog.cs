using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VisualBoost.Core.Indexing;

public static class FileSystemPathCatalog
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".cache", "bin", "obj", "packages", "TestResults",
        "node_modules", "Binaries", "Intermediate", "DerivedDataCache", "Saved",
    };

    public static IReadOnlyList<string> GetFiles(
        IEnumerable<string> searchRoots,
        CancellationToken cancellationToken = default)
    {
        if (searchRoots is null)
        {
            throw new ArgumentNullException(nameof(searchRoots));
        }

        var results = new List<string>();
        var pending = new Stack<string>();
        foreach (var root in searchRoots)
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                pending.Push(root);
            }
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(file);
                }

                foreach (var childDirectory in Directory.EnumerateDirectories(directory))
                {
                    if (ShouldTraverseDirectory(childDirectory))
                    {
                        pending.Push(childDirectory);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 접근할 수 없는 외부 디렉터리가 전체 탐색을 중단시키지 않게 건너뜁니다.
            }
            catch (IOException)
            {
                // 탐색 중 삭제되거나 잠긴 디렉터리는 다음 실행에서 다시 확인합니다.
            }
        }

        return results;
    }

    public static bool IsExcludedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (ExcludedDirectories.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldTraverseDirectory(string directory)
    {
        if (ExcludedDirectories.Contains(Path.GetFileName(directory)))
        {
            return false;
        }

        try
        {
            // 연결점과 심볼릭 링크를 따라가면 Solution 밖으로 벗어나거나 순환할 수 있습니다.
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return false;
        }
    }
}
