using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    public event Action<DocumentMember, long>? Navigate;
    public event Action? ReturnFocus;
    public FrameworkElement? EditorAnchor { get; set; }
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
        var anchor = EditorAnchor;
        Menu.PlacementTarget = anchor;
        Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
        PopupSurface.Width = Math.Max(360, Math.Min(760, anchor.ActualWidth));
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
        Status.Text = state;
        if (!Menu.IsOpen || document is null || disposed) return;
        var request = searchCancellation = new CancellationTokenSource();
        _ = SearchAsync(document, Search.Text, nameOrder, request);
    }
    private async Task SearchAsync(DocumentMemberSnapshot source, string query, bool sort, CancellationTokenSource request)
    {
        try
        {
            var found = await Task.Run(() => source.Search(query, sort, request.Token));
            if (disposed || request.IsCancellationRequested || searchCancellation != request || source != document || !Menu.IsOpen) return;
            var rows = found.Select(m => new DocumentMemberRow(m, DescribeMember?.Invoke(m))).ToArray();
            Results.ItemsSource = rows;
            var current = string.IsNullOrWhiteSpace(query) ? source.FindContaining(caret) : null;
            Results.SelectedItem = rows.FirstOrDefault(r => r.Member == current) ?? rows.FirstOrDefault();
            if (Results.SelectedItem is not null) Results.ScrollIntoView(Results.SelectedItem);
            Status.Text = rows.Length == 0 ? "일치하는 함수 없음" : $"{rows.Length:N0}개 표시 / {source.Members.Count:N0}개 함수";
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
    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Menu.IsOpen = false; ReturnFocus?.Invoke(); e.Handled = true; }
        else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
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
        if (ItemsControl.ContainerFromElement(Results, e.OriginalSource as DependencyObject) is ListViewItem) Accept();
    }
    private void Accept()
    {
        if (document is null || Results.SelectedItem is not DocumentMemberRow row) return;
        Menu.IsOpen = false;
        Navigate?.Invoke(row.Member, version);
    }
    public void Dispose()
    {
        disposed = true; Menu.IsOpen = false; CancelSearch();
        Navigate = null; ReturnFocus = null; DescribeMember = null; EditorAnchor = null;
        Results.ItemsSource = null; document = null;
    }
}

public sealed class DocumentMemberRow
{
    public DocumentMemberRow(DocumentMember member, string? tooltip) { Member = member; Tooltip = tooltip ?? $"{member.Name} · {member.Line}"; }
    public DocumentMember Member { get; }
    public string Name => Member.Name;
    public string Kind => Member.Kind;
    public int Line => Member.Line;
    public SourceSymbolKind ColorKind => SourceSymbolKind.Function;
    public string Tooltip { get; }
}
