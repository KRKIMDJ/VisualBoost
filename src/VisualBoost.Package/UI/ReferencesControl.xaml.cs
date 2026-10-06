using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VisualBoost.Core.Indexing;
using VisualBoost.Core.SemanticNavigation;

namespace VisualBoost.UI;

/// <summary>
/// 도킹 참조 창의 내용입니다. 결과를 파일별로 묶어 보이고, 키보드만으로 훑어보고 열 수 있게 합니다.
/// 표시 상태는 <see cref="ReferenceResultsModel"/>이 갖고 이 컨트롤은 화면 동기화와 입력만 맡습니다. UI thread에서만 씁니다.
/// </summary>
public partial class ReferencesControl : UserControl
{
    private readonly ReferenceResultsModel model = new();

    // 코드에서 필터·범위·최근 결과 선택을 맞추는 동안 생기는 변경 이벤트를 무시합니다.
    private bool syncing;

    // 우클릭 메뉴를 연 행입니다. 빈 곳에서 열면 null이며, 선택은 그대로 둡니다.
    private object? menuRow;

    public ReferencesControl()
    {
        // XAML을 읽는 동안 생기는 변경 이벤트는 아직 만들지 않은 요소를 건드리므로 무시합니다. 첫 Refresh가 끝나면 풀립니다.
        syncing = true;
        InitializeComponent();
        HintText.ToolTip = "Ctrl+Tab/Ctrl+Shift+Tab 현재 프로젝트·모든 프로젝트 전환\nShift+F10 또는 우클릭: 위치·코드 복사, 탐색기에서 보기, 전부 펼치기·접기";
        // 결과를 보인 뒤에 게시된 프로젝트 소속도 쓰도록 이 창에 초점이 올 때 소속 목록을 다시 읽습니다.
        IsKeyboardFocusWithinChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewValue is true && RefreshProjects()) Refresh(SelectionKey(), keepScroll: true);
        };
        Refresh(null, keepScroll: false);
    }

    /// <summary>위치를 엽니다. 두 번째 인수가 true이면 편집기로 초점을 옮기고, false이면 이 창에 초점을 남깁니다.</summary>
    internal Action<NavigationLocation, bool>? OpenLocation { get; set; }

    /// <summary>
    /// 현재 Solution의 범위 목록(프로젝트 소속)을 읽습니다. '현재 프로젝트' 판정과 가까운 순서에 쓰며, 새 결과·모드 전환·창 초점 때마다 다시 읽어
    /// 늦게 게시된 목록을 반영합니다.
    /// </summary>
    internal Func<IReadOnlyList<SymbolSearchScope>>? ScopeSource { get; set; }

    /// <summary>복사·탐색기 결과를 알립니다. 두 번째 인수가 true이면 실패입니다.</summary>
    internal Action<string, bool>? Notify { get; set; }

    internal ReferenceResultsModel Model => model;

    /// <summary>새 결과를 보이고 첫 위치를 고른 뒤 목록에 초점을 둡니다. 범위 모드는 유지합니다.</summary>
    [SuppressMessage("Usage", "VSTHRD001", Justification = "창을 처음 만들 때는 배치가 끝난 뒤에야 행 컨테이너가 생기므로 같은 UI thread에서 초점 이동만 미룹니다.")]
    internal void Show(ReferenceResultSet set)
    {
        RefreshProjects();
        model.Show(set);
        Refresh(null, keepScroll: false);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FocusSelection));
    }

    /// <summary>모든 결과와 Solution의 소속 목록을 지웁니다. 닫은 Solution의 위치를 다시 열지 않게 합니다. 범위 모드는 유지합니다.</summary>
    internal void Clear()
    {
        model.Clear();
        Refresh(null, keepScroll: false);
    }

    /// <summary>
    /// 현재 프로젝트·모든 프로젝트 모드를 서로 바꿉니다(모드가 둘이라 방향은 같습니다). 결과가 없으면 false를 돌려 키를 다른 처리에 넘깁니다.
    /// VS가 Ctrl+Tab을 창 전환에 먼저 쓰므로 도구 창의 키 전처리에서도 부르며, 그 경로는 목록 열림 가드를 거치지 않으므로 여기서 닫습니다.
    /// </summary>
    internal bool CycleScope(bool forward)
    {
        _ = forward;
        if (model.Current is null) return false;
        HistoryBox.IsDropDownOpen = false;
        SetMode(model.Mode == ReferenceScopeMode.CurrentProject ? ReferenceScopeMode.AllProjects : ReferenceScopeMode.CurrentProject);
        if (!FilterBox.IsKeyboardFocusWithin) FocusSelection();
        return true;
    }

    /// <summary>선택한 행에 키보드 초점을 둡니다. 선택이 없으면 목록에 둡니다.</summary>
    internal void FocusSelection()
    {
        var row = ResultsList.SelectedItem;
        if (row is null)
        {
            ResultsList.Focus();
            return;
        }

        ResultsList.ScrollIntoView(row);
        ResultsList.UpdateLayout();
        if (ResultsList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem container) container.Focus();
        else ResultsList.Focus();
    }

    /// <summary>소속 목록을 다시 읽습니다. 목록이 바뀌어 순서·현재 프로젝트를 다시 정했으면 true입니다.</summary>
    private bool RefreshProjects() => ScopeSource?.Invoke() is { } scopes && model.SetProjects(scopes);

    private void SetMode(ReferenceScopeMode mode)
    {
        var keep = SelectionKey();
        RefreshProjects();
        model.SetMode(mode);
        Refresh(keep, keepScroll: false);
    }

    /// <summary>모델 상태를 화면에 옮깁니다. <paramref name="keep"/>는 다시 고를 위치(<see cref="NavigationResultItem"/>)나 파일 경로입니다.</summary>
    private void Refresh(object? keep, bool keepScroll)
    {
        syncing = true;
        try
        {
            var current = model.Current;
            SymbolText.Text = current?.Symbol ?? string.Empty;
            SymbolText.ToolTip = current?.Symbol;
            SummaryText.Text = model.Summary;
            SummaryText.ToolTip = model.Summary.Length == 0 ? null : model.Summary;
            if (FilterBox.Text.Trim() != model.Filter) FilterBox.Text = model.Filter;
            FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            FilterBox.IsEnabled = current is not null;
            CurrentProjectButton.IsChecked = model.Mode == ReferenceScopeMode.CurrentProject;
            AllProjectsButton.IsChecked = model.Mode == ReferenceScopeMode.AllProjects;
            CurrentProjectButton.IsEnabled = AllProjectsButton.IsEnabled = current is not null;
            CurrentProjectButton.ToolTip = model.CurrentProjectToolTip;
            HistoryBox.ItemsSource = model.History.ToArray();
            HistoryBox.SelectedItem = current;
            HistoryBox.IsEnabled = model.History.Count > 0;

            // 행 목록을 바꾸면 스크롤이 맨 위로 돌아가므로, 접기처럼 같은 결과 안의 변경은 보던 위치를 지킵니다.
            var scroll = keepScroll ? ScrollViewerOf(ResultsList) : null;
            var offset = scroll?.VerticalOffset ?? 0;
            ResultsList.ItemsSource = model.Rows;
            if (scroll is not null)
            {
                ResultsList.UpdateLayout();
                scroll.ScrollToVerticalOffset(offset);
            }

            Select(keep);
            EmptyText.Text = model.EmptyMessage;
            EmptyText.Visibility = model.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            syncing = false;
        }
    }

    /// <summary>지정한 위치나 파일의 행을 고릅니다. 접혀서 없으면 그 파일의 머리 행을, 그것도 없으면 첫 위치를 고릅니다.</summary>
    private void Select(object? keep)
    {
        var rows = model.Rows;
        object? row = keep switch
        {
            NavigationResultItem item => rows.OfType<ReferenceLineRow>().FirstOrDefault(r => ReferenceEquals(r.Item, item)) ??
                                         (object?)rows.OfType<ReferenceFileRow>().FirstOrDefault(r => SamePath(r.FullPath, item.FullPath)),
            string path => rows.OfType<ReferenceFileRow>().FirstOrDefault(r => SamePath(r.FullPath, path)) ??
                           (object?)rows.OfType<ReferenceLineRow>().FirstOrDefault(r => SamePath(r.FullPath, path)),
            _ => null,
        };
        row ??= rows.FirstOrDefault(r => r is ReferenceLineRow) ?? rows.FirstOrDefault();
        ResultsList.SelectedItem = row;
        if (row is not null) ResultsList.ScrollIntoView(row);
    }

    private object? SelectionKey() => ResultsList.SelectedItem switch
    {
        ReferenceLineRow line => line.Item,
        ReferenceFileRow file => file.FullPath,
        _ => null,
    };

    private void OnFilterTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        FilterPlaceholder.Visibility = string.IsNullOrEmpty(FilterBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (syncing) return;
        var keep = SelectionKey();
        model.Filter = FilterBox.Text;
        Refresh(keep, keepScroll: false);
    }

    private void OnModeChecked(object sender, RoutedEventArgs eventArgs)
    {
        // 코드에서 고른 표시를 맞출 때(Refresh)는 무시하고, 클릭·키보드·접근성 선택만 모드로 반영합니다.
        if (syncing) return;
        SetMode(ReferenceEquals(sender, CurrentProjectButton) ? ReferenceScopeMode.CurrentProject : ReferenceScopeMode.AllProjects);
    }

    private void SetAllExpanded(bool expanded)
    {
        var keep = SelectionKey();
        model.SetAllExpanded(expanded);
        Refresh(keep, keepScroll: false);
    }

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (syncing || HistoryBox.SelectedItem is not ReferenceResultSet set || ReferenceEquals(set, model.Current)) return;
        // 다른 심볼의 결과로 돌아가므로 이전 필터는 지웁니다. 범위 모드는 유지하고 현재 프로젝트는 그 결과의 파일 기준입니다.
        RefreshProjects();
        model.Select(set, clearFilter: true);
        Refresh(null, keepScroll: false);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        // 최근 결과 목록이 열려 있으면 화살표·Enter는 목록 항목 선택에 씁니다.
        if (HistoryBox.IsDropDownOpen) return;
        if (ResultListKeys.IsScopeCycle(eventArgs.Key, Keyboard.Modifiers, out var forward))
        {
            if (CycleScope(forward)) eventArgs.Handled = true;
            return;
        }

        if (FilterBox.IsKeyboardFocusWithin)
        {
            switch (eventArgs.Key)
            {
                case Key.Down:
                case Key.Enter:
                    if (model.Rows.Count == 0) return;
                    if (ResultsList.SelectedItem is null) Select(null);
                    FocusSelection();
                    break;
                case Key.Escape when FilterBox.Text.Length > 0:
                    FilterBox.Clear();
                    break;
                default:
                    return;
            }

            eventArgs.Handled = true;
            return;
        }

        if (!ResultsList.IsKeyboardFocusWithin) return;
        var selected = ResultsList.SelectedItem;
        switch (eventArgs.Key)
        {
            case Key.Enter:
                Activate(selected, keepFocus: false);
                break;
            case Key.Space:
                Activate(selected, keepFocus: true);
                break;
            case Key.Left when selected is ReferenceFileRow { IsExpanded: true } file:
                SetExpanded(file.FullPath, false);
                break;
            case Key.Left when selected is ReferenceLineRow line:
                // 위치 행에서 왼쪽은 그 파일의 머리 행으로 올라갑니다.
                ResultsList.SelectedItem = model.Rows.OfType<ReferenceFileRow>().FirstOrDefault(r => SamePath(r.FullPath, line.FullPath)) ?? selected;
                FocusSelection();
                break;
            case Key.Right when selected is ReferenceFileRow { IsExpanded: false } file:
                SetExpanded(file.FullPath, true);
                break;
            case Key.Right when selected is ReferenceFileRow file:
                MoveToFirstLine(file);
                break;
            case Key.Escape when model.Filter.Length > 0:
                FilterBox.Clear();
                FocusSelection();
                break;
            default:
                return;
        }

        eventArgs.Handled = true;
    }

    private void OnResultTextInput(object sender, TextCompositionEventArgs eventArgs)
    {
        // 목록에서 글자를 치면 필터로 옮겨 이어서 입력합니다.
        // Space는 미리 보기 키입니다. 메시지 처리 경로에 따라 공백 문자 입력이 뒤따를 수 있어 공백은 넘기지 않습니다.
        if (model.Current is null || string.IsNullOrWhiteSpace(eventArgs.Text) || char.IsControl(eventArgs.Text[0])) return;
        eventArgs.Handled = true;
        FilterBox.Focus();
        FilterBox.Text += eventArgs.Text;
        FilterBox.CaretIndex = FilterBox.Text.Length;
    }

    private void OnResultMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        // 펼침 표시를 누르면 행을 고르지 않고 바로 접거나 펼칩니다. 두 번째 클릭은 더블클릭 처리와 겹치지 않게 무시합니다.
        if (eventArgs.OriginalSource is not FrameworkElement { Name: "Glyph", DataContext: ReferenceFileRow file }) return;
        eventArgs.Handled = true;
        if (eventArgs.ClickCount == 1) SetExpanded(file.FullPath, !file.IsExpanded);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is FrameworkElement { Name: "Glyph" }) return;
        if (ItemsControl.ContainerFromElement(ResultsList, eventArgs.OriginalSource as DependencyObject) is not ListBoxItem container) return;
        eventArgs.Handled = true;
        Activate(container.DataContext, keepFocus: false);
    }

    private void OnResultsPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        // 우클릭한 행을 먼저 골라 메뉴 동작이 보이는 행에 적용되게 합니다. 빈 곳이면 선택은 두고 전체 동작만 보입니다.
        if (ItemsControl.ContainerFromElement(ResultsList, eventArgs.OriginalSource as DependencyObject) is ListBoxItem container)
        {
            container.IsSelected = true;
            container.Focus();
            menuRow = container.DataContext;
        }
        else
        {
            menuRow = null;
        }
    }

    private void OnResultsContextMenuOpening(object sender, ContextMenuEventArgs eventArgs)
    {
        var menu = ResultsList.ContextMenu;
        if (menu is null) return;

        // Shift+F10·메뉴 키로 열면 좌표가 -1입니다. 이때는 선택한 행에 대해 열고 그 행 아래에 붙입니다.
        var keyboard = eventArgs.CursorLeft < 0 && eventArgs.CursorTop < 0;
        if (keyboard) menuRow = ResultsList.SelectedItem;
        Fill(menu, menuRow);
        if (!keyboard)
        {
            menu.Placement = PlacementMode.MousePoint;
            menu.ClearValue(ContextMenu.PlacementTargetProperty);
            return;
        }

        eventArgs.Handled = true;
        var container = menuRow is null ? null : ResultsList.ItemContainerGenerator.ContainerFromItem(menuRow) as UIElement;
        menu.PlacementTarget = container ?? ResultsList;
        menu.Placement = container is null ? PlacementMode.Relative : PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Fill(ContextMenu menu, object? row)
    {
        menu.Items.Clear();
        foreach (var entry in ReferenceMenu.Entries(row, model))
        {
            if (entry is null)
            {
                menu.Items.Add(new Separator());
                continue;
            }

            var item = new MenuItem { Header = entry.Header, InputGestureText = entry.Gesture, IsEnabled = entry.IsEnabled };
            var command = entry.Command;
            item.Click += (_, _) => Run(command, row);
            menu.Items.Add(item);
        }
    }

    private void Run(ReferenceMenuCommand command, object? row)
    {
        var line = row as ReferenceLineRow;
        var path = line?.FullPath ?? (row as ReferenceFileRow)?.FullPath;
        switch (command)
        {
            case ReferenceMenuCommand.Open:
                Activate(line, keepFocus: false);
                break;
            case ReferenceMenuCommand.Preview:
                Activate(line, keepFocus: true);
                break;
            case ReferenceMenuCommand.ToggleFile when row is ReferenceFileRow file:
                SetExpanded(file.FullPath, !file.IsExpanded);
                break;
            case ReferenceMenuCommand.CopyLocation when line is not null:
                Copy(ReferenceMenu.Location(line.Item), "위치를 복사했습니다.");
                break;
            case ReferenceMenuCommand.CopyCode when line is not null:
                Copy(line.Item.SourceLine, "코드 줄을 복사했습니다.");
                break;
            case ReferenceMenuCommand.CopyPath when path is not null:
                Copy(path, "전체 경로를 복사했습니다.");
                break;
            case ReferenceMenuCommand.ShowInExplorer when path is not null:
                if (ResultActions.ShowInExplorer(path) is { } failure) Notify?.Invoke(failure, true);
                break;
            case ReferenceMenuCommand.CopyVisible when model.VisibleCount > 0:
                Copy(ReferenceMenu.Lines(model.VisibleItems), $"보이는 결과 {model.VisibleCount:N0}개를 복사했습니다.");
                break;
            case ReferenceMenuCommand.ExpandAll:
                SetAllExpanded(true);
                break;
            case ReferenceMenuCommand.CollapseAll:
                SetAllExpanded(false);
                break;
        }
    }

    private void Copy(string text, string done)
    {
        var result = ResultActions.Copy(text, done);
        Notify?.Invoke(result, !ReferenceEquals(result, done));
    }

    /// <summary>파일 머리 행은 접거나 펼치고, 위치 행은 편집기에서 엽니다.</summary>
    private void Activate(object? row, bool keepFocus)
    {
        switch (row)
        {
            case ReferenceFileRow file:
                SetExpanded(file.FullPath, !file.IsExpanded);
                break;
            case ReferenceLineRow line:
                OpenLocation?.Invoke(line.Item.Location, !keepFocus);
                break;
        }
    }

    private void SetExpanded(string path, bool expanded)
    {
        model.SetExpanded(path, expanded);
        Refresh(path, keepScroll: true);
        FocusSelection();
    }

    private void MoveToFirstLine(ReferenceFileRow file)
    {
        var first = model.Rows.OfType<ReferenceLineRow>().FirstOrDefault(r => SamePath(r.FullPath, file.FullPath));
        if (first is null) return;
        ResultsList.SelectedItem = first;
        FocusSelection();
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static ScrollViewer? ScrollViewerOf(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer) return viewer;
            if (ScrollViewerOf(child) is { } nested) return nested;
        }

        return null;
    }
}
