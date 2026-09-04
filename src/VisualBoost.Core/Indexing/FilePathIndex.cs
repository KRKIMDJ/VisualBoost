using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Searching;

namespace VisualBoost.Core.Indexing;

public sealed class FilePathIndex : IDisposable
{
    private readonly ReaderWriterLockSlim gate = new();
    private Dictionary<string, HashSet<string>> pathsByStem = CreateMap();
    private HashSet<string> allPaths = new(StringComparer.OrdinalIgnoreCase);
    private string[]? searchSnapshot;

    public int Count
    {
        get
        {
            gate.EnterReadLock();
            try
            {
                return allPaths.Count;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }
    }

    public void ReplaceAll(IEnumerable<string> paths)
    {
        if (paths is null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        var replacementPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacementMap = CreateMap();

        foreach (var path in paths)
        {
            if (!TryNormalize(path, out var normalizedPath) || !replacementPaths.Add(normalizedPath))
            {
                continue;
            }

            AddToMap(replacementMap, normalizedPath);
        }

        gate.EnterWriteLock();
        try
        {
            allPaths = replacementPaths;
            pathsByStem = replacementMap;
            searchSnapshot = replacementPaths.ToArray();
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    public bool Add(string path)
    {
        if (!TryNormalize(path, out var normalizedPath))
        {
            return false;
        }

        gate.EnterWriteLock();
        try
        {
            if (!allPaths.Add(normalizedPath))
            {
                return false;
            }

            AddToMap(pathsByStem, normalizedPath);
            searchSnapshot = null;
            return true;
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    public bool Remove(string path)
    {
        if (!TryNormalize(path, out var normalizedPath))
        {
            return false;
        }

        gate.EnterWriteLock();
        try
        {
            if (!allPaths.Remove(normalizedPath))
            {
                return false;
            }

            var stem = Path.GetFileNameWithoutExtension(normalizedPath);
            if (pathsByStem.TryGetValue(stem, out var paths))
            {
                paths.Remove(normalizedPath);
                if (paths.Count == 0)
                {
                    pathsByStem.Remove(stem);
                }
            }

            searchSnapshot = null;
            return true;
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    public IReadOnlyList<string> FindByStem(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return Array.Empty<string>();
        }

        gate.EnterReadLock();
        try
        {
            return pathsByStem.TryGetValue(stem, out var paths)
                ? paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
        }
        finally
        {
            gate.ExitReadLock();
        }
    }

    public IReadOnlyList<FileSearchMatch> Search(
        string query,
        int maximumResults = 50,
        CancellationToken cancellationToken = default)
    {
        string[] paths;
        gate.EnterUpgradeableReadLock();
        try
        {
            if (searchSnapshot is null)
            {
                gate.EnterWriteLock();
                try
                {
                    searchSnapshot ??= allPaths.ToArray();
                }
                finally
                {
                    gate.ExitWriteLock();
                }
            }

            paths = searchSnapshot;
        }
        finally
        {
            gate.ExitUpgradeableReadLock();
        }

        // 점수 계산 중에는 쓰기 잠금을 유지하지 않아 파일 감시 이벤트 처리를 막지 않습니다.
        return FuzzyFileSearch.Search(query, paths, maximumResults, cancellationToken);
    }

    public void Clear() => ReplaceAll(Array.Empty<string>());

    public void Dispose() => gate.Dispose();

    private static Dictionary<string, HashSet<string>> CreateMap() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static void AddToMap(Dictionary<string, HashSet<string>> map, string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (!map.TryGetValue(stem, out var paths))
        {
            paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            map.Add(stem, paths);
        }

        paths.Add(path);
    }

    private static bool TryNormalize(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath.Length > 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException ||
            exception is NotSupportedException ||
            exception is PathTooLongException)
        {
            return false;
        }
    }
}
