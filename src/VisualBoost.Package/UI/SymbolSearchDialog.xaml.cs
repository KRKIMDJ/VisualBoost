using System;
using System.Collections.Generic;
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
using VisualBoost.Services;

namespace VisualBoost.UI;

public partial class SymbolSearchDialog : DialogWindow
{
    private readonly SolutionFileIndexService fileIndex;
    private readonly IReadOnlyList<SolutionProjectInfo> projects;
    private readonly DispatcherTimer statusTimer;
    private CancellationTokenSource? searchCancellation;
    private bool isSearching;
    private bool isClosed;
    private int observedSymbolCount = -1;
    private bool observedIsAnalyzing;
    private string? observedAnalysisError;

    internal SymbolSearchDialog(
        SolutionFileIndexService fileIndex,
        IReadOnlyList<SolutionProjectInfo> projects)
    {
        this.fileIndex = fileIndex ?? throw new ArgumentNullException(nameof(fileIndex));
        this.projects = projects ?? throw new ArgumentNullException(nameof(projects));
        InitializeComponent();
        statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        statusTimer.Tick += OnStatusTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public SourceSymbolLocation? SelectedLocation { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        var snapshot = fileIndex.GetSnapshot();
        observedSymbolCount = snapshot.SymbolCount;
        observedIsAnalyzing = snapshot.IsAnalyzing;
        UpdateIdleState(snapshot);
        statusTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        isClosed = true;
        statusTimer.Stop();
        statusTimer.Tick -= OnStatusTimerTick;
        searchCancellation?.Cancel();
        searchCancellation = null;
    }

    [SuppressMessage(
        "Usage",
        "VSTHRD100:Avoid async void methods",
        Justification = "WPF 이벤트 시그니처이며 검색 취소와 예외를 메서드 내부에서 처리합니다.")]
    private async void OnSearchTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (!IsLoaded || isClosed) return;
        var isEmpty = string.IsNullOrWhiteSpace(SearchBox.Text);
        SearchPlaceholder.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = isEmpty ? Visibility.Hidden : Visibility.Visible;
        await RefreshResultsAsync(useDebounce: true);
    }

    private async Task RefreshResultsAsync(bool useDebounce)
    {
        if (isClosed) return;
        var previousCancellation = searchCancellation;
        var currentCancellation = new CancellationTokenSource();
        searchCancellation = currentCancellation;
        previousCancellation?.Cancel();

        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            isSearching = false;
            ResultsList.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Visible;
            UpdateIdleState(fileIndex.GetSnapshot());
            searchCancellation = null;
            currentCancellation.Dispose();
            return;
        }

        var cancellationToken = currentCancellation.Token;
        isSearching = true;
        StatusText.Text = "검색 중";
        try
        {
            if (useDebounce)
            {
                await Task.Delay(45, cancellationToken);
            }

            var matches = await Task.Run(
                () => fileIndex.SearchSymbols(query, 200, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var items = matches.Select(match => new SymbolSearchResultItem(match, projects, query)).ToArray();
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
                    : snapshot.AnalysisError ?? "심볼명을 변경해 보세요.";
            }

            var currentSnapshot = fileIndex.GetSnapshot();
            StatusText.Text = $"{items.Length:N0}개 결과 · {currentSnapshot.SymbolCount:N0}개 심볼" +
                              AnalysisStatus(currentSnapshot);
            StatusText.ToolTip = currentSnapshot.AnalysisError;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(searchCancellation, currentCancellation))
            {
                UpdateIdleState(fileIndex.GetSnapshot());
            }
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
        if (isClosed || isSearching) return;
        var snapshot = fileIndex.GetSnapshot();
        if (snapshot.SymbolCount == observedSymbolCount && snapshot.IsAnalyzing == observedIsAnalyzing &&
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
            EmptyStateDescription.Text = snapshot.AnalysisError ?? "심볼 이름만 검색합니다.";
        }

        StatusText.Text = $"0개 결과 · {snapshot.SymbolCount:N0}개 심볼" +
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
            if (isSearching)
            {
                searchCancellation?.Cancel();
            }
            else
            {
                DialogResult = false;
            }

            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Enter)
        {
            AcceptSelection();
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Down)
        {
            MoveSelection(1);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Up)
        {
            MoveSelection(-1);
            eventArgs.Handled = true;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (ItemsControl.ContainerFromElement(ResultsList, eventArgs.OriginalSource as DependencyObject) is ListBoxItem)
            AcceptSelection();
    }

    private void MoveSelection(int offset)
    {
        if (ResultsList.Items.Count == 0)
        {
            return;
        }

        var current = ResultsList.SelectedIndex < 0 ? 0 : ResultsList.SelectedIndex;
        ResultsList.SelectedIndex = Math.Max(0, Math.Min(ResultsList.Items.Count - 1, current + offset));
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
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
