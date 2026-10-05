using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace VisualBoost.Analysis;

/// <summary>동일 분석 패스의 루트 첫 경로 성분을 한 번 색인화하여 없는 include마다 모든 루트를 디스크 조회하지 않습니다.</summary>
internal sealed class IncludeRootLookup
{
    private readonly IReadOnlyList<string> roots;
    private Dictionary<string, List<int>>? firstComponents;
    private readonly List<int> uncertain = new();
    internal int FileProbeCount { get; private set; }

    internal IncludeRootLookup(IReadOnlyList<string> roots) { this.roots = roots; }

    internal string? Find(string relative, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (roots.Count == 0) return null;
        IEnumerable<int> candidates;
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (Path.IsPathRooted(relative) || first.Length == 0 || first == "." || first == "..")
            candidates = Enumerable.Range(0, roots.Count);
        else
        {
            EnsureIndex(token);
            candidates = (firstComponents!.TryGetValue(first, out var found) ? found : Enumerable.Empty<int>())
                .Concat(uncertain).Distinct().OrderBy(i => i);
        }
        foreach (var i in candidates)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(roots[i], relative));
            FileProbeCount++;
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private void EnsureIndex(CancellationToken token)
    {
        if (firstComponents is not null) return;
        var index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < roots.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(roots[i], "*", SearchOption.TopDirectoryOnly))
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry);
                    if (!index.TryGetValue(name, out var values)) index.Add(name, values = new List<int>());
                    values.Add(i);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            { uncertain.Add(i); /* 열거가 거절된 루트는 원래 개별 파일 조회로 확인합니다. */ }
        }
        token.ThrowIfCancellationRequested();
        firstComponents = index;
    }
}
