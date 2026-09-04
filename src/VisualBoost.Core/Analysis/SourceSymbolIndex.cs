using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VisualBoost.Core.Analysis;

public sealed class SourceSymbolIndex : IDisposable
{
    private readonly ReaderWriterLockSlim gate = new();
    private Dictionary<string, SourceSymbolLocation[]> locationsByName =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count { get; private set; }

    public void ReplaceAll(IEnumerable<SourceSymbolLocation> locations)
    {
        if (locations is null)
        {
            throw new ArgumentNullException(nameof(locations));
        }

        var replacement = locations
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

        gate.EnterWriteLock();
        try
        {
            locationsByName = replacement;
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

    public void Dispose() => gate.Dispose();
}
