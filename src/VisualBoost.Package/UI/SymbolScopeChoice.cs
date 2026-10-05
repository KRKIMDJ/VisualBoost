using System;
using System.Collections.Generic;
using System.Linq;
using VisualBoost.Core.Indexing;

namespace VisualBoost.UI;

/// <summary>
/// 심볼 탐색 창 하나의 검색 범위 선택 규칙입니다. 두 값을 구분합니다.
/// <list type="bullet">
/// <item>이 창이 보이려는 범위(<see cref="PreferredId"/>): 기억한 범위가 아직 게시되지 않았으면 화면은 '전체'이고 게시되면 그 범위로 바꿉니다.
/// 사용자가 입력·이동을 시작하거나 소속 목록이 게시되었는데도 없으면 지금 범위로 확정해 저절로 바뀌지 않게 합니다.</item>
/// <item>다음 열기에 이어 쓸 범위(<see cref="NextId"/>): 사용자가 범위를 직접 바꿨을 때만 갱신합니다. 이번 창의 확정은 기억을 지우지 않습니다.</item>
/// </list>
/// UI thread에서만 씁니다.
/// </summary>
internal sealed class SymbolScopeChoice
{
    public SymbolScopeChoice(string? rememberedId, string? rememberedName)
    {
        var remembered = !string.IsNullOrEmpty(rememberedId);
        PreferredId = NextId = remembered ? rememberedId! : SymbolSearchScope.All.Id;
        PreferredName = NextName = remembered ? rememberedName ?? string.Empty : SymbolSearchScope.All.Name;
    }

    public string PreferredId { get; private set; }

    public string PreferredName { get; private set; }

    public string NextId { get; private set; }

    public string NextName { get; private set; }

    /// <summary>
    /// 게시된 범위 목록에서 보일 범위를 고릅니다. 바라던 범위가 없으면 '전체'입니다. 소속 목록이 게시되어 '전체' 말고도
    /// 범위가 있는데 바라던 범위가 없으면(프로젝트 언로드·제거) 더 기다리지 않고 이번 창은 '전체'로 확정합니다.
    /// </summary>
    public SymbolSearchScope Choose(IReadOnlyList<SymbolSearchScope> scopes)
    {
        if (scopes is null) throw new ArgumentNullException(nameof(scopes));
        if (scopes.FirstOrDefault(scope => scope.Id == PreferredId) is { } preferred) return preferred;
        var all = scopes.FirstOrDefault(scope => scope.Id == SymbolSearchScope.All.Id) ?? SymbolSearchScope.All;
        if (scopes.Count > 1) Settle(all);
        return all;
    }

    /// <summary>기억한 범위를 기다리느라 다른 범위를 보이는 중인지입니다.</summary>
    public bool IsPending(SymbolSearchScope shown) => shown.Id != PreferredId;

    /// <summary>사용자가 범위를 직접 골랐습니다. 이 창과 다음 열기 모두 이 범위를 씁니다.</summary>
    public void UserSelected(SymbolSearchScope scope)
    {
        PreferredId = NextId = scope.Id;
        PreferredName = NextName = scope.Name;
    }

    /// <summary>지금 보이는 범위로 이 창을 확정합니다. 기다리던 중이었으면 true입니다. 다음 열기의 기억은 바꾸지 않습니다.</summary>
    public bool Settle(SymbolSearchScope shown)
    {
        if (!IsPending(shown)) return false;
        PreferredId = shown.Id;
        PreferredName = shown.Name;
        return true;
    }

    /// <summary>상태 줄의 범위 이름 뒤에 붙이는 안내입니다. 기다리는 중이 아니면 빈 문자열입니다.</summary>
    public string PendingNote(SymbolSearchScope shown) =>
        IsPending(shown) ? $" · 마지막 범위 '{PreferredName}' 준비 중" : string.Empty;
}
