using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VisualBoost.UI;

// 열 합계를 뷰포트에 맞추고, 경계를 끌면 인접 열이 반대 방향으로 변하게 합니다.
public sealed class FittedResultsList : ListView
{
    private readonly DependencyPropertyDescriptor widthDescriptor =
        DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));
    private GridView? grid;
    private ScrollViewer? scroll;
    private double[] widths = Array.Empty<double>();
    private double[] defaults = Array.Empty<double>();
    private bool updating;

    public FittedResultsList()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (grid is not null || View is not GridView view) return;
        grid = view;
        widths = grid.Columns.Select(column => double.IsNaN(column.Width) ? 100 : column.Width).ToArray();
        if (defaults.Length == 0) defaults = (double[])widths.Clone();
        var reset = new MenuItem { Header = "열 너비 초기화" };
        reset.Click += (_, __) => ResetColumnWidths();
        grid.ColumnHeaderContextMenu = new ContextMenu();
        grid.ColumnHeaderContextMenu.Items.Add(reset);
        foreach (var column in grid.Columns) widthDescriptor.AddValueChanged(column, OnColumnWidthChanged);
        // Popup 안에서는 Loaded 시점에도 기본 템플릿이 아직 생성되지 않을 수 있습니다.
        ApplyTemplate();
        scroll = FindScrollViewer(this);
        if (scroll is not null) scroll.ScrollChanged += OnScrollChanged;
        SizeChanged += OnSizeChanged;
        FitColumns();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (grid is not null)
            foreach (var column in grid.Columns) widthDescriptor.RemoveValueChanged(column, OnColumnWidthChanged);
        if (scroll is not null) scroll.ScrollChanged -= OnScrollChanged;
        SizeChanged -= OnSizeChanged;
        grid = null;
        scroll = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => FitColumns();

    public void ResetColumnWidths()
    {
        if (defaults.Length == 0) return;
        widths = (double[])defaults.Clone();
        FitColumns();
    }
    private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (args.ViewportWidthChange != 0) FitColumns();
    }

    private void FitColumns()
    {
        var available = scroll?.ViewportWidth ?? 0;
        if (grid is null || updating || available <= 0 || widths.Length == 0) return;
        var total = widths.Sum();
        if (total <= 0) return;
        Apply(widths.Select(width => width * available / total).ToArray());
    }

    private void OnColumnWidthChanged(object? sender, EventArgs args)
    {
        if (updating || grid is null || sender is not GridViewColumn column) return;
        var index = grid.Columns.IndexOf(column);
        if (index < 0 || widths.Length < 2) return;
        var neighbor = index == widths.Length - 1 ? index - 1 : index + 1;
        var pair = widths[index] + widths[neighbor];
        var minimum = Math.Min(48, pair / 2);
        var requested = double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
        var next = (double[])widths.Clone();
        next[index] = Math.Max(minimum, Math.Min(pair - minimum, requested));
        next[neighbor] = pair - next[index];
        Apply(next);
    }

    private void Apply(double[] next)
    {
        if (grid is null) return;
        updating = true;
        try
        {
            widths = next;
            for (var index = 0; index < next.Length; index++) grid.Columns[index].Width = next[index];
        }
        finally { updating = false; }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer) return viewer;
            var nested = FindScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
