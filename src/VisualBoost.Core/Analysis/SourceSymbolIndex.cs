using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using VisualBoost.Core.Searching;

namespace VisualBoost.Core.Analysis;

public sealed class SourceSymbolIndex : IDisposable
{
    // 부분 갱신으로 숨긴 위치와 추가 묶음이 이만큼(최소값 또는 전체의 1/8 중 큰 값)을 넘으면 한 번 다시 만듭니다. 숨긴 옛 위치는 다시 만들
    // 때까지 메모리에 남고, 조회는 묶음마다 따로 찾으므로 끝없이 쌓아 두지 않습니다.
    private const int CompactionMinimum = 50_000;
    private readonly ReaderWriterLockSlim gate = new();
    private Dictionary<string, SourceSymbolLocation[]> locationsByName =
        new(StringComparer.OrdinalIgnoreCase);
    private SymbolSearchSnapshot searchSnapshot = new(Array.Empty<SourceSymbolLocation>());
    private SymbolCompletionSnapshot baseCompletion = SymbolCompletionSnapshot.Empty;
    private SymbolCompletionSnapshot completionSnapshot = SymbolCompletionSnapshot.Empty;
    private readonly object appendGate = new();
    private Segment[] additions = Array.Empty<Segment>();
    // 부분 갱신에서 지운 위치입니다(참조로 비교). 공개한 뒤에는 고치지 않고 새 집합으로 바꿔 끼우므로 잠금 밖 검색에서도 읽을 수 있습니다.
    private HashSet<SourceSymbolLocation> hidden = EmptyHidden;
    private int baseCount;
    private int replacementVersion;
    private int revision;

    private static readonly HashSet<SourceSymbolLocation> EmptyHidden = new(ReferenceComparer.Instance);

    private sealed class Segment
    {
        public Segment(SourceSymbolLocation[] items, int level, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Items = items;
            Level = level;
            ByName = items.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            Snapshot = new SymbolSearchSnapshot(items);
            token.ThrowIfCancellationRequested();
        }
        public SourceSymbolLocation[] Items { get; }
        public int Level { get; }
        public Dictionary<string, SourceSymbolLocation[]> ByName { get; }
        public SymbolSearchSnapshot Snapshot { get; }
    }

    private sealed class ReferenceComparer : IEqualityComparer<SourceSymbolLocation>
    {
        public static readonly ReferenceComparer Instance = new();
        public bool Equals(SourceSymbolLocation? x, SourceSymbolLocation? y) => ReferenceEquals(x, y);
        public int GetHashCode(SourceSymbolLocation value) => RuntimeHelpers.GetHashCode(value);
    }

    public int Count { get; private set; }

    /// <summary>검색에 공개한 내용이 바뀔 때마다(전체 교체·추가 묶음·부분 갱신) 늘어나는 번호입니다. 잠금 없이 읽으며 수가 같은 재분석도 구별합니다.</summary>
    public int Revision => Volatile.Read(ref revision);

    public void ReplaceAll(IEnumerable<SourceSymbolLocation> locations, CancellationToken cancellationToken = default)
    {
        if (locations is null)
        {
            throw new ArgumentNullException(nameof(locations));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var replacement = locations.Select(location =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return location;
            })
            .GroupBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(location => location.Path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(location => location.Line)
                    .ThenBy(location => location.Column)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var count = replacement.Values.Sum(items => items.Length);
        var snapshot = new SymbolSearchSnapshot(replacement.Values.SelectMany(items => items));
        var completion = new SymbolCompletionSnapshot(replacement.Values.SelectMany(items => items));

        gate.EnterWriteLock();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            locationsByName = replacement;
            additions = Array.Empty<Segment>();
            hidden = EmptyHidden;
            replacementVersion++;
            Interlocked.Increment(ref revision);
            searchSnapshot = snapshot;
            baseCompletion = completion;
            Volatile.Write(ref completionSnapshot, completion);
            baseCount = count;
            Count = count;
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    /// <summary>최초 분석의 중복 없는 파일 묶음을 검색에 공개합니다. 완료 시 ReplaceAll로 압축합니다.</summary>
    public void AppendBatch(IEnumerable<SourceSymbolLocation> locations, CancellationToken token = default) =>
        Publish(locations.Select(location => { token.ThrowIfCancellationRequested(); return location; }).ToArray(),
            Array.Empty<SourceSymbolLocation>(), refreshCompletion: false, token);

    /// <summary>
    /// 몇 파일이 바뀌었을 때 전체를 다시 만들지 않고 고칩니다. <paramref name="removed"/>는 지금 공개된 위치 객체여야 하며(참조로 비교) 숨기고,
    /// <paramref name="added"/>는 추가 묶음으로 공개합니다. 숨긴 위치가 쌓이면 남은 위치로 다시 만듭니다.
    /// </summary>
    /// <remarks>
    /// 전체 교체는 엔진 규모(위치 약 400만)에서 10초 넘게 걸리고 옛 인덱스와 새 인덱스가 함께 있는 동안 메모리를 더 씁니다. 파일 하나를 저장할
    /// 때마다 그 비용을 치르지 않게 합니다. 입력 추천은 바뀐 파일의 이름을 앞세워 더하고, 지운 이름은 다시 만들 때까지 남깁니다.
    /// </remarks>
    public void Update(IReadOnlyCollection<SourceSymbolLocation> removed, IReadOnlyCollection<SourceSymbolLocation> added,
        CancellationToken token = default)
    {
        if (removed is null) throw new ArgumentNullException(nameof(removed));
        if (added is null) throw new ArgumentNullException(nameof(added));
        if (removed.Count == 0 && added.Count == 0) return;
        Publish(added.ToArray(), removed, refreshCompletion: true, token);
        bool compact;
        gate.EnterReadLock();
        try { compact = hidden.Count + additions.Sum(part => part.Items.Length) > Math.Max(CompactionMinimum, baseCount / 8); }
        finally { gate.ExitReadLock(); }
        if (compact) Compact(token);
    }

    private void Compact(CancellationToken token)
    {
        // 다시 만드는 동안 들어온 부분 갱신을 잃지 않게 추가 묶음 공개와 같은 잠금 안에서 바꿉니다.
        lock (appendGate)
        {
            SourceSymbolLocation[] visible;
            gate.EnterReadLock();
            try
            {
                var current = hidden;
                visible = locationsByName.Values.SelectMany(items => items).Concat(additions.SelectMany(part => part.Items))
                    .Where(location => !current.Contains(location)).ToArray();
            }
            finally { gate.ExitReadLock(); }
            ReplaceAll(visible, token);
        }
    }

    private void Publish(SourceSymbolLocation[] items, IReadOnlyCollection<SourceSymbolLocation> removed, bool refreshCompletion, CancellationToken token)
    {
        lock (appendGate)
        {
            Segment[] previous;
            int version;
            HashSet<SourceSymbolLocation> previousHidden;
            gate.EnterReadLock();
            try { previous = additions; version = replacementVersion; previousHidden = hidden; }
            finally { gate.ExitReadLock(); }
            var nextHidden = previousHidden;
            if (removed.Count > 0)
            {
                nextHidden = new HashSet<SourceSymbolLocation>(previousHidden, ReferenceComparer.Instance);
                nextHidden.UnionWith(removed);
            }
            var level = 0;
            var remaining = previous.Length;
            // 같은 크기의 묶음만 병합하므로 누적 재구성 비용은 O(N log N), 검색 묶음 수는 O(log N)입니다.
            while (remaining > 0 && previous[remaining - 1].Level == level)
            {
                token.ThrowIfCancellationRequested();
                items = previous[--remaining].Items.Concat(items).ToArray();
                level++;
            }
            if (level > 0 && nextHidden.Count > 0)
            {
                // 병합하는 묶음의 숨긴 위치는 여기서 버립니다. 그 위치는 이 묶음에만 있으므로 숨김 목록에서도 뺍니다.
                var dropped = items.Where(nextHidden.Contains).ToArray();
                if (dropped.Length > 0)
                {
                    items = items.Where(location => !nextHidden.Contains(location)).ToArray();
                    if (ReferenceEquals(nextHidden, previousHidden)) nextHidden = new HashSet<SourceSymbolLocation>(previousHidden, ReferenceComparer.Instance);
                    nextHidden.ExceptWith(dropped);
                }
            }
            var segment = new Segment(items, level, token);
            var replacement = previous.Take(remaining).Concat(new[] { segment }).ToArray();
            SymbolCompletionSnapshot? completion = null;
            if (refreshCompletion)
            {
                var completionHidden = nextHidden;
                SymbolCompletionSnapshot older;
                gate.EnterReadLock();
                try { older = baseCompletion; }
                finally { gate.ExitReadLock(); }
                completion = new SymbolCompletionSnapshot(older,
                    replacement.SelectMany(part => part.Items).Where(location => !completionHidden.Contains(location)));
            }
            gate.EnterWriteLock();
            try
            {
                token.ThrowIfCancellationRequested();
                if (replacementVersion != version) return;
                additions = replacement;
                hidden = nextHidden;
                Interlocked.Increment(ref revision);
                if (completion is not null) Volatile.Write(ref completionSnapshot, completion);
                Count = baseCount + additions.Sum(part => part.Items.Length) - hidden.Count;
            }
            finally { gate.ExitWriteLock(); }
        }
    }

    public IReadOnlyList<SourceSymbolLocation> Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Array.Empty<SourceSymbolLocation>();
        }

        gate.EnterReadLock();
        try
        {
            var found = locationsByName.TryGetValue(name, out var locations) ? locations : Array.Empty<SourceSymbolLocation>();
            if (additions.Length == 0 && hidden.Count == 0) return found;
            var current = hidden;
            return found.Concat(additions.SelectMany(part => part.ByName.TryGetValue(name, out var matches)
                    ? matches : Array.Empty<SourceSymbolLocation>()))
                .Where(location => !current.Contains(location))
                .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Line).ThenBy(item => item.Column).ToArray();
        }
        finally
        {
            gate.ExitReadLock();
        }
    }

    public IReadOnlyList<SourceSymbolMatch> Search(
        string query,
        int maximumResults = 100,
        CancellationToken cancellationToken = default,
        Func<string, bool>? includes = null)
    {
        SymbolSearchSnapshot snapshot;
        Segment[] segments;
        HashSet<SourceSymbolLocation> current;
        gate.EnterReadLock();
        try
        {
            snapshot = searchSnapshot;
            segments = additions;
            current = hidden;
        }
        finally
        {
            gate.ExitReadLock();
        }

        // 스냅샷과 숨김 목록은 교체 후 변경되지 않으므로 검색 중 쓰기 잠금을 유지할 필요가 없습니다.
        var excluded = current.Count == 0 ? null : current;
        var results = snapshot.Search(query, maximumResults, cancellationToken, includes, excluded);
        if (segments.Length == 0) return results;
        var merged = results.ToList();
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var match in segment.Snapshot.Search(query, maximumResults, cancellationToken, includes, excluded))
                FuzzySymbolSearch.Insert(merged, match, maximumResults);
        }
        return merged;
    }

    public void Dispose() => gate.Dispose();

    public SymbolCompletionSnapshot CompletionSnapshot => Volatile.Read(ref completionSnapshot);
}
