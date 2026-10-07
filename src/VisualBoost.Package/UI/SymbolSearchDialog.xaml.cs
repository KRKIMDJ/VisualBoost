using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Searching;
using VisualBoost.Core.Indexing;
using VisualBoost.Services;

namespace VisualBoost.UI;

public partial class SymbolSearchDialog : DialogWindow
{
    // 사용자가 직접 고른 마지막 범위는 같은 Solution(파일 기준) 안에서만 VS를 닫을 때까지 기억합니다. 프로젝트 범위 ID가
    // Solution마다 다르므로 다른 Solution에서는 쓰지 않고, 영구 저장도 하지 않습니다. UI thread에서만 씁니다.
    private static string? rememberedSolution;
    private static string? rememberedScopeId;
    private static string? rememberedScopeName;

    private readonly SolutionFileIndexService fileIndex;
    private readonly IReadOnlyList<SolutionProjectInfo> projects;
    private readonly string? solutionKey;
    private readonly DispatcherTimer statusTimer;
    // 검색이 이 시간을 넘길 때만 "검색 중"을 보입니다. 분석 중에는 상태 타이머가 0.5초마다 다시 검색하므로 바로 바꾸면 상태 글자가
    // 결과 수와 번갈아 깜박입니다(2026-10-07 사용자 피드백, 파일 탐색 창과 같은 규칙). 입력 대기 시간은 검색 시간에 넣지 않습니다.
    private static readonly TimeSpan BusyDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan InputDelay = TimeSpan.FromMilliseconds(45);
    private readonly DispatcherTimer busyTimer;
    // "검색 중" 안내가 지금 보이는지입니다. Esc는 이 안내가 보일 때만 검색을 취소하고, 그 밖에는 "Esc 닫기" 안내대로 창을 닫습니다.
    private bool busyShown;
    private readonly string idleKeyboardHint;
    // 사용자가 검색을 취소하면 검색어나 범위를 바꿀 때까지 분석 진행에 따른 자동 재검색을 멈춥니다(파일 탐색 창과 같은 규칙).
    private bool searchPaused;
    private CancellationTokenSource? searchCancellation;
    private bool isSearching;
    private bool isClosed;
    private int observedSymbolCount = -1;
    private bool observedIsAnalyzing;
    private string? observedAnalysisError;
    private IReadOnlyList<SymbolSearchScope>? observedScopes;
    private bool updatingScopes;
    private readonly SymbolScopeChoice scopeChoice;

    internal SymbolSearchDialog(
        SolutionFileIndexService fileIndex,
        IReadOnlyList<SolutionProjectInfo> projects,
        string? solutionKey = null,
        string? initialQuery = null)
    {
        this.fileIndex = fileIndex ?? throw new ArgumentNullException(nameof(fileIndex));
        this.projects = projects ?? throw new ArgumentNullException(nameof(projects));
        this.solutionKey = solutionKey;
        scopeChoice = SameSolution(rememberedSolution, solutionKey)
            ? new SymbolScopeChoice(rememberedScopeId, rememberedScopeName)
            : new SymbolScopeChoice(null, null);
        ResultListFont.EnsureTracking();
        InitializeComponent();
        UpdateScopes();
        // 로드 전 TextChanged는 무시되고, 첫 검색은 OnLoaded에서 이 검색어로 실행합니다.
        if (!string.IsNullOrEmpty(initialQuery)) SearchBox.Text = initialQuery;
        idleKeyboardHint = KeyboardHintText.Text;
        KeyboardHintText.ToolTip = ResultListKeys.MoveKeysDescription + "\n" + ResultListKeys.ScopeKeysDescription +
                                   "\nEnter 열기 · Esc 닫기 · 결과 우클릭으로 이름·경로 복사";
        statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        statusTimer.Tick += OnStatusTimerTick;
        busyTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = BusyDelay };
        busyTimer.Tick += OnBusyTimerTick;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public SourceSymbolLocation? SelectedLocation { get; private set; }

    /// <summary>닫힐 때의 창 경계입니다. 명령이 다음 열기를 위해 저장합니다.</summary>
    internal Rect? ClosedBounds { get; private set; }

    private SymbolSearchScope SelectedScope => ScopeSelector.SelectedItem as SymbolSearchScope ?? SymbolSearchScope.All;

    private bool UpdateScopes()
    {
        var scopes = fileIndex.SymbolScopes;
        if (ReferenceEquals(scopes, observedScopes)) return false;
        observedScopes = scopes;
        updatingScopes = true;
        try
        {
            ScopeSelector.ItemsSource = scopes;
            // 바라던 범위가 아직 게시되지 않았으면 '전체'를 보이고, 게시되면 그 범위로 바꿉니다. 소속 목록이 게시되었는데도
            // 없으면 이번 창은 '전체'로 확정합니다(SymbolScopeChoice). 상태 줄은 다음 갱신에서 다시 그립니다.
            ScopeSelector.SelectedItem = scopeChoice.Choose(scopes);
        }
        finally { updatingScopes = false; }
        return true;
    }

    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "WPF 이벤트이며 검색 취소와 예외를 호출 경로에서 처리합니다.")]
    private async void OnScopeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (updatingScopes || isClosed) return;
        scopeChoice.UserSelected(SelectedScope);
        if (!IsLoaded) return;
        await RefreshResultsAsync(useDebounce: false);
    }

    [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "WPF 이벤트이며 검색 취소와 예외를 호출 경로에서 처리합니다.")]
    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        // 미리 채운 검색어는 전체 선택해 두어 바로 입력하면 덮어쓰게 합니다.
        SearchBox.SelectAll();
        UpdateSearchControls();
        var snapshot = fileIndex.GetSnapshot();
        observedSymbolCount = snapshot.SymbolCount;
        observedIsAnalyzing = snapshot.IsAnalyzing;
        UpdateIdleState(snapshot);
        statusTimer.Start();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) await RefreshResultsAsync(useDebounce: false);
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs) => ClosedBounds = WindowPlacement.Capture(this);

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        isClosed = true;
        statusTimer.Stop();
        statusTimer.Tick -= OnStatusTimerTick;
        busyTimer.Stop();
        busyTimer.Tick -= OnBusyTimerTick;
        searchCancellation?.Cancel();
        searchCancellation = null;
        rememberedSolution = solutionKey;
        rememberedScopeId = scopeChoice.NextId;
        rememberedScopeName = scopeChoice.NextName;
    }

    /// <summary>
    /// 사용자가 지금 범위로 입력·이동을 시작하면 늦게 게시되는 이전 범위로 바꾸지 않습니다. 보던 결과가 저절로
    /// 바뀌지 않게 하려는 것이며, 이번 창에만 적용하고 다음 열기의 기억은 그대로 둡니다.
    /// </summary>
    private void SettleScope()
    {
        var note = PendingScopeNote();
        if (scopeChoice.Settle(SelectedScope)) StatusText.Text = StatusText.Text.Replace(note, string.Empty);
    }

    private string PendingScopeNote() => scopeChoice.PendingNote(SelectedScope);

    [SuppressMessage(
        "Usage",
        "VSTHRD100:Avoid async void methods",
        Justification = "WPF 이벤트 시그니처이며 검색 취소와 예외를 메서드 내부에서 처리합니다.")]
    private async void OnSearchTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (!IsLoaded || isClosed) return;
        SettleScope();
        UpdateSearchControls();
        await RefreshResultsAsync(useDebounce: true);
    }

    private void UpdateSearchControls()
    {
        var isEmpty = string.IsNullOrWhiteSpace(SearchBox.Text);
        SearchPlaceholder.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = isEmpty ? Visibility.Hidden : Visibility.Visible;
    }

    private async Task RefreshResultsAsync(bool useDebounce)
    {
        if (isClosed) return;
        // 멈춘 동안 상태 타이머는 범위가 바뀔 때만 여기로 오므로, 이 호출은 검색어·범위 변경입니다.
        searchPaused = false;
        var previousCancellation = searchCancellation;
        var currentCancellation = new CancellationTokenSource();
        searchCancellation = currentCancellation;
        previousCancellation?.Cancel();

        var query = SearchBox.Text;
        var scope = SelectedScope;
        if (string.IsNullOrWhiteSpace(query))
        {
            isSearching = false;
            busyTimer.Stop();
            SetBusyShown(false);
            ResultsList.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Visible;
            UpdateIdleState(fileIndex.GetSnapshot());
            searchCancellation = null;
            currentCancellation.Dispose();
            return;
        }

        var cancellationToken = currentCancellation.Token;
        isSearching = true;
        // 이미 "검색 중"이 보이면 이어지는 검색 동안 그대로 둡니다.
        busyTimer.Stop();
        busyTimer.Interval = useDebounce ? BusyDelay + InputDelay : BusyDelay;
        busyTimer.Start();
        try
        {
            if (useDebounce)
            {
                await Task.Delay(InputDelay, cancellationToken);
            }

            var matches = await Task.Run(
                () => fileIndex.SearchSymbols(query, 201, cancellationToken, scope),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var items = matches.Take(200).Select(match => new SymbolSearchResultItem(match, projects, query)).ToArray();
            ResultsList.ItemsSource = items;
            ResultsList.SelectedIndex = items.Length > 0 ? 0 : -1;
            EmptyStatePanel.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (items.Length == 0)
            {
                var snapshot = fileIndex.GetSnapshot();
                EmptyStateTitle.Text = snapshot.IsAnalyzing
                    ? "심볼 분석이 진행 중입니다."
                    : "일치하는 심볼이 없습니다.";
                EmptyStateDescription.Text = snapshot.IsAnalyzing
                    ? "캐시 또는 새 분석 결과가 준비되면 자동으로 다시 검색합니다."
                    : snapshot.AnalysisError ?? (scope.Id == SymbolSearchScope.All.Id
                        ? "심볼명을 변경해 보세요."
                        : "심볼명을 변경하거나 Ctrl+Tab으로 검색 범위를 바꿔 보세요.");
            }

            var currentSnapshot = fileIndex.GetSnapshot();
            StatusText.Text = $"{items.Length:N0}개 표시" + (matches.Count > 200 ? " · 추가 결과 있음" : string.Empty) +
                              $" · {scope.Name}{PendingScopeNote()} · 전체 등록 위치 {currentSnapshot.SymbolCount:N0}개" +
                              AnalysisStatus(currentSnapshot);
            StatusText.ToolTip = currentSnapshot.AnalysisError;
        }
        catch (OperationCanceledException)
        {
            // 새 검색이 앞선 검색을 취소한 경우는 새 검색이 화면을 그립니다. 여기에 오는 것은 Esc 취소와 창 닫기뿐입니다.
            if (ReferenceEquals(searchCancellation, currentCancellation) && !isClosed) ShowCancelled();
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(searchCancellation, currentCancellation)) return;
            ResultsList.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateTitle.Text = "심볼 검색을 완료하지 못했습니다.";
            EmptyStateDescription.Text = exception.Message;
            StatusText.Text = "검색 오류";
        }
        finally
        {
            if (ReferenceEquals(searchCancellation, currentCancellation))
            {
                isSearching = false;
                busyTimer.Stop();
                if (!isClosed) SetBusyShown(false);
            }
            if (ReferenceEquals(searchCancellation, currentCancellation)) searchCancellation = null;
            currentCancellation.Dispose();
        }
    }

    [SuppressMessage(
        "Usage",
        "VSTHRD100:Avoid async void methods",
        Justification = "WPF 타이머 이벤트이며 갱신 취소와 예외를 호출 경로에서 처리합니다.")]
    private async void OnStatusTimerTick(object? sender, EventArgs eventArgs)
    {
        if (isClosed) return;
        var scopeChanged = UpdateScopes();
        if ((isSearching || searchPaused) && !scopeChanged) return;
        var snapshot = fileIndex.GetSnapshot();
        if (!scopeChanged && snapshot.SymbolCount == observedSymbolCount && snapshot.IsAnalyzing == observedIsAnalyzing &&
            snapshot.AnalysisError == observedAnalysisError)
        {
            return;
        }

        observedSymbolCount = snapshot.SymbolCount;
        observedIsAnalyzing = snapshot.IsAnalyzing;
        observedAnalysisError = snapshot.AnalysisError;
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            UpdateIdleState(snapshot);
            return;
        }

        await RefreshResultsAsync(useDebounce: false);
    }

    private void OnBusyTimerTick(object? sender, EventArgs eventArgs)
    {
        busyTimer.Stop();
        if (isClosed || !isSearching) return;
        StatusText.Text = "검색 중";
        SetBusyShown(true);
    }

    private void SetBusyShown(bool shown)
    {
        busyShown = shown;
        KeyboardHintText.Text = shown ? "Esc 검색 취소" : idleKeyboardHint;
    }

    /// <summary>
    /// Esc로 검색을 취소합니다. 검색이 취소를 알아차리기를 기다리지 않고 안내를 바로 내려 두 번째 Esc는 창을 닫습니다(파일 탐색 창과 같은 규칙).
    /// </summary>
    private void CancelActiveSearch()
    {
        searchCancellation?.Cancel();
        isSearching = false;
        searchPaused = true;
        busyTimer.Stop();
        SetBusyShown(false);
        ShowCancelled();
    }

    /// <summary>Esc로 검색을 취소한 상태를 보입니다. 보이던 결과는 그대로 두고, 결과가 없으면 취소 안내를 보입니다.</summary>
    private void ShowCancelled()
    {
        var shown = ResultsList.Items.Count;
        if (shown == 0)
        {
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateTitle.Text = "검색을 취소했습니다.";
            EmptyStateDescription.Text = "검색어를 변경하면 다시 검색합니다.";
        }

        var snapshot = fileIndex.GetSnapshot();
        StatusText.Text = $"{shown:N0}개 표시 · 검색 취소됨 · {SelectedScope.Name}{PendingScopeNote()} · 전체 등록 위치 {snapshot.SymbolCount:N0}개" +
                          AnalysisStatus(snapshot);
        StatusText.ToolTip = snapshot.AnalysisError;
    }

    private void UpdateIdleState(SolutionFileIndexSnapshot snapshot)
    {
        if (snapshot.IsAnalyzing)
        {
            EmptyStateTitle.Text = "심볼 분석이 진행 중입니다.";
            EmptyStateDescription.Text = "검색어를 미리 입력하면 결과가 준비되는 즉시 갱신합니다.";
        }
        else
        {
            EmptyStateTitle.Text = "심볼명을 입력하세요.";
            EmptyStateDescription.Text = snapshot.AnalysisError ?? "심볼 이름만 검색합니다. Ctrl+Tab으로 검색 범위를 바꿉니다.";
        }

        StatusText.Text = $"0개 표시 · {SelectedScope.Name}{PendingScopeNote()} · 전체 등록 위치 {snapshot.SymbolCount:N0}개" +
                          AnalysisStatus(snapshot);
        StatusText.ToolTip = snapshot.AnalysisError;
    }

    private static string AnalysisStatus(SolutionFileIndexSnapshot snapshot) => snapshot.IsAnalyzing
        ? " · 소스 분석 중"
        : snapshot.AnalysisError is not null ? " · 분석 확인 필요" : string.Empty;

    private void OnClearSearchClick(object sender, RoutedEventArgs eventArgs)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            if (ScopeSelector.IsDropDownOpen) return;
            // 안내가 보이기 전의 짧은 검색(입력 대기·분석 중 재검색)은 Esc로 창을 닫으면서 함께 취소됩니다.
            if (busyShown)
            {
                CancelActiveSearch();
            }
            else
            {
                DialogResult = false;
            }

            eventArgs.Handled = true;
            return;
        }

        // 범위 드롭다운이 열려 있으면 화살표·Enter는 드롭다운 항목 선택에 씁니다.
        if (ScopeSelector.IsDropDownOpen) return;

        if (eventArgs.Key == Key.Enter)
        {
            AcceptSelection();
            eventArgs.Handled = true;
            return;
        }

        if (ResultListKeys.IsScopeCycle(eventArgs.Key, Keyboard.Modifiers, out var forward))
        {
            var next = ResultListKeys.NextScope(ScopeSelector.SelectedIndex, ScopeSelector.Items.Count, forward, _ => true);
            if (next >= 0) ScopeSelector.SelectedIndex = next;
            eventArgs.Handled = true;
            return;
        }

        var pageSize = ResultListKeys.PageSize(ResultsList, 24);
        if (ResultListKeys.TryMove(eventArgs.Key, Keyboard.Modifiers, ResultsList.SelectedIndex, ResultsList.Items.Count, pageSize, out var target))
        {
            SettleScope();
            if (target >= 0)
            {
                ResultsList.SelectedIndex = target;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }

            eventArgs.Handled = true;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (ItemsControl.ContainerFromElement(ResultsList, eventArgs.OriginalSource as DependencyObject) is ListBoxItem)
            AcceptSelection();
    }

    private void OnResultsPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        // 우클릭한 행을 먼저 골라 메뉴 동작이 보이는 행에 적용되게 합니다.
        if (ItemsControl.ContainerFromElement(ResultsList, eventArgs.OriginalSource as DependencyObject) is not ListBoxItem item)
        {
            ResultsList.SelectedItem = null;
            return;
        }

        item.IsSelected = true;
        ResultsList.Focus();
    }

    private void OnResultsContextMenuOpening(object sender, ContextMenuEventArgs eventArgs)
    {
        if (ResultsList.SelectedItem is null) eventArgs.Handled = true;
    }

    private void OnContextOpenClick(object sender, RoutedEventArgs eventArgs) => AcceptSelection();

    private void OnContextCopyNameClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ResultsList.SelectedItem is SymbolSearchResultItem selected)
            ShowNotice(ResultActions.Copy(selected.Name, "이름을 복사했습니다."));
    }

    private void OnContextCopyPathClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ResultsList.SelectedItem is SymbolSearchResultItem selected)
            ShowNotice(ResultActions.Copy(selected.FullPath, "전체 경로를 복사했습니다."));
    }

    private void OnContextShowInExplorerClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ResultsList.SelectedItem is SymbolSearchResultItem selected && ResultActions.ShowInExplorer(selected.FullPath) is { } failure)
            ShowNotice(failure);
    }

    /// <summary>복사 결과처럼 짧은 알림은 다음 검색·상태 갱신 때까지 상태 줄에 둡니다.</summary>
    private void ShowNotice(string message)
    {
        StatusText.Text = message;
        StatusText.ToolTip = null;
    }

    private void AcceptSelection()
    {
        if (ResultsList.SelectedItem is not SymbolSearchResultItem selected)
        {
            return;
        }

        SelectedLocation = selected.Location;
        DialogResult = true;
    }

    private static bool SameSolution(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

internal sealed class SymbolSearchResultItem
{
    public SymbolSearchResultItem(
        SourceSymbolMatch match,
        IReadOnlyList<SolutionProjectInfo> projects,
        string query)
    {
        Location = match.Location;
        SearchQuery = query;
        Name = Location.Name;
        Kind = GetKindText(Location.Kind);
        FileName = Path.GetFileName(Location.Path);
        ProjectName = ProjectNameResolver.Resolve(Location.Path, projects);
        FullPath = Location.Path;
        Line = Location.Line.ToString();
    }

    public SourceSymbolLocation Location { get; }

    public string Name { get; }

    public string SearchQuery { get; }

    public string Kind { get; }

    public string FileName { get; }

    public string ProjectName { get; }

    public string FullPath { get; }

    public string Line { get; }

    private static string GetKindText(SourceSymbolKind kind) => kind switch
    {
        SourceSymbolKind.Namespace => "namespace",
        SourceSymbolKind.Type => "type",
        SourceSymbolKind.Class => "class",
        SourceSymbolKind.Struct => "struct",
        SourceSymbolKind.Union => "union",
        SourceSymbolKind.Enum => "enum",
        SourceSymbolKind.Function => "function",
        SourceSymbolKind.Variable => "variable",
        SourceSymbolKind.Macro => "macro",
        _ => "unknown",
    };
}
