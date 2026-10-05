using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisualBoost.Core.Indexing;

namespace VisualBoost.Core.Analysis;

/// <summary>소스 분석 순서 등급입니다. 값이 작을수록 먼저 분석합니다.</summary>
public enum SourceAnalysisRank
{
    /// <summary>열려 있거나 최근에 연 파일입니다.</summary>
    Focus = 0,

    /// <summary>사용자가 연 파일과 같은 프로젝트(프로젝트가 너무 크면 같은 폴더)의 파일입니다.</summary>
    Related = 1,

    /// <summary>그 밖의 프로젝트 등록 파일입니다.</summary>
    Project = 2,

    /// <summary>프로젝트에 등록되지 않은 보충 파일입니다.</summary>
    Other = 3,

    /// <summary>엔진 소스입니다. 양이 가장 많아 마지막에 분석합니다.</summary>
    Engine = 4,
}

/// <summary>
/// 프로젝트 소속과 엔진 위치로 파일의 기본 분석 등급을 정합니다. 만든 뒤에는 바꾸지 않으므로 여러 스레드에서 읽어도 됩니다.
/// </summary>
public sealed class SourceAnalysisPriority
{
    /// <summary>
    /// 이보다 파일이 많은 프로젝트는 관련 파일로 통째로 올리지 않습니다. 엔진 전체를 담은 생성 프로젝트가 앞줄을 차지하지 않게 합니다.
    /// </summary>
    public const int MaxRelatedProjectFiles = 10000;

    // 프로젝트에 등록된 순서를 지켜 관련 파일을 올립니다.
    private readonly string[][] projects;
    private readonly Dictionary<string, int> smallestProject;
    private readonly string[] engineRoots;

    public SourceAnalysisPriority(IEnumerable<IEnumerable<string>> projectFiles, IEnumerable<string> engineRoots)
    {
        if (projectFiles is null) throw new ArgumentNullException(nameof(projectFiles));
        if (engineRoots is null) throw new ArgumentNullException(nameof(engineRoots));
        projects = projectFiles.Select(files => files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()).ToArray();
        smallestProject = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < projects.Length; index++)
        {
            foreach (var file in projects[index])
            {
                // 여러 프로젝트에 등록된 파일은 가장 작은 프로젝트를 소속으로 봅니다(공유 항목·생성 프로젝트 대비).
                if (!smallestProject.TryGetValue(file, out var known) || projects[index].Length < projects[known].Length) smallestProject[file] = index;
            }
        }

        this.engineRoots = engineRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>프로젝트·엔진 정보가 없을 때의 기본값입니다. 모든 파일이 같은 등급입니다.</summary>
    public static SourceAnalysisPriority None { get; } = new(Array.Empty<IEnumerable<string>>(), Array.Empty<string>());

    /// <summary>사용자가 아직 열지 않은 파일의 기본 등급입니다.</summary>
    public SourceAnalysisRank Rank(string path)
    {
        if (engineRoots.Any(root => ProjectSourceScope.IsInside(path, root))) return SourceAnalysisRank.Engine;
        return smallestProject.ContainsKey(path) ? SourceAnalysisRank.Project : SourceAnalysisRank.Other;
    }

    /// <summary>파일이 속한 프로젝트 중 관련 파일로 올릴 만큼 작은 프로젝트의 파일입니다. 없으면 null입니다.</summary>
    public IReadOnlyCollection<string>? RelatedProject(string path)
    {
        if (!smallestProject.TryGetValue(path, out var index)) return null;
        var members = projects[index];
        return members.Length <= MaxRelatedProjectFiles ? members : null;
    }
}

/// <summary>
/// 등급 순서로 분석할 파일을 내주는 대기열입니다. 분석 도중 사용자가 파일을 열면 그 파일과 관련 파일을 앞으로 옮깁니다.
/// 같은 등급 안에서는 넣은 순서를 지킵니다. 모든 멤버는 thread-safe입니다.
/// </summary>
public sealed class SourceAnalysisQueue
{
    private static readonly int RankCount = Enum.GetValues(typeof(SourceAnalysisRank)).Length;

    private readonly object gate = new();
    private readonly SourceAnalysisPriority priority;
    private readonly Queue<string>[] tiers;
    private readonly Dictionary<string, SourceAnalysisRank> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> byDirectory = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="focus">먼저 분석할 파일입니다. 앞에 있을수록 먼저 분석합니다(최근에 연 순서).</param>
    public SourceAnalysisQueue(IEnumerable<string> files, SourceAnalysisPriority priority, IEnumerable<string>? focus = null)
    {
        if (files is null) throw new ArgumentNullException(nameof(files));
        this.priority = priority ?? throw new ArgumentNullException(nameof(priority));
        tiers = Enumerable.Range(0, RankCount).Select(_ => new Queue<string>()).ToArray();
        foreach (var file in files)
        {
            if (pending.ContainsKey(file)) continue;
            var rank = priority.Rank(file);
            pending[file] = rank;
            tiers[(int)rank].Enqueue(file);
            var directory = Path.GetDirectoryName(file) ?? string.Empty;
            if (!byDirectory.TryGetValue(directory, out var siblings)) byDirectory[directory] = siblings = new List<string>();
            siblings.Add(file);
        }

        foreach (var path in focus ?? Array.Empty<string>()) Focus(path);
    }

    /// <summary>아직 내주지 않은 파일 수입니다.</summary>
    public int Count
    {
        get
        {
            lock (gate) return pending.Count;
        }
    }

    /// <summary>가장 앞선 등급의 다음 파일을 꺼냅니다. 남은 파일이 없으면 false입니다.</summary>
    public bool TryTake(out string path, out SourceAnalysisRank rank)
    {
        lock (gate)
        {
            for (var tier = 0; tier < tiers.Length; tier++)
            {
                var queue = tiers[tier];
                while (queue.Count > 0)
                {
                    var candidate = queue.Dequeue();
                    // 승격된 파일은 원래 등급 대기열에도 남아 있으므로, 현재 등급과 다르면 건너뜁니다.
                    if (!pending.TryGetValue(candidate, out var current) || (int)current != tier) continue;
                    pending.Remove(candidate);
                    path = candidate;
                    rank = current;
                    return true;
                }
            }
        }

        path = string.Empty;
        rank = SourceAnalysisRank.Engine;
        return false;
    }

    /// <summary>
    /// 사용자가 연 파일을 맨 앞으로, 같은 프로젝트의 파일을 그다음으로 옮깁니다. 프로젝트를 모르거나 너무 크면 같은 폴더의 파일을 옮깁니다.
    /// 이미 분석했거나 대기열에 없는 파일은 무시합니다.
    /// </summary>
    public void Focus(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (gate)
        {
            Promote(path, SourceAnalysisRank.Focus);
            IEnumerable<string>? related = priority.RelatedProject(path);
            if (related is null && byDirectory.TryGetValue(Path.GetDirectoryName(path) ?? string.Empty, out var siblings)) related = siblings;
            foreach (var file in related ?? Array.Empty<string>()) Promote(file, SourceAnalysisRank.Related);
        }
    }

    private void Promote(string path, SourceAnalysisRank rank)
    {
        if (!pending.TryGetValue(path, out var current) || current <= rank) return;
        pending[path] = rank;
        tiers[(int)rank].Enqueue(path);
    }
}
