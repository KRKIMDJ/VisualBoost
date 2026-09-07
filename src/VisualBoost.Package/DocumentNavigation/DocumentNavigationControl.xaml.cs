using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.DocumentNavigation;

namespace VisualBoost.DocumentNavigation;

public partial class DocumentNavigationControl : Popup, IDisposable
{
    internal Popup Menu => this;
    private DocumentMemberSnapshot? document;
    private long version;
    private int caret;
    private bool nameOrder;
    private bool disposed;
    private string state = "분석 중…";
    private CancellationTokenSource? searchCancellation;
    private IReadOnlyList<DocumentMemberRow> roots = Array.Empty<DocumentMemberRow>();
    public event Action<DocumentMember, long>? Navigate;
    public event Action? ReturnFocus;
    public FrameworkElement? EditorAnchor { get; set; }
    public FrameworkElement? BarAnchor { get; set; }
    internal TextBox SearchInput => Search;
    public Func<DocumentMember, string>? DescribeMember { get; set; }
    public DocumentNavigationControl() { InitializeComponent(); }
    public void Configure(bool sortByName)
    {
        nameOrder = sortByName;
        OrderLabel.Text = nameOrder ? "이름 순서" : "문서 순서";
        RefreshSearch();
    }
    public void SetDocument(DocumentMemberSnapshot? members, long snapshotVersion, string status)
    {
        document = members; version = snapshotVersion; state = status;
        RefreshSearch();
    }
    public void SetCaret(int offset) => caret = offset;
    public void Open()
    {
        if (disposed || EditorAnchor is null) return;
        var anchor = BarAnchor is { IsVisible: true } ? BarAnchor : EditorAnchor;
        Menu.PlacementTarget = anchor;
        Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
        PopupSurface.Width = Math.Max(360, Math.Min(960, anchor.ActualWidth));
        Search.Text = string.Empty;
        Menu.IsOpen = true;
        RefreshSearch();
        Search.Focus();
    }
    [SuppressMessage("Usage", "VSTHRD001", Justification = "Popup의 별도 WPF 표면이 열린 다음 입력 우선순위로 포커스를 예약합니다.")]
    private void OnOpened(object? sender, EventArgs e) => _ = Dispatcher.BeginInvoke(new Action(() => { if (Menu.IsOpen) Search.Focus(); }), DispatcherPriority.Input);
    private void OnClosed(object? sender, EventArgs e) { CancelSearch(); }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (Results is not null) RefreshSearch(); }
    private void CancelSearch() { searchCancellation?.Cancel(); searchCancellation = null; }
    private void RefreshSearch()
    {
        CancelSearch();
        Results.ItemsSource = null;
        roots = Array.Empty<DocumentMemberRow>();
        Status.Text = state;
        if (!Menu.IsOpen || document is null || disposed) return;
        var request = searchCancellation = new CancellationTokenSource();
        _ = SearchAsync(document, Search.Text, nameOrder, request);
    }
    private async Task SearchAsync(DocumentMemberSnapshot source, string query, bool sort, CancellationTokenSource request)
    {
        try
        {
            var found = await Task.Run(() =>
            {
                var members = source.Search(query, sort, request.Token);
                return (count: members.Count, tree: DocumentMemberTree.Build(members, request.Token, sort && string.IsNullOrWhiteSpace(query)));
            });
            if (disposed || request.IsCancellationRequested || searchCancellation != request || source != document || !Menu.IsOpen) return;
            roots = found.tree.Select(n => MakeRow(n, null)).ToArray();
            var rows = VisibleRows().ToArray();
            var current = string.IsNullOrWhiteSpace(query) ? source.FindContaining(caret) : null;
            Display(current is null ? rows.FirstOrDefault(r => r.Member is not null) : rows.FirstOrDefault(r => r.Member == current));
            Status.Text = found.count == 0 ? "일치하는 함수 없음" : $"{found.count:N0}개 표시 / {source.Members.Count:N0}개 함수";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // 이벤트 진입점의 비동기 오류는 사용자에게 표시하고 다음 입력에서 재시도합니다.
            if (!disposed && searchCancellation == request) Status.Text = "검색 실패: " + exception.Message;
            System.Diagnostics.Trace.TraceError("VisualBoost.DocumentMembers: " + exception);
        }
        finally { if (searchCancellation == request) searchCancellation = null; request.Dispose(); }
    }
    private DocumentMemberRow MakeRow(DocumentTreeNode node, DocumentMemberRow? parent)
    {
        var row = new DocumentMemberRow(node, parent, node.Member is null ? null : DescribeMember?.Invoke(node.Member));
        row.Children = node.Children.Select(n => MakeRow(n, row)).ToArray();
        return row;
    }
    private IEnumerable<DocumentMemberRow> VisibleRows()
    {
        var pending = new Stack<DocumentMemberRow>(roots.Reverse());
        while (pending.Count > 0)
        {
            var row = pending.Pop(); yield return row;
            if (row.IsExpanded) foreach (var child in row.Children.Reverse()) pending.Push(child);
        }
    }
    private void Display(DocumentMemberRow? selected)
    {
        var visible = VisibleRows().ToArray(); Results.ItemsSource = visible;
        Results.SelectedItem = selected is not null && visible.Contains(selected) ? selected : visible.FirstOrDefault();
        if (Results.SelectedItem is not null) Results.ScrollIntoView(Results.SelectedItem);
    }
    internal void ToggleBranch(DocumentMemberRow row)
    {
        if (row.Children.Count == 0) return;
        row.IsExpanded = !row.IsExpanded; Display(row);
    }
    private void OnExpanderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button ?? e.OriginalSource as Button)?.DataContext is DocumentMemberRow row) ToggleBranch(row);
        e.Handled = true;
    }
    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
        else if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
        else if (e.Key == Key.Tab) { e.Handled = true; ToggleFocus(); }
        else if (e.Key is Key.Left or Key.Right && !Search.IsKeyboardFocusWithin && Results.SelectedItem is DocumentMemberRow row)
        {
            if (e.Key == Key.Left)
            { if (row.Children.Count > 0 && row.IsExpanded) ToggleBranch(row); else if (row.Parent is not null) Display(row.Parent); }
            else if (row.Children.Count > 0)
            { if (!row.IsExpanded) ToggleBranch(row); else Display(row.Children[0]); }
            e.Handled = true;
        }
        else if ((e.Key == Key.Down || e.Key == Key.Up) && Search.IsKeyboardFocusWithin)
        {
            if (Results.Items.Count > 0)
            {
                Results.SelectedIndex = Math.Max(0, Math.Min(Results.Items.Count - 1, Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1)));
                Results.ScrollIntoView(Results.SelectedItem);
            }
            e.Handled = true;
        }
    }
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 펼침 버튼의 두 번째 클릭을 행 열기로 다시 처리하지 않습니다.
        for (var origin = e.OriginalSource as DependencyObject; origin is not null;
             origin = origin is Visual ? VisualTreeHelper.GetParent(origin) : (origin as FrameworkContentElement)?.Parent)
            if (origin is ButtonBase) return;
        if (ItemsControl.ContainerFromElement(Results, e.OriginalSource as DependencyObject) is ListViewItem) Accept();
    }
    internal void MoveSelection(int delta)
    {
        if (Results.Items.Count == 0) return;
        Results.SelectedIndex = Math.Max(0, Math.Min(Results.Items.Count - 1, Results.SelectedIndex + delta));
        Results.ScrollIntoView(Results.SelectedItem);
    }
    internal void ToggleFocus() { if (Search.IsKeyboardFocusWithin) Results.Focus(); else Search.Focus(); }
    internal bool MoveTree(bool right)
    {
        if (Search.IsKeyboardFocusWithin || Results.SelectedItem is not DocumentMemberRow row) return false;
        if (!right)
        { if (row.Children.Count > 0 && row.IsExpanded) ToggleBranch(row); else if (row.Parent is not null) Display(row.Parent); }
        else if (row.Children.Count > 0)
        { if (!row.IsExpanded) ToggleBranch(row); else Display(row.Children[0]); }
        return true;
    }
    internal void Cancel() { Menu.IsOpen = false; ReturnFocus?.Invoke(); }
    internal void Accept()
    {
        if (document is null || Results.SelectedItem is not DocumentMemberRow row) return;
        if (row.Member is null) { ToggleBranch(row); return; }
        Menu.IsOpen = false;
        Navigate?.Invoke(row.Member, version);
    }
    public void Dispose()
    {
        disposed = true; Menu.IsOpen = false; CancelSearch();
        Navigate = null; ReturnFocus = null; DescribeMember = null; EditorAnchor = null; BarAnchor = null;
        Results.ItemsSource = null; roots = Array.Empty<DocumentMemberRow>(); document = null;
    }
}

public sealed class DocumentMemberRow : INotifyPropertyChanged
{
    public DocumentMemberRow(DocumentTreeNode node, DocumentMemberRow? parent, string? tooltip)
    { Node = node; Parent = parent; Depth = parent is null ? 0 : parent.Depth + 1; Tooltip = tooltip ?? node.Name; }
    public DocumentTreeNode Node { get; }
    public DocumentMember? Member => Node.Member;
    public DocumentMemberRow? Parent { get; }
    public IReadOnlyList<DocumentMemberRow> Children { get; internal set; } = Array.Empty<DocumentMemberRow>();
    public int Depth { get; }
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public Visibility ExpanderVisibility => Children.Count > 0 ? Visibility.Visible : Visibility.Hidden;
    private bool expanded = true;
    public bool IsExpanded { get => expanded; set { expanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpanderText))); } }
    public string ExpanderText => IsExpanded ? "▾" : "▸";
    public string Name => Node.Name;
    public string Kind => Node.Kind;
    public string Line => Member?.Line.ToString() ?? "";
    public SourceSymbolKind ColorKind => Member is not null ? SourceSymbolKind.Function : Enum.TryParse<SourceSymbolKind>(Kind, true, out var kind) ? kind : SourceSymbolKind.Namespace;
    public string Tooltip { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
}
