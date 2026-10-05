using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace VisualBoost.UI;

/// <summary>참조 찾기 한 번의 결과입니다. 만든 뒤에는 바꾸지 않습니다.</summary>
internal sealed class ReferenceResultSet
{
    public ReferenceResultSet(string symbol, IReadOnlyList<NavigationResultItem> items, string notes, DateTime createdAt)
    {
        Symbol = symbol ?? string.Empty;
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Notes = notes ?? string.Empty;
        CreatedAt = createdAt;
        FileCount = items.Select(item => item.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    public string Symbol { get; }

    /// <summary>표시 순서대로 정렬된 위치입니다(요청한 파일이 앞).</summary>
    public IReadOnlyList<NavigationResultItem> Items { get; }

    /// <summary>결과 제한·색인 진행처럼 함께 알릴 내용입니다.</summary>
    public string Notes { get; }

    public DateTime CreatedAt { get; }

    public int FileCount { get; }

    /// <summary>최근 결과 목록에 보이는 이름입니다.</summary>
    public string Title => $"{Symbol} · {Items.Count:N0}개 · {CreatedAt.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    public override string ToString() => Title;
}

/// <summary>참조 결과의 파일 머리 행입니다. XAML 형식별 템플릿이 찾을 수 있게 공개합니다.</summary>
public sealed class ReferenceFileRow
{
    internal ReferenceFileRow(NavigationResultItem first, int count, int total, bool isExpanded)
    {
        FullPath = first.FullPath;
        FileName = first.FileName;
        Folder = first.Folder;
        Count = count == total ? count.ToString("N0", CultureInfo.CurrentCulture) : $"{count:N0}/{total:N0}";
        IsExpanded = isExpanded;
    }

    public string FullPath { get; }

    public string FileName { get; }

    public string Folder { get; }

    /// <summary>이 파일의 위치 수입니다. 필터로 일부만 보이면 "보이는 수/전체"입니다.</summary>
    public string Count { get; }

    public bool IsExpanded { get; }

    public string Glyph => IsExpanded ? "▾" : "▸";

    /// <summary>화면 읽기 프로그램이 읽는 이름입니다.</summary>
    public override string ToString() => $"{FileName}, {Count}개 위치";
}

/// <summary>참조 위치 한 줄입니다. 파일별로 묶지 않을 때는 파일 이름을 함께 보입니다.</summary>
public sealed class ReferenceLineRow
{
    internal ReferenceLineRow(NavigationResultItem item, bool showFile)
    {
        Item = item;
        ShowFile = showFile;
    }

    internal NavigationResultItem Item { get; }

    public bool ShowFile { get; }

    public string FullPath => Item.FullPath;

    public string FileName => Item.FileName;

    public string Folder => Item.Folder;

    public string Line => Item.Line;

    public string Before => Item.Before;

    public string Match => Item.Match;

    public string After => Item.After;

    public string Code => Item.Code;

    public string Container => Item.Container;

    /// <summary>일치 구간이 가리키는 심볼의 종류입니다. 모르면 null입니다.</summary>
    public VisualBoost.Core.Analysis.SourceSymbolKind? SymbolKind => Item.SymbolKind;

    /// <summary>화면 읽기 프로그램이 읽는 이름입니다.</summary>
    public override string ToString() => $"{FileName} {Line}줄: {Code}";
}

/// <summary>
/// 도킹 참조 창의 표시 상태입니다. 현재 결과·최근 결과·필터·묶기·접힌 파일로 화면에 그릴 행 목록을 만듭니다.
/// UI thread에서만 씁니다.
/// </summary>
internal sealed class ReferenceResultsModel : INotifyPropertyChanged
{
    /// <summary>최근 결과는 이 수만큼만 남깁니다. 결과 하나가 최대 수천 위치이므로 메모리를 묶어 둡니다.</summary>
    public const int MaxHistory = 10;

    private readonly List<ReferenceResultSet> history = new();
    private readonly HashSet<string> collapsed = new(StringComparer.OrdinalIgnoreCase);
    private ReferenceResultSet? current;
    private string filter = string.Empty;
    private bool groupByFile = true;
    private IReadOnlyList<object> rows = Array.Empty<object>();
    private int visibleCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ReferenceResultSet? Current => current;

    /// <summary>최근 결과입니다. 최신이 앞입니다.</summary>
    public IReadOnlyList<ReferenceResultSet> History => history;

    public IReadOnlyList<object> Rows => rows;

    /// <summary>필터를 통과한 위치 수입니다.</summary>
    public int VisibleCount => visibleCount;

    public string Filter
    {
        get => filter;
        set
        {
            var next = (value ?? string.Empty).Trim();
            if (next == filter) return;
            filter = next;
            Rebuild();
        }
    }

    public bool GroupByFile
    {
        get => groupByFile;
        set
        {
            if (value == groupByFile) return;
            groupByFile = value;
            Rebuild();
        }
    }

    /// <summary>머리 줄에 보일 요약입니다.</summary>
    public string Summary
    {
        get
        {
            if (current is null) return string.Empty;
            var count = filter.Length == 0 ? $"{current.Items.Count:N0}개 위치" : $"{visibleCount:N0}/{current.Items.Count:N0}개 위치";
            return $"{count} · {current.FileCount:N0}개 파일" + (current.Notes.Length == 0 ? string.Empty : " · " + current.Notes);
        }
    }

    /// <summary>새 결과를 보이고 최근 결과 맨 앞에 둡니다. 필터는 지우고 접힌 파일은 모두 펼칩니다.</summary>
    public void Show(ReferenceResultSet set)
    {
        if (set is null) throw new ArgumentNullException(nameof(set));
        history.Remove(set);
        history.Insert(0, set);
        if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);
        Select(set, clearFilter: true);
        OnPropertyChanged(nameof(History));
    }

    /// <summary>최근 결과 중 하나로 돌아갑니다.</summary>
    public void Select(ReferenceResultSet set, bool clearFilter = false)
    {
        if (set is null) throw new ArgumentNullException(nameof(set));
        current = set;
        collapsed.Clear();
        if (clearFilter) filter = string.Empty;
        Rebuild();
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Filter));
    }

    /// <summary>현재 결과와 최근 결과를 모두 지웁니다.</summary>
    public void Clear()
    {
        history.Clear();
        collapsed.Clear();
        current = null;
        filter = string.Empty;
        Rebuild();
        OnPropertyChanged(nameof(History));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Filter));
    }

    /// <summary>파일 머리 행을 접거나 펼칩니다.</summary>
    public void SetExpanded(string path, bool expanded)
    {
        var changed = expanded ? collapsed.Remove(path) : collapsed.Add(path);
        if (changed) Rebuild();
    }

    public void SetAllExpanded(bool expanded)
    {
        if (current is null) return;
        collapsed.Clear();
        if (!expanded)
        {
            foreach (var item in current.Items) collapsed.Add(item.FullPath);
        }

        Rebuild();
    }

    public bool IsExpanded(string path) => !collapsed.Contains(path);

    private void Rebuild()
    {
        var result = new List<object>();
        visibleCount = 0;
        if (current is not null)
        {
            var visible = filter.Length == 0 ? current.Items : current.Items.Where(item => item.Matches(filter)).ToArray();
            visibleCount = visible.Count;
            if (groupByFile)
            {
                // 결과 순서(요청한 파일 먼저)를 지키며 파일 단위로 묶습니다.
                var totals = current.Items.GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
                foreach (var group in visible.GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase))
                {
                    var expanded = !collapsed.Contains(group.Key);
                    var members = group.ToArray();
                    result.Add(new ReferenceFileRow(members[0], members.Length, totals[group.Key], expanded));
                    if (expanded) result.AddRange(members.Select(item => new ReferenceLineRow(item, showFile: false)));
                }
            }
            else
            {
                result.AddRange(visible.Select(item => new ReferenceLineRow(item, showFile: true)));
            }
        }

        rows = result;
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(Summary));
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
