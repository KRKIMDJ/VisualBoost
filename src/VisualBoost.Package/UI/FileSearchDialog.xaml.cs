using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using VisualBoost.Core.Searching;
using VisualBoost.Options;
using VisualBoost.Services;

namespace VisualBoost.UI;

public partial class FileSearchDialog : DialogWindow
{
    private readonly SolutionFileIndexService fileIndex;
    private readonly FileSearchOptionsPage options;
    private readonly string? preferredRoot;
    private readonly string? solutionRoot;
    private readonly HashSet<string> openFiles;
    private readonly IReadOnlyList<SolutionProjectInfo> projects;
    private readonly IReadOnlyList<string>? candidatePaths;
    private readonly DispatcherTimer statusTimer;
    private CancellationTokenSource? searchCancellation;
    private FileSearchScope scope;
    private int displayedResultCount;
    private bool isSearching;
    private string? statusNotice;
    private DateTime statusNoticeUntil;

    internal FileSearchDialog(
        SolutionFileIndexService fileIndex,
        FileSearchOptionsPage options,
        string? preferredRoot,
        string? solutionRoot,
        IReadOnlyCollection<string> openFiles,
        IReadOnlyList<SolutionProjectInfo> projects,
        IReadOnlyList<string>? candidatePaths = null)
    {
        this.fileIndex = fileIndex ?? throw new ArgumentNullException(nameof(fileIndex));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.preferredRoot = preferredRoot;
        this.solutionRoot = solutionRoot;
        this.openFiles = new HashSet<string>(
            openFiles ?? throw new ArgumentNullException(nameof(openFiles)),
            StringComparer.OrdinalIgnoreCase);
        this.projects = projects ?? throw new ArgumentNullException(nameof(projects));
        this.candidatePaths = candidatePaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        scope = ParseScope(options.FileSearchScope);

        InitializeComponent();
        ConfigureScopeButtons();
        ConfigureMode();
        RestoreWindowPlacement();

        statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        statusTimer.Tick += OnStatusTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public string? SelectedPath { get; private set; }

    [SuppressMessage(
        "Usage",
        "VSTHRD100:Avoid async void methods",
        Justification = "WPF 이벤트 시그니처이며 검색 취소와 예외를 메서드 내부에서 처리합니다.")]
    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        UpdateSearchControls();
        statusTimer.Start();
        await RefreshResultsAsync(useDebounce: false);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        statusTimer.Stop();
        statusTimer.Tick -= OnStatusTimerTick;
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
        searchCancellation = null;
        SaveWindowPlacement();
    }

    [SuppressMessage(
        "Usage",
        "VSTHRD100:Avoid async void methods",
        Justification = "WPF 이벤트 시그니처이며 검색 취소와 예외를 메서드 내부에서 처리합니다.")]
    private async void OnSearchTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        UpdateSearchControls();
        await RefreshResultsAsync(useDebounce: true);
    }

    private void OnSearchFocusChanged(object sender, RoutedEventArgs eventArgs) => UpdateSearchControls();

    private void OnClearSearchClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SearchBox.Text.Length == 0)
        {
            SearchBox.Focus();
            return;
        }

        SearchBox.Clear();
        SearchBox.Focus();
    }

    [SuppressMessage(
        "Usage",
        "VSTHRD100:Avoid async void methods",
        Justification = "WPF 이벤트 시그니처이며 검색 취소와 예외를 메서드 내부에서 처리합니다.")]
    private async void OnScopeClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not RadioButton button ||
            button.Tag is not string value ||
            !Enum.TryParse(value, ignoreCase: false, out FileSearchScope selectedScope))
        {
            return;
        }

        scope = selectedScope;
        await RefreshResultsAsync(useDebounce: false);
        SearchBox.Focus();
    }

    private async Task RefreshResultsAsync(bool useDebounce)
    {
        var previousCancellation = searchCancellation;
        var currentCancellation = new CancellationTokenSource();
        searchCancellation = currentCancellation;
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();

        var query = SearchBox.Text;
        var selectedScope = scope;
        var cancellationToken = currentCancellation.Token;
        isSearching = true;
        statusNotice = null;
        UpdateStatus();
        UpdateEmptyState(query, isLoading: true);

        try
        {
            var inputDelay = options.GetInputDelayMilliseconds();
            if (useDebounce && !string.IsNullOrWhiteSpace(query) && inputDelay > 0)
            {
                await Task.Delay(inputDelay, cancellationToken);
            }

            var maximumResults = options.GetMaximumResults();
            var showSuggestions = options.ShowSuggestionsForEmptyQuery;
            var matches = await Task.Run(
                () => GetMatches(
                    query,
                    maximumResults,
                    showSuggestions,
                    selectedScope,
                    cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var items = CreateItems(matches, query);
            ResultsList.ItemsSource = items;
            ResultsList.SelectedIndex = items.Count > 0 ? 0 : -1;
            displayedResultCount = items.Count;
            UpdateEmptyState(query, isLoading: false);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(searchCancellation, currentCancellation) && displayedResultCount == 0)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                EmptyStateTitle.Text = "검색을 취소했습니다.";
                EmptyStateDescription.Text = "검색어를 변경하면 다시 검색합니다.";
            }
        }
        catch (Exception exception)
        {
            ResultsList.ItemsSource = null;
            displayedResultCount = 0;
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateTitle.Text = "파일 검색을 완료하지 못했습니다.";
            EmptyStateDescription.Text = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(searchCancellation, currentCancellation))
            {
                isSearching = false;
                UpdateStatus();
            }
        }
    }

    private void OnStatusTimerTick(object? sender, EventArgs eventArgs) => UpdateStatus();

    private void UpdateStatus()
    {
        var snapshot = fileIndex.GetSnapshot();
        if (statusNotice is not null && DateTime.UtcNow >= statusNoticeUntil)
        {
            statusNotice = null;
        }

        var stateText = statusNotice ?? (isSearching
            ? "검색 중"
            : candidatePaths is not null
                ? "대응 파일 선택"
                : snapshot.State switch
                {
                    SolutionFileIndexState.Building => "인덱싱 중",
                    SolutionFileIndexState.Ready when snapshot.IsAnalyzing => "소스 분석 중",
                    SolutionFileIndexState.Ready => "준비됨",
                    SolutionFileIndexState.Faulted => "인덱스 오류",
                    _ => "인덱스 없음",
                });
        StatusText.Text = candidatePaths is null
            ? $"{displayedResultCount:N0}개 결과 · {snapshot.FileCount:N0}개 인덱싱 · {stateText}"
            : $"{displayedResultCount:N0}개 후보 · {stateText}";
        CancelSearchButton.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;
        KeyboardHintText.Text = isSearching
            ? "Esc 검색 취소"
            : "↑↓ 선택   Enter 열기   Esc 닫기";
    }

    private void SetStatusNotice(string message)
    {
        statusNotice = message;
        statusNoticeUntil = DateTime.UtcNow.AddSeconds(2);
        UpdateStatus();
    }

    private void UpdateSearchControls()
    {
        var isEmpty = string.IsNullOrEmpty(SearchBox.Text);
        SearchPlaceholder.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = isEmpty ? Visibility.Hidden : Visibility.Visible;
    }

    private void UpdateEmptyState(string query, bool isLoading)
    {
        if (displayedResultCount > 0 && !isLoading)
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Visible;
        if (isLoading)
        {
            EmptyStateTitle.Text = "파일을 찾는 중입니다.";
            EmptyStateDescription.Text = "현재 인덱스에서 후보를 준비하고 있습니다.";
        }
        else if (string.IsNullOrWhiteSpace(query))
        {
            EmptyStateTitle.Text = "표시할 파일이 없습니다.";
            EmptyStateDescription.Text = "다른 검색 범위를 선택하거나 검색어를 입력하세요.";
        }
        else
        {
            EmptyStateTitle.Text = "일치하는 파일이 없습니다.";
            EmptyStateDescription.Text = "검색어 또는 검색 범위를 변경해 보세요.";
        }
    }

    private void ConfigureScopeButtons()
    {
        CurrentProjectScopeButton.IsEnabled = !string.IsNullOrWhiteSpace(preferredRoot);
        OpenFilesScopeButton.IsEnabled = openFiles.Count > 0;
        ExternalSourcesScopeButton.IsEnabled =
            !string.IsNullOrWhiteSpace(solutionRoot) || !string.IsNullOrWhiteSpace(preferredRoot);

        if (!IsScopeAvailable(scope))
        {
            scope = FileSearchScope.All;
        }

        AllScopeButton.IsChecked = scope == FileSearchScope.All;
        CurrentProjectScopeButton.IsChecked = scope == FileSearchScope.CurrentProject;
        OpenFilesScopeButton.IsChecked = scope == FileSearchScope.OpenFiles;
        ExternalSourcesScopeButton.IsChecked = scope == FileSearchScope.ExternalSources;
    }

    private void ConfigureMode()
    {
        if (candidatePaths is null)
        {
            return;
        }

        Title = "VisualBoost 대응 파일 선택";
        ScopePanel.Visibility = Visibility.Collapsed;
        SearchPlaceholder.Text = "대응 파일 후보 검색";
    }

    private IReadOnlyList<FileSearchMatch> SearchCandidatePaths(
        string query,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (candidatePaths is null)
        {
            return Array.Empty<FileSearchMatch>();
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            return FuzzyFileSearch.Search(
                query,
                candidatePaths,
                maximumResults,
                cancellationToken);
        }

        var results = new List<FileSearchMatch>(Math.Min(candidatePaths.Count, maximumResults));
        for (var index = 0; index < candidatePaths.Count && index < maximumResults; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(new FileSearchMatch(candidatePaths[index], candidatePaths.Count - index));
        }

        return results;
    }

    private IReadOnlyList<FileSearchMatch> GetMatches(
        string query,
        int maximumResults,
        bool showSuggestions,
        FileSearchScope selectedScope,
        CancellationToken cancellationToken)
    {
        if (candidatePaths is not null)
        {
            return SearchCandidatePaths(query, maximumResults, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return showSuggestions
                ? fileIndex.GetSuggestions(
                    maximumResults,
                    preferredRoot,
                    solutionRoot,
                    selectedScope,
                    openFiles,
                    cancellationToken)
                : Array.Empty<FileSearchMatch>();
        }

        return fileIndex.Search(
            query,
            maximumResults,
            preferredRoot,
            solutionRoot,
            selectedScope,
            openFiles,
            cancellationToken);
    }

    private bool IsScopeAvailable(FileSearchScope candidate) => candidate switch
    {
        FileSearchScope.CurrentProject => CurrentProjectScopeButton.IsEnabled,
        FileSearchScope.OpenFiles => OpenFilesScopeButton.IsEnabled,
        FileSearchScope.ExternalSources => ExternalSourcesScopeButton.IsEnabled,
        _ => true,
    };

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            if (CancelActiveSearch())
            {
                eventArgs.Handled = true;
                return;
            }

            DialogResult = false;
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

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs eventArgs) => AcceptSelection();

    private void OnCancelSearchClick(object sender, RoutedEventArgs eventArgs) => CancelActiveSearch();

    private bool CancelActiveSearch()
    {
        if (!isSearching)
        {
            return false;
        }

        searchCancellation?.Cancel();
        isSearching = false;
        SetStatusNotice("검색 취소됨");
        if (displayedResultCount > 0)
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }

        return true;
    }

    private void OnResultsPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        var item = ItemsControl.ContainerFromElement(
            ResultsList,
            eventArgs.OriginalSource as DependencyObject) as ListBoxItem;
        if (item is null)
        {
            ResultsList.SelectedItem = null;
            return;
        }

        item.IsSelected = true;
        ResultsList.Focus();
    }

    private void OnResultsContextMenuOpening(object sender, ContextMenuEventArgs eventArgs)
    {
        if (ResultsList.SelectedItem is null)
        {
            eventArgs.Handled = true;
        }
    }

    private void OnContextOpenClick(object sender, RoutedEventArgs eventArgs) => AcceptSelection();

    private void OnContextCopyPathClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ResultsList.SelectedItem is not FileSearchResultItem selected)
        {
            return;
        }

        try
        {
            Clipboard.SetText(selected.FullPath);
            SetStatusNotice("전체 경로를 복사했습니다.");
        }
        catch (ExternalException)
        {
            SetStatusNotice("클립보드를 사용할 수 없습니다.");
        }
    }

    private void OnContextShowInExplorerClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ResultsList.SelectedItem is not FileSearchResultItem selected)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{selected.FullPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (
            exception is Win32Exception ||
            exception is InvalidOperationException)
        {
            SetStatusNotice("탐색기를 열 수 없습니다.");
        }
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
        if (ResultsList.SelectedItem is not FileSearchResultItem selected)
        {
            return;
        }

        SelectedPath = selected.FullPath;
        DialogResult = true;
    }

    private IReadOnlyList<FileSearchResultItem> CreateItems(
        IReadOnlyList<FileSearchMatch> matches,
        string query) =>
        matches
            .Select(match => new FileSearchResultItem(
                match.Path,
                query,
                preferredRoot,
                solutionRoot,
                projects))
            .ToArray();

    private void RestoreWindowPlacement()
    {
        if (IsFinite(options.FileSearchWidth) && options.FileSearchWidth >= MinWidth)
        {
            Width = options.FileSearchWidth;
        }

        if (IsFinite(options.FileSearchHeight) && options.FileSearchHeight >= MinHeight)
        {
            Height = options.FileSearchHeight;
        }

        if (!options.FileSearchPlacementSaved ||
            !IsFinite(options.FileSearchLeft) ||
            !IsFinite(options.FileSearchTop))
        {
            return;
        }

        var savedBounds = new Rect(options.FileSearchLeft, options.FileSearchTop, Width, Height);
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        if (!savedBounds.IntersectsWith(virtualScreen))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = options.FileSearchLeft;
        Top = options.FileSearchTop;
    }

    private void SaveWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (IsFinite(bounds.Width) && bounds.Width >= MinWidth &&
            IsFinite(bounds.Height) && bounds.Height >= MinHeight)
        {
            options.FileSearchWidth = bounds.Width;
            options.FileSearchHeight = bounds.Height;
            options.FileSearchLeft = bounds.Left;
            options.FileSearchTop = bounds.Top;
            options.FileSearchPlacementSaved = true;
        }

        if (candidatePaths is null)
        {
            options.FileSearchScope = scope.ToString();
        }
        options.SaveSettingsToStorage();
    }

    private static FileSearchScope ParseScope(string? value) =>
        Enum.TryParse(value, ignoreCase: false, out FileSearchScope parsed)
            ? parsed
            : FileSearchScope.All;

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

internal sealed class FileSearchResultItem
{
    public FileSearchResultItem(
        string fullPath,
        string query,
        string? preferredRoot,
        string? solutionRoot,
        IReadOnlyList<SolutionProjectInfo> projects)
    {
        FullPath = fullPath;
        var fileName = Path.GetFileName(fullPath);
        var displayPath = CreateDisplayPath(fullPath, preferredRoot, solutionRoot);
        if (TryCreateIconMoniker(fullPath, out var iconMoniker))
        {
            IconMoniker = iconMoniker;
            IconVisibility = Visibility.Visible;
            FallbackIconVisibility = Visibility.Collapsed;
        }
        else
        {
            IconText = CreateIconText(fullPath);
            IconVisibility = Visibility.Collapsed;
            FallbackIconVisibility = Visibility.Visible;
        }

        ProjectName = ResolveProjectName(fullPath, projects);
        FileNameSegments = FileSearchTextSegment.Create(fileName, query);
        PathSegments = FileSearchTextSegment.Create(displayPath, query);
    }

    public string FullPath { get; }

    public string IconText { get; } = string.Empty;

    public ImageMoniker IconMoniker { get; }

    public Visibility IconVisibility { get; }

    public Visibility FallbackIconVisibility { get; }

    public string ProjectName { get; }

    public IReadOnlyList<FileSearchTextSegment> FileNameSegments { get; }

    public IReadOnlyList<FileSearchTextSegment> PathSegments { get; }

    private static string CreateDisplayPath(string fullPath, string? preferredRoot, string? solutionRoot)
    {
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var relativeRoot = FileSearchScopeFilter.IsInside(fullPath, solutionRoot)
            ? solutionRoot
            : FileSearchScopeFilter.IsInside(fullPath, preferredRoot)
                ? preferredRoot
                : null;
        if (!string.IsNullOrWhiteSpace(relativeRoot))
        {
            return MakeRelativePath(relativeRoot!, directory);
        }

        var normalized = directory.Replace('/', '\\');
        var engineSourceOffset = normalized.IndexOf("\\Engine\\Source\\", StringComparison.OrdinalIgnoreCase);
        return engineSourceOffset >= 0
            ? normalized.Substring(engineSourceOffset + 1)
            : directory;
    }

    private static string ResolveProjectName(
        string fullPath,
        IReadOnlyList<SolutionProjectInfo> projects)
    {
        foreach (var project in projects)
        {
            if (FileSearchScopeFilter.IsInside(fullPath, project.RootPath))
            {
                return project.Name;
            }
        }

        return TryInferSourceModule(fullPath, out var moduleName) ? moduleName : "—";
    }

    private static bool TryInferSourceModule(string fullPath, out string moduleName)
    {
        moduleName = string.Empty;
        var segments = fullPath
            .Replace('/', '\\')
            .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (var index = segments.Length - 2; index >= 0; index--)
        {
            if (!string.Equals(segments[index], "Source", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidateOffset = index + 1;
            if (candidateOffset >= segments.Length - 1)
            {
                return false;
            }

            if (IsSourceCategory(segments[candidateOffset]) && candidateOffset + 1 < segments.Length - 1)
            {
                candidateOffset++;
            }

            moduleName = segments[candidateOffset];
            return moduleName.Length > 0;
        }

        return false;
    }

    private static bool IsSourceCategory(string value) =>
        string.Equals(value, "Runtime", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Editor", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Developer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Programs", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "ThirdParty", StringComparison.OrdinalIgnoreCase);

    private static bool TryCreateIconMoniker(string path, out ImageMoniker moniker)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".h":
            case ".hh":
            case ".hpp":
            case ".hxx":
            case ".inl":
                moniker = KnownMonikers.CPPHeaderFile;
                return true;
            case ".c":
                moniker = KnownMonikers.CFile;
                return true;
            case ".cc":
            case ".cpp":
            case ".cxx":
                moniker = KnownMonikers.CPPSourceFile;
                return true;
            case ".cs":
                moniker = KnownMonikers.CSSourceFile;
                return true;
            case ".fs":
            case ".fsx":
                moniker = KnownMonikers.FSCodeFile;
                return true;
            case ".py":
                moniker = KnownMonikers.PYSourceFile;
                return true;
            case ".js":
                moniker = KnownMonikers.JSScript;
                return true;
            case ".jsx":
                moniker = KnownMonikers.JSXScript;
                return true;
            case ".ts":
            case ".tsx":
                moniker = KnownMonikers.TSSourceFile;
                return true;
            case ".java":
            case ".kt":
                moniker = KnownMonikers.JavaSource;
                return true;
            case ".md":
            case ".markdown":
                moniker = KnownMonikers.MarkdownFile;
                return true;
            case ".json":
                moniker = KnownMonikers.JSONScript;
                return true;
            case ".xml":
            case ".xaml":
                moniker = KnownMonikers.XMLFile;
                return true;
            case ".yml":
            case ".yaml":
                moniker = KnownMonikers.YamlFile;
                return true;
            case ".html":
            case ".htm":
                moniker = KnownMonikers.HTMLFile;
                return true;
            case ".ps1":
            case ".psm1":
                moniker = KnownMonikers.PowershellFile;
                return true;
            case ".config":
            case ".props":
            case ".targets":
                moniker = KnownMonikers.ConfigurationFile;
                return true;
            case ".txt":
                moniker = KnownMonikers.TextFile;
                return true;
            default:
                moniker = default;
                return false;
        }
    }

    private static string CreateIconText(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".h" or ".hh" or ".hpp" or ".hxx" or ".inl" => "H",
            ".c" or ".cc" or ".cpp" or ".cxx" => "C++",
            ".cs" => "C#",
            ".fs" or ".fsx" => "F#",
            ".py" => "PY",
            ".js" or ".jsx" => "JS",
            ".ts" or ".tsx" => "TS",
            ".rs" => "RS",
            ".go" => "GO",
            ".java" or ".kt" => "JVM",
            _ => extension.Length > 1 ? extension.Substring(1).ToUpperInvariant() : "FILE",
        };
    }

    private static string MakeRelativePath(string root, string directory)
    {
        try
        {
            var rootUri = new Uri(AppendDirectorySeparator(root));
            var directoryUri = new Uri(AppendDirectorySeparator(directory));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(directoryUri).ToString())
                .Replace('/', '\\')
                .TrimEnd('\\');
        }
        catch (UriFormatException)
        {
            return directory;
        }
    }

    private static string AppendDirectorySeparator(string path) =>
        path.EndsWith("\\", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal)
            ? path
            : path + Path.DirectorySeparatorChar;
}

internal sealed class FileSearchTextSegment
{
    public FileSearchTextSegment(string text, bool isMatch)
    {
        Text = text;
        IsMatch = isMatch;
    }

    public string Text { get; }

    public bool IsMatch { get; }

    public static IReadOnlyList<FileSearchTextSegment> Create(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query))
        {
            return new[] { new FileSearchTextSegment(text, isMatch: false) };
        }

        var matched = new bool[text.Length];
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var positions = FindPositions(text, token);
            if (positions is null)
            {
                continue;
            }

            foreach (var position in positions)
            {
                matched[position] = true;
            }
        }

        var segments = new List<FileSearchTextSegment>();
        var segmentStart = 0;
        for (var index = 1; index <= text.Length; index++)
        {
            if (index < text.Length && matched[index] == matched[segmentStart])
            {
                continue;
            }

            segments.Add(new FileSearchTextSegment(
                text.Substring(segmentStart, index - segmentStart),
                matched[segmentStart]));
            segmentStart = index;
        }

        return segments;
    }

    private static IReadOnlyList<int>? FindPositions(string text, string token)
    {
        var positions = new List<int>(token.Length);
        var searchOffset = 0;
        foreach (var expected in token)
        {
            var found = -1;
            for (var index = searchOffset; index < text.Length; index++)
            {
                if (AreEquivalent(text[index], expected))
                {
                    found = index;
                    break;
                }
            }

            if (found < 0)
            {
                return null;
            }

            positions.Add(found);
            searchOffset = found + 1;
        }

        return positions;
    }

    private static bool AreEquivalent(char first, char second) =>
        (IsDirectorySeparator(first) && IsDirectorySeparator(second)) ||
        char.ToUpperInvariant(first) == char.ToUpperInvariant(second);

    private static bool IsDirectorySeparator(char value) => value == '\\' || value == '/';
}
