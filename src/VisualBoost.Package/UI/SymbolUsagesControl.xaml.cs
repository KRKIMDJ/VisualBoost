using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisualBoost.Core.Analysis;
using VisualBoost.Services;

namespace VisualBoost.UI;

public partial class SymbolUsagesControl : UserControl
{
    private const int MaximumResults = 5_000;
    private SolutionFileIndexService? fileIndex;
    private ISymbolUsageProvider? provider;
    private Action<SourceUsageLocation>? openLocation;
    private IReadOnlyList<SolutionProjectInfo> projects = Array.Empty<SolutionProjectInfo>();
    private string symbol = string.Empty;
    private string? sourceProjectFile;
    private CancellationTokenSource? searchCancellation;
    private SymbolUsageScope scope;
    private bool suppressScopeChange;
    private bool isSearching;

    public SymbolUsagesControl()
    {
        InitializeComponent();
        Unloaded += (_, __) => CancelSearch();
    }

    internal void StartSearch(
        SolutionFileIndexService fileIndex,
        ISymbolUsageProvider provider,
        string symbol,
        string? sourceProjectFile,
        IReadOnlyList<SolutionProjectInfo> projects,
        Action<SourceUsageLocation> openLocation,
        string? sourceProjectName = null)
    {
        this.fileIndex = fileIndex ?? throw new ArgumentNullException(nameof(fileIndex));
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        this.sourceProjectFile = sourceProjectFile;
        this.projects = projects ?? throw new ArgumentNullException(nameof(projects));
        this.openLocation = openLocation ?? throw new ArgumentNullException(nameof(openLocation));
        scope = string.IsNullOrWhiteSpace(sourceProjectFile)
            ? SymbolUsageScope.EntireSolution
            : SymbolUsageScope.CurrentProject;

        SymbolText.Text = symbol;
        CurrentProjectScopeItem.ToolTip = string.IsNullOrWhiteSpace(sourceProjectFile)
            ? "실행 문서의 소속 프로젝트를 확인할 수 없습니다."
            : "실행 문서의 프로젝트: " + (sourceProjectName ?? Path.GetFileNameWithoutExtension(sourceProjectFile));
        suppressScopeChange = true;
        CurrentProjectScopeItem.IsEnabled = !string.IsNullOrWhiteSpace(sourceProjectFile);
        ScopeSelector.SelectedValue = scope.ToString();
        suppressScopeChange = false;
        _ = RefreshResultsAsync();
    }

    [SuppressMessage(
        "Usage",
        "VSTHRD100:Avoid async void methods",
        Justification = "WPF 이벤트 시그니처이며 검색 취소와 예외를 메서드 내부에서 처리합니다.")]
    private async void OnScopeChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (suppressScopeChange || provider is null ||
            ScopeSelector.SelectedValue is not string value ||
            !Enum.TryParse(value, ignoreCase: false, out SymbolUsageScope selectedScope))
        {
            return;
        }

        scope = selectedScope;
        await RefreshResultsAsync();
    }

    private async Task RefreshResultsAsync()
    {
        if (fileIndex is null || provider is null)
        {
            return;
        }

        var previousCancellation = searchCancellation;
        var currentCancellation = new CancellationTokenSource();
        searchCancellation = currentCancellation;
        previousCancellation?.Cancel();

        var cancellationToken = currentCancellation.Token;
        var currentIndex = fileIndex;
        var currentProvider = provider;
        var currentSymbol = symbol;
        var currentProjectFile = sourceProjectFile;
        var currentProjects = projects;
        var selectedScope = scope;
        var watch = Stopwatch.StartNew();
        isSearching = true;
        CancelSearchButton.Visibility = Visibility.Visible;
        ResultsList.ItemsSource = null;
        EmptyStatePanel.Visibility = Visibility.Visible;
        EmptyStateTitle.Text = "사용처를 찾는 중입니다.";
        EmptyStateDescription.Text = selectedScope == SymbolUsageScope.CurrentProject
            ? "현재 프로젝트의 C++ 파일을 확인하고 있습니다."
            : "전체 솔루션의 C++ 파일을 확인하고 있습니다.";
        StatusText.Text = "검색 중";

        try
        {
            var paths = currentIndex.GetFilePathsSnapshot();
            if (paths.Count == 0 && currentIndex.GetSnapshot().State == SolutionFileIndexState.Building)
            {
                EmptyStateTitle.Text = "파일 인덱싱을 기다리는 중입니다.";
                EmptyStateDescription.Text = "인덱싱이 끝나면 자동으로 검색합니다.";
                await currentIndex.WaitUntilReadyAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                paths = currentIndex.GetFilePathsSnapshot();
            }

            var items = await Task.Run(() =>
            {
                var matches = currentProvider.FindUsages(
                    currentSymbol,
                    paths,
                    currentProjectFile,
                    selectedScope,
                    MaximumResults,
                    cancellationToken);
                // 조회와 표시용 구간 생성도 UI 밖에서 수행하며 같은 이름은 요청당 한 번만 조회합니다.
                var resolver = new SymbolKindResolver(currentIndex.FindSymbol, cancellationToken);
                var results = new List<SymbolUsageResultItem>(matches.Count);
                foreach (var match in matches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(new SymbolUsageResultItem(match, currentProjects, resolver.Resolve));
                }
                return results;
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            watch.Stop();

            ResultsList.ItemsSource = items;
            ResultsList.SelectedIndex = items.Count > 0 ? 0 : -1;
            EmptyStatePanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (items.Count == 0)
            {
                EmptyStateTitle.Text = "사용처를 찾지 못했습니다.";
                EmptyStateDescription.Text = "검색 범위를 변경하거나 인덱싱 상태를 확인하세요.";
            }

            var truncatedText = items.Count >= MaximumResults ? " · 최대 결과 도달" : string.Empty;
            StatusText.Text = $"{items.Count:N0}개 결과 · {watch.ElapsedMilliseconds:N0} ms{truncatedText}";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(searchCancellation, currentCancellation))
            {
                StatusText.Text = "검색 취소됨";
                EmptyStateTitle.Text = "검색을 취소했습니다.";
                EmptyStateDescription.Text = "범위를 다시 선택하거나 명령을 다시 실행하세요.";
            }
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(searchCancellation, currentCancellation)) return;
            ResultsList.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateTitle.Text = "사용처 검색을 완료하지 못했습니다.";
            EmptyStateDescription.Text = exception.Message;
            StatusText.Text = "검색 오류";
        }
        finally
        {
            if (ReferenceEquals(searchCancellation, currentCancellation))
            {
                isSearching = false;
                CancelSearchButton.Visibility = Visibility.Collapsed;
            }
            if (ReferenceEquals(searchCancellation, currentCancellation)) searchCancellation = null;
            currentCancellation.Dispose();
        }
    }

    internal void CancelSearch()
    {
        searchCancellation?.Cancel();
    }

    private void OnCancelSearchClick(object sender, RoutedEventArgs eventArgs) => searchCancellation?.Cancel();

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape && isSearching)
        {
            searchCancellation?.Cancel();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Enter && ResultsList.IsKeyboardFocusWithin)
        {
            OpenSelection();
            eventArgs.Handled = true;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (ItemsControl.ContainerFromElement(ResultsList, eventArgs.OriginalSource as DependencyObject) is ListBoxItem)
            OpenSelection();
    }

    private void OpenSelection()
    {
        if (ResultsList.SelectedItem is SymbolUsageResultItem selected)
        {
            openLocation?.Invoke(selected.Location);
        }
    }
}

internal sealed class SymbolUsageResultItem
{
    public SymbolUsageResultItem(
        SourceUsageLocation location,
        IReadOnlyList<SolutionProjectInfo> projects,
        Func<string, SourceSymbolKind>? resolveKind = null)
    {
        Location = location;
        FileName = Path.GetFileName(location.Path);
        DirectoryPath = Path.GetDirectoryName(location.Path) ?? string.Empty;
        ProjectName = ProjectNameResolver.Resolve(location.Path, projects);
        Line = location.Line.ToString();
        CodeSegments = UsageTextSegment.Create(location, resolveKind ?? (_ => SourceSymbolKind.Unknown));
        FullPath = location.Path;
    }

    public SourceUsageLocation Location { get; }

    public string FileName { get; }

    public string DirectoryPath { get; }

    public string ProjectName { get; }

    public string Line { get; }

    public IReadOnlyList<UsageTextSegment> CodeSegments { get; }

    public string FullPath { get; }
}

internal sealed class UsageTextSegment
{
    private UsageTextSegment(string text, bool isMatch, SourceSymbolKind kind = SourceSymbolKind.Unknown)
    {
        Text = text;
        IsMatch = isMatch;
        Kind = kind;
    }

    public string Text { get; }

    public bool IsMatch { get; }

    public SourceSymbolKind Kind { get; }

    public static IReadOnlyList<UsageTextSegment> Create(SourceUsageLocation location, Func<string, SourceSymbolKind> resolveKind)
    {
        if (location.Identifiers.Count == 0) return Create(location.LineText, location.Symbol);
        var result = new List<UsageTextSegment>();
        var offset = 0;
        foreach (var span in location.Identifiers)
        {
            if (span.Start < offset || span.Length <= 0 || span.Start > location.LineText.Length - span.Length) continue;
            if (span.Start > offset) result.Add(new UsageTextSegment(location.LineText.Substring(offset, span.Start - offset), false));
            var text = location.LineText.Substring(span.Start, span.Length);
            result.Add(new UsageTextSegment(text, text == location.Symbol, resolveKind(text)));
            offset = span.Start + span.Length;
        }
        if (offset < location.LineText.Length) result.Add(new UsageTextSegment(location.LineText.Substring(offset), false));
        return result;
    }

    public static IReadOnlyList<UsageTextSegment> Create(string text, string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return new[] { new UsageTextSegment(text, false) };
        var segments = new List<UsageTextSegment>();
        var offset = 0;
        while (offset < text.Length)
        {
            var match = FindNextIdentifierOccurrence(text, symbol, offset);
            if (match < 0)
            {
                segments.Add(new UsageTextSegment(text.Substring(offset), isMatch: false));
                break;
            }

            if (match > offset)
            {
                segments.Add(new UsageTextSegment(text.Substring(offset, match - offset), isMatch: false));
            }

            segments.Add(new UsageTextSegment(symbol, isMatch: true));
            offset = match + symbol.Length;
        }

        return segments.Count == 0
            ? new[] { new UsageTextSegment(text, isMatch: false) }
            : segments;
    }

    private static int FindNextIdentifierOccurrence(string text, string symbol, int startIndex)
    {
        var offset = startIndex;
        while (offset < text.Length)
        {
            var match = text.IndexOf(symbol, offset, StringComparison.Ordinal);
            if (match < 0)
            {
                return -1;
            }

            var beforeIsIdentifier = match > 0 && IsIdentifierPart(text[match - 1]);
            var afterOffset = match + symbol.Length;
            var afterIsIdentifier = afterOffset < text.Length && IsIdentifierPart(text[afterOffset]);
            if (!beforeIsIdentifier && !afterIsIdentifier)
            {
                return match;
            }

            offset = match + symbol.Length;
        }

        return -1;
    }

    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);
}
