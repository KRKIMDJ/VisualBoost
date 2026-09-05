using System;
using System.Collections.Generic;
using System.Threading;

namespace VisualBoost.Core.Analysis;

// 검색 요청마다 하나씩 생성하고 백그라운드 작업 한 곳에서만 사용합니다.
public sealed class SymbolKindResolver
{
    private readonly Func<string, IReadOnlyList<SourceSymbolLocation>> find;
    private readonly Dictionary<string, SourceSymbolKind> cache = new(StringComparer.Ordinal);
    private readonly CancellationToken cancellationToken;

    public SymbolKindResolver(Func<string, IReadOnlyList<SourceSymbolLocation>> find,
        CancellationToken cancellationToken = default)
    {
        this.find = find ?? throw new ArgumentNullException(nameof(find));
        this.cancellationToken = cancellationToken;
    }

    public SourceSymbolKind Resolve(string name)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cache.TryGetValue(name, out var cached)) return cached;
        var kind = SourceSymbolKind.Unknown;
        var found = false;
        foreach (var candidate in find(name))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 파일 인덱스의 조회는 대소문자를 무시하지만 C++ 이름은 구분합니다.
            if (!string.Equals(candidate.Name, name, StringComparison.Ordinal)) continue;
            if (found && candidate.Kind != kind) { kind = SourceSymbolKind.Unknown; break; }
            kind = candidate.Kind;
            found = true;
        }
        cache[name] = kind;
        return kind;
    }
}
