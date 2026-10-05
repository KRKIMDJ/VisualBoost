using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisualBoost.UI;

/// <summary>검색 창 공통 조작: 목록 이동 키, 범위 전환 키, 창 위치 복원 규칙과 XAML의 안내·우클릭 메뉴.</summary>
internal static class SearchKeyboardTests
{
    public static void Run(string root)
    {
        Move(Key.Down, ModifierKeys.None, 3, 10, 5, 4, "↓ 한 줄");
        Move(Key.Up, ModifierKeys.None, 0, 10, 5, 0, "처음에서 ↑는 넘어가지 않음");
        Move(Key.Down, ModifierKeys.None, 9, 10, 5, 9, "끝에서 ↓는 넘어가지 않음");
        Move(Key.Down, ModifierKeys.None, -1, 10, 5, 0, "선택 없음에서 ↓는 첫 항목");
        Move(Key.PageDown, ModifierKeys.None, 2, 10, 5, 7, "PgDn 한 화면");
        Move(Key.PageDown, ModifierKeys.None, 8, 10, 5, 9, "PgDn은 끝에서 멈춤");
        Move(Key.PageUp, ModifierKeys.None, 3, 10, 5, 0, "PgUp은 처음에서 멈춤");
        Move(Key.PageDown, ModifierKeys.None, 0, 10, 0, 1, "쪽 크기 0은 한 줄");
        Move(Key.End, ModifierKeys.Control, 2, 10, 5, 9, "Ctrl+End 끝");
        Move(Key.Home, ModifierKeys.Control, 7, 10, 5, 0, "Ctrl+Home 처음");
        Check(!ResultListKeys.TryMove(Key.Home, ModifierKeys.None, 3, 10, 5, out _), "Home은 검색란 편집에 남김");
        Check(!ResultListKeys.TryMove(Key.End, ModifierKeys.Shift, 3, 10, 5, out _), "Shift+End는 검색란 선택에 남김");
        Check(!ResultListKeys.TryMove(Key.Left, ModifierKeys.None, 3, 10, 5, out _), "좌우는 검색란 편집에 남김");
        Check(ResultListKeys.TryMove(Key.Down, ModifierKeys.None, -1, 0, 5, out var none) && none == -1, "결과가 없어도 이동 키는 처리");

        Check(ResultListKeys.IsScopeCycle(Key.Tab, ModifierKeys.Control, out var forward) && forward, "Ctrl+Tab 다음 범위");
        Check(ResultListKeys.IsScopeCycle(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, out forward) && !forward, "Ctrl+Shift+Tab 이전 범위");
        Check(!ResultListKeys.IsScopeCycle(Key.Tab, ModifierKeys.None, out _), "Tab은 초점 이동에 남김");
        Check(!ResultListKeys.IsScopeCycle(Key.Tab, ModifierKeys.Control | ModifierKeys.Alt, out _), "Ctrl+Alt+Tab 제외");
        var enabled = new[] { true, false, true, true };
        Check(ResultListKeys.NextScope(0, 4, true, i => enabled[i]) == 2, "쓸 수 없는 범위 건너뜀");
        Check(ResultListKeys.NextScope(3, 4, true, i => enabled[i]) == 0, "끝에서 처음으로 순환");
        Check(ResultListKeys.NextScope(0, 4, false, i => enabled[i]) == 3, "처음에서 이전은 끝");
        Check(ResultListKeys.NextScope(-1, 4, true, i => enabled[i]) == 0, "선택 없음에서 다음은 첫 범위");
        Check(ResultListKeys.NextScope(0, 4, true, i => i == 0) == 0, "다른 범위가 없으면 그대로");
        Check(ResultListKeys.NextScope(0, 0, true, _ => true) == -1, "범위 없음");
        Check(ResultListKeys.PageSize(240, 24) == 9 && ResultListKeys.PageSize(10, 24) == 1 && ResultListKeys.PageSize(double.NaN, 24) == 1, "쪽 크기");

        var screen = new Rect(0, 0, 1920, 1080);
        var minimum = new Size(720, 380);
        var plan = WindowPlacement.Resolve(new Rect(100, 50, 1000, 600), minimum, screen);
        Check(plan.Width == 1000 && plan.Height == 600 && plan.Position == new Point(100, 50), "저장한 크기·위치 복원");
        plan = WindowPlacement.Resolve(new Rect(-5000, 50, 1000, 600), minimum, screen);
        Check(plan.Width == 1000 && plan.Position is null, "분리한 모니터 위치는 버리고 크기만 유지");
        plan = WindowPlacement.Resolve(new Rect(100, 50, 300, 600), minimum, screen);
        Check(plan.Width is null && plan.Height == 600 && plan.Position == new Point(100, 50), "최소보다 작은 너비는 기본값");
        plan = WindowPlacement.Resolve(new Rect(double.NaN, double.NaN, 1000, 600), minimum, screen);
        Check(plan.Width == 1000 && plan.Position is null, "위치를 저장하지 않았으면 가운데에 열기");
        plan = WindowPlacement.Resolve(null, minimum, screen);
        Check(plan.Width is null && plan.Height is null && plan.Position is null, "저장값 없음");

        var symbol = Program.LoadXaml(root, "SymbolSearchDialog");
        var list = (ListView)symbol.FindName("ResultsList");
        var menu = list.ContextMenu?.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
        Check(menu is not null && menu.SequenceEqual(new[] { "열기", "이름 복사", "전체 경로 복사", "탐색기에서 보기" }), "심볼 결과 우클릭 메뉴");
        Check(((TextBlock)symbol.FindName("KeyboardHintText")).Text.Contains("Ctrl+Tab"), "심볼 탐색 범위 전환 안내");
        var file = Program.LoadXaml(root, "FileSearchDialog");
        Check(((TextBlock)file.FindName("KeyboardHintText")).Text.Contains("Ctrl+Tab"), "파일 탐색 범위 전환 안내");
        Check(((TextBlock)file.FindName("SearchPlaceholder")).Text.Contains(":줄"), "파일 탐색 줄 이동 안내");
        Console.WriteLine("PASS: 검색 창 목록 이동·범위 전환 키, 창 위치 복원 규칙, 심볼 우클릭 메뉴와 안내");
    }

    private static void Move(Key key, ModifierKeys modifiers, int current, int count, int page, int expected, string message)
    {
        Check(ResultListKeys.TryMove(key, modifiers, current, count, page, out var target) && target == expected, $"{message}: {target}");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
