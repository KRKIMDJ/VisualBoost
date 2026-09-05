using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VisualBoost.Core.Analysis;

public sealed class SymbolCompletionSnapshot
{
    public static readonly SymbolCompletionSnapshot Empty = new(Array.Empty<SourceSymbolLocation>());
    private readonly SourceSymbolLocation[] entries;

    public SymbolCompletionSnapshot(IEnumerable<SourceSymbolLocation> symbols)
    {
        // 지역 변수는 접근 가능 범위를 알 수 없으므로 전역 입력 추천에서 제외합니다.
        entries = symbols.Where(s => s.Kind != SourceSymbolKind.Variable && s.Name.Length > 0)
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Select(g => g.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Line).First())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<SourceSymbolLocation> Find(string prefix, int limit = 30, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3 || limit <= 0) return Array.Empty<SourceSymbolLocation>();
        limit = Math.Min(limit, 50);
        var low = 0;
        var high = entries.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (StringComparer.OrdinalIgnoreCase.Compare(entries[mid].Name, prefix) < 0) low = mid + 1;
            else high = mid;
        }
        var result = new List<SourceSymbolLocation>();
        // 이름 수가 수백만 개여도 입력 경로는 이진 탐색과 최대 256개 후보만 확인합니다.
        for (var i = low; i < entries.Length && i - low < 256; i++)
        {
            token.ThrowIfCancellationRequested();
            var item = entries[i];
            if (!item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) break;
            if (item.Name != prefix) result.Add(item);
        }
        return result.OrderByDescending(s => s.Name.StartsWith(prefix, StringComparison.Ordinal)).ThenBy(s => s.Name.Length)
            .ThenBy(s => s.Name, StringComparer.Ordinal).Take(limit).ToArray();
    }
}
