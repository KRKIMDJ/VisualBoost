using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VisualBoost.Core.Analysis;

public sealed class SymbolCompletionSnapshot
{
    public static readonly SymbolCompletionSnapshot Empty = new(Array.Empty<SourceSymbolLocation>());
    private readonly SourceSymbolLocation[] entries;
    // 부분 갱신으로 바뀐 파일의 이름입니다. 같은 이름이면 entries보다 앞세웁니다.
    private readonly SymbolCompletionSnapshot? newer;

    public SymbolCompletionSnapshot(IEnumerable<SourceSymbolLocation> symbols)
    {
        // 지역 변수는 접근 가능 범위를 알 수 없으므로 전역 입력 추천에서 제외합니다.
        entries = symbols.Where(s => s.Kind != SourceSymbolKind.Variable && s.Name.Length > 0)
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Select(g => g.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Line).First())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// <paramref name="older"/>에 바뀐 파일의 이름을 더합니다. 지운 이름은 빼지 않습니다(같은 이름이 다른 파일에 남았는지 여기서는 모름). 다음 전체
    /// 재구성에서 정리됩니다.
    /// </summary>
    internal SymbolCompletionSnapshot(SymbolCompletionSnapshot older, IEnumerable<SourceSymbolLocation> newer)
    {
        entries = older.entries;
        this.newer = new SymbolCompletionSnapshot(newer);
    }

    public IReadOnlyList<SourceSymbolLocation> Find(string prefix, int limit = 30, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3 || limit <= 0) return Array.Empty<SourceSymbolLocation>();
        limit = Math.Min(limit, 50);
        IEnumerable<SourceSymbolLocation> result = Candidates(prefix, token);
        if (newer is not null)
        {
            var fresh = newer.Candidates(prefix, token);
            var names = new HashSet<string>(fresh.Select(s => s.Name), StringComparer.Ordinal);
            result = fresh.Concat(result.Where(s => !names.Contains(s.Name)));
        }
        return result.OrderByDescending(s => s.Name.StartsWith(prefix, StringComparison.Ordinal)).ThenBy(s => s.Name.Length)
            .ThenBy(s => s.Name, StringComparer.Ordinal).Take(limit).ToArray();
    }

    private List<SourceSymbolLocation> Candidates(string prefix, CancellationToken token)
    {
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
        return result;
    }
}
