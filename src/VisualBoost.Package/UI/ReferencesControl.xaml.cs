using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VisualBoost.Core.SemanticNavigation;

namespace VisualBoost.UI;

/// <summary>
/// 도킹 참조 창의 내용입니다. 결과를 파일별로 묶어 보이고, 키보드만으로 훑어보고 열 수 있게 합니다.
/// 표시 상태는 <see cref="ReferenceResultsModel"/>이 갖고 이 컨트롤은 화면 동기화와 입력만 맡습니다. UI thread에서만 씁니다.
/// </summary>
public partial class ReferencesControl : UserControl
{
    private const string EmptyMessage = "C++ 편집기에서 참조 찾기(Shift+Alt+F)를 실행하면 결과가 여기에 표시됩니다.";
    private const string NoMatchMessage = "필터와 일치하는 위치가 없습니다.";

    private readonly ReferenceResultsModel model = new();

    // 코드에서 필터·묶기·최근 결과 선택을 맞추는 동안 생기는 변경 이벤트를 무시합니다.
    private bool syncing;

    public ReferencesControl()
    {
        // XAML을 읽는 동안 생기는 변경 이벤트는 아직 만들지 않은 요소를 건드리므로 무시합니다. 첫 Refresh가 끝나면 풀립니다.
        syncing = true;
        InitializeComponent();
        Refresh(null, keepScroll: false);
    }

    /// <summary>위치를 엽니다. 두 번째 인수가 true이면 편집기로 초점을 옮기고, false이면 이 창에 초점을 남깁니다.</summary>
    internal Action<NavigationLocation, bool>? OpenLocation { get; set; }

    internal ReferenceResultsModel Model => model;

    /// <summary>새 결과를 보이고 첫 위치를 고른 뒤 목록에 초점을 둡니다.</summary>
    [SuppressMessage("Usage", "VSTHRD001", Justification = "창을 처음 만들 때는 배치가 끝난 뒤에야 행 컨테이너가 생기므로 같은 UI thread에서 초점 이동만 미룹니다.")]
    internal void Show(ReferenceResultSet set)
    {
        model.Show(set);
        Refresh(null, keepScroll: false);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FocusSelection));
    }

    /// <summary>모든 결과를 지웁니다. 닫은 Solution의 위치를 다시 열지 않게 합니다.</summary>
    internal void Clear()
    {
        model.Clear();
        Refresh(null, keepScroll: false);
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
            GroupToggle.IsChecked = model.GroupByFile;
            ExpandAllButton.IsEnabled = CollapseAllButton.IsEnabled = current is not null && model.GroupByFile;
            HistoryBox.ItemsSource = model.History.ToArray();
            HistoryBox.SelectedItem = current;
            HistoryBox.IsEnabled = model.History.Count > 0;

            // 행 목록을 바꾸면 스크롤이 맨 위로 돌아가므로, 접기·필터처럼 같은 결과 안의 변경은 보던 위치를 지킵니다.
            var scroll = keepScroll ? ScrollViewerOf(ResultsList) : null;
            var offset = scroll?.VerticalOffset ?? 0;
            ResultsList.ItemsSource = model.Rows;
            if (scroll is not null)
            {
                ResultsList.UpdateLayout();
                scroll.ScrollToVerticalOffset(offset);
            }

            Select(keep);
            EmptyText.Text = current is null ? EmptyMessage : NoMatchMessage;
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

    private void OnGroupToggled(object sender, RoutedEventArgs eventArgs)
    {
        if (syncing) return;
        var keep = SelectionKey();
        model.GroupByFile = GroupToggle.IsChecked == true;
        Refresh(keep, keepScroll: false);
    }

    private void OnExpandAll(object sender, RoutedEventArgs eventArgs) => SetAllExpanded(true);

    private void OnCollapseAll(object sender, RoutedEventArgs eventArgs) => SetAllExpanded(false);

    private void SetAllExpanded(bool expanded)
    {
        var keep = SelectionKey();
        model.SetAllExpanded(expanded);
        Refresh(keep, keepScroll: false);
    }

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (syncing || HistoryBox.SelectedItem is not ReferenceResultSet set || ReferenceEquals(set, model.Current)) return;
        // 다른 심볼의 결과로 돌아가므로 이전 필터는 지웁니다.
        model.Select(set, clearFilter: true);
        Refresh(null, keepScroll: false);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
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
            case Key.Left when selected is ReferenceLineRow line && model.GroupByFile:
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
