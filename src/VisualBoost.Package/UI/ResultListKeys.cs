using System;
using System.Windows.Input;

namespace VisualBoost.UI;

/// <summary>
/// 검색 창들이 같은 키로 결과 목록을 움직이게 하는 규칙입니다. 초점이 검색란에 있어도 목록을 움직이므로
/// 검색란의 텍스트 편집 키(Home·End·좌우)는 건드리지 않고, 편집에 쓰지 않는 조합만 목록 이동에 씁니다.
/// </summary>
internal static class ResultListKeys
{
    /// <summary>하단 안내의 툴팁에 넣는 목록 이동 키 설명입니다. 안내 줄에는 자주 쓰는 키만 보입니다.</summary>
    public const string MoveKeysDescription = "↑↓ 한 줄 · PgUp/PgDn 한 화면 · Ctrl+Home/Ctrl+End 처음·끝";

    /// <summary>범위를 고를 수 있는 창의 범위 전환 키 설명입니다.</summary>
    public const string ScopeKeysDescription = "Ctrl+Tab/Ctrl+Shift+Tab 검색 범위 전환";

    /// <summary>
    /// 목록 이동 키이면 true와 새 선택 위치를 돌려줍니다. 결과가 없으면 위치는 -1이지만 키는 처리한 것으로 봅니다.
    /// ↑↓는 한 줄, PgUp/PgDn은 한 화면, Ctrl+Home/Ctrl+End는 처음·끝입니다. 끝에서 반대쪽으로 넘어가지 않습니다.
    /// </summary>
    public static bool TryMove(Key key, ModifierKeys modifiers, int current, int count, int pageSize, out int target)
    {
        var control = (modifiers & ModifierKeys.Control) != 0;
        int? offset = key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            Key.PageUp => -Math.Max(1, pageSize),
            Key.PageDown => Math.Max(1, pageSize),
            Key.Home when control => int.MinValue,
            Key.End when control => int.MaxValue,
            _ => null,
        };
        target = -1;
        if (offset is null) return false;
        if (count <= 0) return true;

        // 아무것도 고르지 않은 상태에서는 아래로 가는 키가 첫 항목을 고릅니다.
        var start = current < 0 ? (offset.Value > 0 ? -1 : 0) : current;
        var next = (long)start + offset.Value;
        target = (int)Math.Max(0, Math.Min(count - 1, next));
        return true;
    }

    /// <summary>Ctrl+Tab은 다음, Ctrl+Shift+Tab은 이전 검색 범위로 넘깁니다.</summary>
    public static bool IsScopeCycle(Key key, ModifierKeys modifiers, out bool forward)
    {
        forward = (modifiers & ModifierKeys.Shift) == 0;
        return key == Key.Tab && (modifiers & ModifierKeys.Control) != 0 && (modifiers & ModifierKeys.Alt) == 0;
    }

    /// <summary>사용할 수 있는 다음 범위의 위치입니다. 끝에서 처음으로 돌아가며, 고를 수 있는 다른 범위가 없으면 현재 위치입니다.</summary>
    public static int NextScope(int current, int count, bool forward, Func<int, bool> isAvailable)
    {
        if (count <= 0) return -1;
        var step = forward ? 1 : -1;
        var index = current < 0 || current >= count ? (forward ? -1 : count) : current;
        for (var tried = 0; tried < count; tried++)
        {
            index = ((index + step) % count + count) % count;
            if (isAvailable(index)) return index;
        }

        return current;
    }

    /// <summary>보이는 행 수에서 한 줄을 겹친 쪽 이동 크기입니다.</summary>
    public static int PageSize(double viewportHeight, double rowHeight) =>
        rowHeight <= 0 || double.IsNaN(viewportHeight) || viewportHeight <= rowHeight
            ? 1
            : Math.Max(1, (int)(viewportHeight / rowHeight) - 1);
}
