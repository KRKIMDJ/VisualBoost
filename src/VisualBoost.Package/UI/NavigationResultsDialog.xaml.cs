using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Core.SemanticNavigation;

namespace VisualBoost.UI;

/// <summary>정의 후보를 보여 주고 하나를 고르게 합니다. 참조 결과는 도킹 창(<see cref="ReferencesToolWindow"/>)에 보입니다.</summary>
public partial class NavigationResultsDialog : DialogWindow
{
    /// <summary>창 크기·위치를 저장하는 이름입니다(<see cref="WindowPlacementStore"/>).</summary>
    private const string PlacementName = "NavigationResults";

    private readonly IReadOnlyList<NavigationResultItem> items;
    private readonly string status;

    internal NavigationResultsDialog(string title, string header, IReadOnlyList<NavigationResultItem> items, string status)
    {
        this.items = items ?? throw new ArgumentNullException(nameof(items));
        this.status = status;
        ThreadHelper.ThrowIfNotOnUIThread();
        EditorCodeStyleSource.EnsureStarted();
        ResultListFont.EnsureTracking();
        InitializeComponent();
        Title = title;
        HeaderText.Text = header;
        HeaderText.ToolTip = header;
        KeyboardHintText.ToolTip = ResultListKeys.MoveKeysDescription;
        // 다른 검색 창처럼 사용자가 맞춘 크기·위치로 다시 엽니다. 여는 명령마다 반복하지 않도록 창이 직접 읽고 씁니다.
        WindowPlacement.Apply(this, WindowPlacementStore.Load(ServiceProvider.GlobalProvider, PlacementName));
        Closing += OnClosing;
        ApplyFilter();
        Loaded += (_, _) =>
        {
            FilterBox.Focus();
            Keyboard.Focus(FilterBox);
        };
    }

    public NavigationLocation? SelectedLocation { get; private set; }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        FilterPlaceholder.Visibility = string.IsNullOrEmpty(FilterBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = FilterBox.Text.Trim();
        var visible = query.Length == 0 ? items : items.Where(item => item.Matches(query)).ToArray();
        ResultsList.ItemsSource = visible;
        ResultsList.SelectedIndex = visible.Count > 0 ? 0 : -1;
        StatusText.Text = (query.Length == 0 ? $"{items.Count:N0}개 위치" : $"{visible.Count:N0}/{items.Count:N0}개 위치") +
                          (string.IsNullOrEmpty(status) ? string.Empty : " · " + status);
        StatusText.ToolTip = status;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        switch (eventArgs.Key)
        {
            case Key.Escape:
                DialogResult = false;
                break;
            case Key.Enter:
                AcceptSelection();
                break;
            default:
                // 파일·심볼 탐색과 같은 목록 이동 키(↑↓·PgUp/PgDn·Ctrl+Home/End)를 씁니다.
                var pageSize = ResultListKeys.PageSize(ResultsList, 24);
                if (!ResultListKeys.TryMove(eventArgs.Key, Keyboard.Modifiers, ResultsList.SelectedIndex, ResultsList.Items.Count, pageSize, out var target))
                    return;
                if (target >= 0)
                {
                    ResultsList.SelectedIndex = target;
                    ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                }

                break;
        }

        eventArgs.Handled = true;
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        WindowPlacementStore.Save(ServiceProvider.GlobalProvider, PlacementName, WindowPlacement.Capture(this));
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (ItemsControl.ContainerFromElement(ResultsList, eventArgs.OriginalSource as DependencyObject) is ListBoxItem)
        {
            AcceptSelection();
        }
    }

    private void AcceptSelection()
    {
        if (ResultsList.SelectedItem is not NavigationResultItem selected) return;
        SelectedLocation = selected.Location;
        DialogResult = true;
    }
}
