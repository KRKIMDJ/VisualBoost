using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using VisualBoost.Core.Searching;
using VisualBoost.Services;

namespace VisualBoost.UI;

public partial class FileSearchDialog : DialogWindow
{
    private readonly SolutionFileIndexService fileIndex;
    private CancellationTokenSource? searchCancellation;

    internal FileSearchDialog(SolutionFileIndexService fileIndex)
    {
        this.fileIndex = fileIndex ?? throw new ArgumentNullException(nameof(fileIndex));
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public string? SelectedPath { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
        searchCancellation = null;
    }

    [SuppressMessage(
        "Usage",
        "VSTHRD100:Avoid async void methods",
        Justification = "WPF 이벤트 시그니처이며 취소와 검색 예외를 메서드 내부에서 처리합니다.")]
    private async void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs eventArgs)
    {
        var previousCancellation = searchCancellation;
        searchCancellation = new CancellationTokenSource();
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();

        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            ResultsList.ItemsSource = null;
            ResultCountText.Text = string.Empty;
            HintText.Text = "파일명 또는 경로를 입력하세요.  Enter: 열기  Esc: 닫기";
            return;
        }

        var cancellationToken = searchCancellation.Token;
        try
        {
            await Task.Delay(80, cancellationToken);
            var matches = await Task.Run(
                () => fileIndex.Search(query, 100, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var items = CreateItems(matches);
            ResultsList.ItemsSource = items;
            ResultsList.SelectedIndex = items.Count > 0 ? 0 : -1;
            ResultCountText.Text = $"{items.Count:N0}개";
            HintText.Text = items.Count > 0
                ? "↑/↓: 선택  Enter: 열기  Esc: 닫기"
                : "일치하는 파일이 없습니다.";
        }
        catch (OperationCanceledException)
        {
            // 새 입력이 도착하면 이전 검색 결과를 UI에 반영하지 않습니다.
        }
        catch (Exception exception)
        {
            ResultsList.ItemsSource = null;
            ResultCountText.Text = string.Empty;
            HintText.Text = $"검색 실패: {exception.Message}";
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
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

    private static IReadOnlyList<FileSearchResultItem> CreateItems(
        IReadOnlyList<FileSearchMatch> matches) =>
        matches
            .Select(match => new FileSearchResultItem(match.Path))
            .ToArray();
}

internal sealed class FileSearchResultItem
{
    public FileSearchResultItem(string fullPath)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        DirectoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
    }

    public string FullPath { get; }

    public string FileName { get; }

    public string DirectoryPath { get; }
}
