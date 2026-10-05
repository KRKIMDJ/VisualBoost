using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Searching;

namespace VisualBoost.Core.Analysis;

public sealed class SourceSymbolIndex : IDisposable
{
    private readonly ReaderWriterLockSlim gate = new();
    private Dictionary<string, SourceSymbolLocation[]> locationsByName =
        new(StringComparer.OrdinalIgnoreCase);
    private SymbolSearchSnapshot searchSnapshot = new(Array.Empty<SourceSymbolLocation>());
    private SymbolCompletionSnapshot completionSnapshot = SymbolCompletionSnapshot.Empty;
    private readonly object appendGate = new();
    private Segment[] additions = Array.Empty<Segment>();
    private int replacementVersion;

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

    public int Count { get; private set; }

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
            replacementVersion++;
            searchSnapshot = snapshot;
            Volatile.Write(ref completionSnapshot, completion);
            Count = count;
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    /// <summary>최초 분석의 중복 없는 파일 묶음을 검색에 공개합니다. 완료 시 ReplaceAll로 압축합니다.</summary>
    public void AppendBatch(IEnumerable<SourceSymbolLocation> locations, CancellationToken token = default)
    {
        lock (appendGate)
        {
            Segment[] previous;
            int version;
            gate.EnterReadLock();
            try { previous = additions; version = replacementVersion; }
            finally { gate.ExitReadLock(); }
            var items = locations.Select(location => { token.ThrowIfCancellationRequested(); return location; }).ToArray();
            var level = 0;
            var remaining = previous.Length;
            // 같은 크기의 묶음만 병합하므로 누적 재구성 비용은 O(N log N), 검색 묶음 수는 O(log N)입니다.
            while (remaining > 0 && previous[remaining - 1].Level == level)
            {
                token.ThrowIfCancellationRequested();
                items = previous[--remaining].Items.Concat(items).ToArray();
                level++;
            }
            var segment = new Segment(items, level, token);
            var replacement = previous.Take(remaining).Concat(new[] { segment }).ToArray();
            gate.EnterWriteLock();
            try
            {
                token.ThrowIfCancellationRequested();
                if (replacementVersion != version) return;
                additions = replacement;
                Count = locationsByName.Values.Sum(values => values.Length) + additions.Sum(part => part.Items.Length);
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
            if (additions.Length == 0) return found;
            return found.Concat(additions.SelectMany(part => part.ByName.TryGetValue(name, out var matches)
                    ? matches : Array.Empty<SourceSymbolLocation>()))
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
        gate.EnterReadLock();
        try
        {
            snapshot = searchSnapshot;
            segments = additions;
        }
        finally
        {
            gate.ExitReadLock();
        }

        // 스냅샷은 교체 후 변경되지 않으므로 검색 중 쓰기 잠금을 유지할 필요가 없습니다.
        var results = snapshot.Search(query, maximumResults, cancellationToken, includes);
        if (segments.Length == 0) return results;
        var merged = results.ToList();
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var match in segment.Snapshot.Search(query, maximumResults, cancellationToken, includes))
                FuzzySymbolSearch.Insert(merged, match, maximumResults);
        }
        return merged;
    }

    public void Dispose() => gate.Dispose();

    public SymbolCompletionSnapshot CompletionSnapshot => Volatile.Read(ref completionSnapshot);
}
