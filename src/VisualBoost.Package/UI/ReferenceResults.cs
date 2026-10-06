using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using VisualBoost.Core.Indexing;

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

    /// <summary>이 파일의 위치 수입니다. 필터·범위로 일부만 보이면 "보이는 수/전체"입니다.</summary>
    public string Count { get; }

    public bool IsExpanded { get; }

    public string Glyph => IsExpanded ? "▾" : "▸";

    /// <summary>화면 읽기 프로그램이 읽는 이름입니다.</summary>
    public override string ToString() => $"{FileName}, {Count}개 위치";
}

/// <summary>참조 위치 한 줄입니다. 항상 파일 머리 행 아래에 오므로 파일 이름은 보이지 않습니다.</summary>
public sealed class ReferenceLineRow
{
    internal ReferenceLineRow(NavigationResultItem item)
    {
        Item = item;
    }

    internal NavigationResultItem Item { get; }

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

    public IReadOnlyList<VisualBoost.Core.Coloring.CodePreviewSpan> PreviewSpans => Item.PreviewSpans;

    /// <summary>화면 읽기 프로그램이 읽는 이름입니다.</summary>
    public override string ToString() => $"{FileName} {Line}줄: {Code}";
}

/// <summary>
/// 도킹 참조 창의 표시 상태입니다. 현재 결과·최근 결과·범위·필터·접힌 파일로 화면에 그릴 행 목록을 만듭니다.
/// 행은 항상 파일별로 묶습니다. UI thread에서만 씁니다.
/// </summary>
internal sealed class ReferenceResultsModel : INotifyPropertyChanged
{
    /// <summary>최근 결과는 이 수만큼만 남깁니다. 결과 하나가 최대 수천 위치이므로 메모리를 묶어 둡니다.</summary>
    public const int MaxHistory = 10;

    public const string NoResultsMessage = "C++ 편집기에서 참조 찾기(Shift+Alt+F)를 실행하면 결과가 여기에 표시됩니다.";
    public const string NoMatchMessage = "필터와 일치하는 위치가 없습니다.";

    private static readonly IReadOnlyList<SymbolSearchScope> AllOnly = new[] { SymbolSearchScope.All };

    private readonly List<ReferenceResultSet> history = new();
    private readonly HashSet<string> collapsed = new(StringComparer.OrdinalIgnoreCase);
    private ReferenceResultSet? current;
    private string filter = string.Empty;
    private IReadOnlyList<SymbolSearchScope> scopes = AllOnly;
    private SymbolSearchScope scope = SymbolSearchScope.All;

    // 사용자가 고른 범위는 새 결과·최근 결과 전환에도 유지하고 Solution을 닫으면(Clear) 잊습니다. 고른 범위가 다시 탐색하는 동안
    // 잠시 빠진 목록('전체'만 있는 목록)에서는 '전체'로 보이다가 돌아오면 다시 쓰고, 소속 목록이 게시되었는데도 없으면 '전체'로 확정합니다.
    private SymbolScopeChoice scopeChoice = new(null, null);
    private IReadOnlyList<object> rows = Array.Empty<object>();
    private IReadOnlyList<NavigationResultItem> visibleItems = Array.Empty<NavigationResultItem>();
    private int scopeCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ReferenceResultSet? Current => current;

    /// <summary>최근 결과입니다. 최신이 앞입니다.</summary>
    public IReadOnlyList<ReferenceResultSet> History => history;

    public IReadOnlyList<object> Rows => rows;

    /// <summary>범위·필터를 통과한 위치입니다. 접힌 파일의 위치도 들어 있으며 순서는 화면의 파일 순서입니다.</summary>
    public IReadOnlyList<NavigationResultItem> VisibleItems => visibleItems;

    /// <summary>범위·필터를 통과한 위치 수입니다.</summary>
    public int VisibleCount => visibleItems.Count;

    /// <summary>고를 수 있는 범위입니다. 소속 목록이 아직 게시되지 않았으면 '전체'만 있습니다.</summary>
    public IReadOnlyList<SymbolSearchScope> Scopes => scopes;

    /// <summary>지금 적용한 범위입니다.</summary>
    public SymbolSearchScope Scope => scope;

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

    /// <summary>머리 줄에 보일 요약입니다. 범위나 필터로 숨긴 위치가 있으면 "보이는 수/전체"로 보입니다.</summary>
    public string Summary
    {
        get
        {
            if (current is null) return string.Empty;
            var narrowed = filter.Length > 0 || !IsAll(scope);
            var count = narrowed ? $"{VisibleCount:N0}/{current.Items.Count:N0}개 위치" : $"{current.Items.Count:N0}개 위치";
            return $"{count} · {current.FileCount:N0}개 파일" + (current.Notes.Length == 0 ? string.Empty : " · " + current.Notes);
        }
    }

    /// <summary>보일 행이 없을 때의 안내입니다. 행이 있으면 빈 문자열입니다.</summary>
    public string EmptyMessage
    {
        get
        {
            if (current is null) return NoResultsMessage;
            if (scopeCount == 0) return $"이 범위에는 위치가 없습니다 · 전체 {current.Items.Count:N0}개";
            return rows.Count == 0 ? NoMatchMessage : string.Empty;
        }
    }

    /// <summary>새 결과를 보이고 최근 결과 맨 앞에 둡니다. 필터는 지우고 접힌 파일은 모두 펼칩니다. 범위는 유지합니다.</summary>
    public void Show(ReferenceResultSet set)
    {
        if (set is null) throw new ArgumentNullException(nameof(set));
        history.Remove(set);
        history.Insert(0, set);
        if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);
        Select(set, clearFilter: true);
        OnPropertyChanged(nameof(History));
    }

    /// <summary>최근 결과 중 하나로 돌아갑니다. 범위는 유지합니다.</summary>
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

    /// <summary>현재 결과·최근 결과를 지우고 범위를 '전체'로 되돌립니다. Solution을 닫을 때 부릅니다.</summary>
    public void Clear()
    {
        history.Clear();
        collapsed.Clear();
        current = null;
        filter = string.Empty;
        scopes = AllOnly;
        scope = SymbolSearchScope.All;
        scopeChoice = new SymbolScopeChoice(null, null);
        Rebuild();
        OnPropertyChanged(nameof(History));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Filter));
        OnPropertyChanged(nameof(Scopes));
        OnPropertyChanged(nameof(Scope));
    }

    /// <summary>
    /// 게시된 범위 목록을 받습니다. 같은 목록이면 아무것도 하지 않습니다. 다시 게시된 목록은 같은 ID라도 소속 파일이 바뀌었을 수 있으므로
    /// 새 범위 객체로 다시 거릅니다.
    /// </summary>
    public void SetScopes(IReadOnlyList<SymbolSearchScope> next)
    {
        if (next is null) throw new ArgumentNullException(nameof(next));
        if (ReferenceEquals(next, scopes)) return;
        scopes = next.Count == 0 ? AllOnly : next;
        OnPropertyChanged(nameof(Scopes));
        Apply(scopeChoice.Choose(scopes));
    }

    /// <summary>사용자가 범위를 골랐습니다. 같은 Solution 동안 새 결과에도 이 범위를 씁니다.</summary>
    public void SelectScope(SymbolSearchScope next)
    {
        if (next is null) throw new ArgumentNullException(nameof(next));
        scopeChoice.UserSelected(next);
        Apply(next);
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

    private static bool IsAll(SymbolSearchScope candidate) => candidate.Id == SymbolSearchScope.All.Id;

    private void Apply(SymbolSearchScope next)
    {
        if (ReferenceEquals(next, scope)) return;
        scope = next;
        Rebuild();
        OnPropertyChanged(nameof(Scope));
    }

    private void Rebuild()
    {
        var result = new List<object>();
        var ordered = new List<NavigationResultItem>();
        scopeCount = 0;
        if (current is not null)
        {
            var inScope = IsAll(scope) ? current.Items : current.Items.Where(item => scope.Includes(item.FullPath)).ToArray();
            scopeCount = inScope.Count;
            var visible = filter.Length == 0 ? inScope : inScope.Where(item => item.Matches(filter)).ToArray();

            // 결과 순서(요청한 파일 먼저)를 지키며 파일 단위로 묶습니다. 위치 수는 범위·필터 전의 그 파일 위치 수와 함께 보입니다.
            var totals = current.Items.GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var group in visible.GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                var expanded = !collapsed.Contains(group.Key);
                var members = group.ToArray();
                ordered.AddRange(members);
                result.Add(new ReferenceFileRow(members[0], members.Length, totals[group.Key], expanded));
                if (expanded) result.AddRange(members.Select(item => new ReferenceLineRow(item)));
            }
        }

        rows = result;
        visibleItems = ordered;
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(Summary));
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>참조 창 우클릭 메뉴의 동작입니다.</summary>
internal enum ReferenceMenuCommand
{
    Open,
    Preview,
    CopyLocation,
    CopyCode,
    CopyPath,
    ShowInExplorer,
    CopyVisible,
    ToggleFile,
    ExpandAll,
    CollapseAll,
}

/// <summary>우클릭 메뉴 항목 하나입니다. 구분선은 <see cref="ReferenceMenu.Entries"/> 목록의 null로 나타냅니다.</summary>
internal sealed class ReferenceMenuEntry
{
    public ReferenceMenuEntry(ReferenceMenuCommand command, string header, string gesture = "", bool isEnabled = true)
    {
        Command = command;
        Header = header;
        Gesture = gesture;
        IsEnabled = isEnabled;
    }

    public ReferenceMenuCommand Command { get; }

    public string Header { get; }

    /// <summary>메뉴 오른쪽에 보이는 키 안내입니다. 메뉴가 키를 등록하지는 않습니다.</summary>
    public string Gesture { get; }

    public bool IsEnabled { get; }
}

/// <summary>참조 창 우클릭 메뉴의 구성과 복사 형식입니다. 화면과 떼어 두어 행 종류별 항목을 테스트로 고정합니다.</summary>
internal static class ReferenceMenu
{
    /// <summary>
    /// 메뉴를 연 행(<paramref name="row"/>)에 맞는 항목입니다. null 항목은 구분선입니다. 위치 행은 열기·복사, 파일 머리 행은 접기·펼치기와 경로,
    /// 빈 곳은 전부 펼치기·접기만 보입니다. 전부 펼치기·접기는 바꿀 파일이 보일 때만 켭니다.
    /// </summary>
    public static IReadOnlyList<ReferenceMenuEntry?> Entries(object? row, ReferenceResultsModel model)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        var files = model.Rows.OfType<ReferenceFileRow>().ToArray();
        var result = new List<ReferenceMenuEntry?>();
        switch (row)
        {
            case ReferenceLineRow:
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.Open, "열기", "Enter"));
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.Preview, "미리 보기", "Space"));
                result.Add(null);
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.CopyLocation, "위치 복사"));
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.CopyCode, "코드 줄 복사"));
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.CopyPath, "전체 경로 복사"));
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.ShowInExplorer, "탐색기에서 보기"));
                result.Add(null);
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.CopyVisible, "보이는 결과 모두 복사"));
                result.Add(null);
                break;
            case ReferenceFileRow file:
                result.Add(file.IsExpanded
                    ? new ReferenceMenuEntry(ReferenceMenuCommand.ToggleFile, "접기", "←")
                    : new ReferenceMenuEntry(ReferenceMenuCommand.ToggleFile, "펼치기", "→"));
                result.Add(null);
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.CopyPath, "전체 경로 복사"));
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.ShowInExplorer, "탐색기에서 보기"));
                result.Add(null);
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.CopyVisible, "보이는 결과 모두 복사"));
                result.Add(null);
                break;
        }

        result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.ExpandAll, "전부 펼치기", isEnabled: files.Any(file => !file.IsExpanded)));
        result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.CollapseAll, "전부 접기", isEnabled: files.Any(file => file.IsExpanded)));
        return result;
    }

    /// <summary>위치 복사 형식 <c>전체경로(줄)</c>입니다. 파일 탐색 입력과 출력 창의 위치 형식에 맞춥니다.</summary>
    public static string Location(NavigationResultItem item) => $"{item.FullPath}({item.Line})";

    /// <summary>위치마다 한 줄씩 <c>전체경로(줄): 코드</c>로 적습니다. 코드는 미리보기의 생략 없이 원래 줄 그대로입니다.</summary>
    public static string Lines(IEnumerable<NavigationResultItem> items) =>
        string.Join(Environment.NewLine, items.Select(item => Location(item) + ": " + item.SourceLine));
}
