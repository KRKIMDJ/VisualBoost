using System;
using System.Collections.Generic;
using System.Linq;
using VisualBoost.Core.Coloring;
using VisualBoost.Services;

namespace VisualBoost.UI;

/// <summary>
/// 결과 목록 코드 미리보기의 식별자 이름 종류를 Solution 이름 인덱스에서 찾습니다(<see cref="CodePreviewStyle.NameKind"/>).
/// 미리보기 색 보강 전용입니다. 탐색 결과나 일치 구간의 심볼 종류 판정에는 쓰지 않습니다. 이름 인덱스는 대소문자를 무시해 찾으므로
/// 이름이 정확히 같은 심볼만 셉니다. 보이는 행을 처음 그릴 때 UI thread에서 부르며, 이름별 답을 캐시하고 인덱스 심볼 수가 바뀌면 비웁니다.
/// </summary>
internal sealed class CodePreviewNames
{
    // 캐시가 이만큼 차면 비웁니다. 보이는 행의 이름은 수백 개 안쪽이라 실사용에서는 인덱스가 바뀔 때만 비워집니다.
    private const int CacheLimit = 4096;

    // 인덱스 심볼 수 확인 간격입니다. 수를 읽으려면 인덱스 상태 잠금을 거치므로 이름마다 확인하지 않습니다.
    private const int CountCheckInterval = 1000;

    private readonly SolutionFileIndexService index;
    private readonly Dictionary<string, CodePreviewKind?> cache = new(StringComparer.Ordinal);
    private int symbolCount = -1;
    private int checkedAt;

    public CodePreviewNames(SolutionFileIndexService index) => this.index = index ?? throw new ArgumentNullException(nameof(index));

    public CodePreviewKind? Resolve(string name)
    {
        try
        {
            RefreshCache();
            if (cache.TryGetValue(name, out var known)) return known;
            if (cache.Count >= CacheLimit) cache.Clear();
            var kind = CppLinePreviewClassifier.KindOfSymbols(index.FindSymbol(name).Where(symbol => symbol.Name == name).Select(symbol => symbol.Kind));
            cache[name] = kind;
            return kind;
        }
        catch (ObjectDisposedException)
        {
            // VS 종료 중 패키지가 인덱스를 정리한 뒤 남은 창이 다시 그려질 수 있습니다. 색 보강 없이 형태 추정만 씁니다.
            return null;
        }
    }

    private void RefreshCache()
    {
        var now = Environment.TickCount;
        if (symbolCount >= 0 && unchecked(now - checkedAt) < CountCheckInterval) return;
        checkedAt = now;
        var count = index.GetSnapshot().SymbolCount;
        if (count == symbolCount) return;
        symbolCount = count;
        cache.Clear();
    }
}
