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
            searchSnapshot = snapshot;
            Volatile.Write(ref completionSnapshot, completion);
            Count = count;
        }
        finally
        {
            gate.ExitWriteLock();
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
            return locationsByName.TryGetValue(name, out var locations)
                ? locations
                : Array.Empty<SourceSymbolLocation>();
        }
        finally
        {
            gate.ExitReadLock();
        }
    }

    public IReadOnlyList<SourceSymbolMatch> Search(
        string query,
        int maximumResults = 100,
        CancellationToken cancellationToken = default)
    {
        SymbolSearchSnapshot snapshot;
        gate.EnterReadLock();
        try
        {
            snapshot = searchSnapshot;
        }
        finally
        {
            gate.ExitReadLock();
        }

        // 스냅샷은 교체 후 변경되지 않으므로 검색 중 쓰기 잠금을 유지할 필요가 없습니다.
        return snapshot.Search(query, maximumResults, cancellationToken);
    }

    public void Dispose() => gate.Dispose();

    public SymbolCompletionSnapshot CompletionSnapshot => Volatile.Read(ref completionSnapshot);
}
