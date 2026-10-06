using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using VisualBoost.Core.Indexing;

namespace VisualBoost.UI;

/// <summary>참조 찾기 한 번의 결과입니다. 만든 뒤에는 바꾸지 않습니다.</summary>
internal sealed class ReferenceResultSet
{
    public ReferenceResultSet(string symbol, IReadOnlyList<NavigationResultItem> items, string notes, DateTime createdAt, string? originPath = null)
    {
        Symbol = symbol ?? string.Empty;
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Notes = notes ?? string.Empty;
        CreatedAt = createdAt;
        OriginPath = string.IsNullOrEmpty(originPath) ? null : originPath;
        FileCount = items.Select(item => item.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    public string Symbol { get; }

    /// <summary>clangd가 돌려준 위치입니다. 화면 순서는 모델이 <see cref="ReferenceOrder"/>로 정합니다.</summary>
    public IReadOnlyList<NavigationResultItem> Items { get; }

    /// <summary>참조 찾기를 실행한 파일입니다. '현재 프로젝트'와 가까운 순서의 기준이며 모르면 null입니다.</summary>
    public string? OriginPath { get; }

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

/// <summary>참조 창이 보일 위치의 범위입니다.</summary>
internal enum ReferenceScopeMode
{
    /// <summary>모든 위치를 보입니다.</summary>
    AllProjects,

    /// <summary>참조 찾기를 실행한 파일이 속한 프로젝트의 위치만 보입니다.</summary>
    CurrentProject,
}

/// <summary>
/// 도킹 참조 창의 표시 상태입니다. 현재 결과·최근 결과·범위 모드·필터·접힌 파일로 화면에 그릴 행 목록을 만듭니다.
/// 행은 항상 파일별로 묶고 요청한 파일에서 가까운 순서(<see cref="ReferenceOrder"/>)로 둡니다. UI thread에서만 씁니다.
/// </summary>
internal sealed class ReferenceResultsModel : INotifyPropertyChanged
{
    /// <summary>최근 결과는 이 수만큼만 남깁니다. 결과 하나가 최대 수천 위치이므로 메모리를 묶어 둡니다.</summary>
    public const int MaxHistory = 10;

    public const string NoResultsMessage = "C++ 편집기에서 참조 찾기(Shift+Alt+F)를 실행하면 결과가 여기에 표시됩니다.";
    public const string NoMatchMessage = "필터와 일치하는 위치가 없습니다.";
    public const string CurrentProjectTip = "현재 프로젝트 탐색";
    public const string AllProjectsTip = "모든 프로젝트 탐색";

    private readonly List<ReferenceResultSet> history = new();
    private readonly HashSet<string> collapsed = new(StringComparer.OrdinalIgnoreCase);
    private ReferenceResultSet? current;
    private string filter = string.Empty;

    // 범위 모드는 Solution과 무관한 선택이라 Solution을 닫아도 유지합니다. 소속 판정에 쓰는 범위 목록은 Solution마다 다시 받습니다.
    private ReferenceScopeMode mode = ReferenceScopeMode.AllProjects;
    private IReadOnlyList<SymbolSearchScope>? catalog;
    private SymbolSearchScope[] originProjects = Array.Empty<SymbolSearchScope>();
    private IReadOnlyList<NavigationResultItem> orderedItems = Array.Empty<NavigationResultItem>();
    private IReadOnlyList<object> rows = Array.Empty<object>();
    private IReadOnlyList<NavigationResultItem> visibleItems = Array.Empty<NavigationResultItem>();
    private int modeCount;
    private int visibleFileCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ReferenceResultSet? Current => current;

    /// <summary>최근 결과입니다. 최신이 앞입니다.</summary>
    public IReadOnlyList<ReferenceResultSet> History => history;

    public IReadOnlyList<object> Rows => rows;

    /// <summary>범위·필터를 통과한 위치입니다. 접힌 파일의 위치도 들어 있으며 순서는 화면의 파일 순서입니다.</summary>
    public IReadOnlyList<NavigationResultItem> VisibleItems => visibleItems;

    /// <summary>범위·필터를 통과한 위치 수입니다.</summary>
    public int VisibleCount => visibleItems.Count;

    public ReferenceScopeMode Mode => mode;

    /// <summary>현재 결과를 찾은 파일이 속한 프로젝트를 알았는지입니다. 모르면 '현재 프로젝트' 모드에서도 모든 위치를 보입니다.</summary>
    public bool HasCurrentProject => originProjects.Length > 0;

    /// <summary>현재 결과를 찾은 파일이 속한 프로젝트 이름입니다. 여러 프로젝트에 등록된 파일이면 모두 적습니다.</summary>
    public string CurrentProjectNames => string.Join(", ", originProjects.Select(project => project.Name));

    /// <summary>'현재 프로젝트 탐색' 버튼의 툴팁입니다. 첫 줄은 기능 이름이고, 결과가 있으면 둘째 줄에 기준 프로젝트나 판정 상태를 붙입니다.</summary>
    public string CurrentProjectToolTip =>
        current is null ? CurrentProjectTip : CurrentProjectTip + "\n" + (HasCurrentProject ? CurrentProjectNames : ProjectPendingText);

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

    /// <summary>머리 줄에 보일 요약입니다. 범위나 필터로 숨긴 위치가 있으면 위치·파일 수를 "보이는 수/전체"로 보입니다.</summary>
    public string Summary
    {
        get
        {
            if (current is null) return string.Empty;
            var narrowed = filter.Length > 0 || IsNarrowedByProject;
            var counts = narrowed
                ? $"{VisibleCount:N0}/{current.Items.Count:N0}개 위치 · {visibleFileCount:N0}/{current.FileCount:N0}개 파일"
                : $"{current.Items.Count:N0}개 위치 · {current.FileCount:N0}개 파일";
            var pending = mode == ReferenceScopeMode.CurrentProject && !HasCurrentProject ? " · " + ProjectPendingText : string.Empty;
            return counts + pending + (current.Notes.Length == 0 ? string.Empty : " · " + current.Notes);
        }
    }

    /// <summary>보일 행이 없을 때의 안내입니다. 행이 있으면 빈 문자열입니다.</summary>
    public string EmptyMessage
    {
        get
        {
            if (current is null) return NoResultsMessage;
            if (modeCount == 0) return $"현재 프로젝트에는 위치가 없습니다 · 전체 {current.Items.Count:N0}개";
            return rows.Count == 0 ? NoMatchMessage : string.Empty;
        }
    }

    private bool IsNarrowedByProject => mode == ReferenceScopeMode.CurrentProject && HasCurrentProject;

    // 소속 목록이 게시되기 전이면 확인 중이고, 게시되었는데 요청한 파일이 어느 프로젝트에도 없으면(엔진·미등록 파일) 모든 위치를 보입니다.
    private string ProjectPendingText => current?.OriginPath is not null && catalog?.Any(IsProject) == true
        ? "현재 파일이 속한 프로젝트가 없어 모든 프로젝트 표시"
        : "현재 프로젝트 확인 중";

    /// <summary>새 결과를 보이고 최근 결과 맨 앞에 둡니다. 필터는 지우고 접힌 파일은 모두 펼칩니다. 범위 모드는 유지합니다.</summary>
    public void Show(ReferenceResultSet set)
    {
        if (set is null) throw new ArgumentNullException(nameof(set));
        history.Remove(set);
        history.Insert(0, set);
        if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);
        Select(set, clearFilter: true);
        OnPropertyChanged(nameof(History));
    }

    /// <summary>최근 결과 중 하나로 돌아갑니다. 범위 모드는 유지하고 '현재 프로젝트'는 그 결과를 찾은 파일 기준으로 다시 정합니다.</summary>
    public void Select(ReferenceResultSet set, bool clearFilter = false)
    {
        if (set is null) throw new ArgumentNullException(nameof(set));
        current = set;
        collapsed.Clear();
        if (clearFilter) filter = string.Empty;
        UpdateOrigin();
        Rebuild();
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Filter));
    }

    /// <summary>현재 결과·최근 결과와 Solution의 소속 목록을 지웁니다. Solution을 닫을 때 부릅니다. 범위 모드는 유지합니다.</summary>
    public void Clear()
    {
        history.Clear();
        collapsed.Clear();
        current = null;
        filter = string.Empty;
        catalog = null;
        UpdateOrigin();
        Rebuild();
        OnPropertyChanged(nameof(History));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Filter));
    }

    /// <summary>
    /// 소속 판정에 쓸 범위 목록(<c>SolutionFileIndexService.SymbolScopes</c>)을 받습니다. 같은 목록이면 아무것도 하지 않고 false입니다.
    /// 다시 게시된 목록은 소속 파일이 바뀌었을 수 있으므로 현재 프로젝트와 순서를 다시 정합니다.
    /// </summary>
    public bool SetProjects(IReadOnlyList<SymbolSearchScope> scopes)
    {
        if (scopes is null) throw new ArgumentNullException(nameof(scopes));
        if (ReferenceEquals(scopes, catalog)) return false;
        catalog = scopes;
        UpdateOrigin();
        Rebuild();
        return true;
    }

    public void SetMode(ReferenceScopeMode next)
    {
        if (next == mode) return;
        mode = next;
        Rebuild();
        OnPropertyChanged(nameof(Mode));
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

    private static bool IsProject(SymbolSearchScope scope) => scope.Id.StartsWith(SymbolSearchScope.ProjectIdPrefix, StringComparison.Ordinal);

    private bool InCurrentProject(string path)
    {
        foreach (var project in originProjects)
        {
            if (project.Includes(path)) return true;
        }

        return false;
    }

    private void UpdateOrigin()
    {
        var origin = current?.OriginPath;
        originProjects = origin is null || catalog is null
            ? Array.Empty<SymbolSearchScope>()
            : catalog.Where(scope => IsProject(scope) && scope.Includes(origin)).ToArray();
        var solutionCode = catalog?.FirstOrDefault(scope => scope.Id == SymbolSearchScope.RegisteredId);
        orderedItems = current is null
            ? Array.Empty<NavigationResultItem>()
            : ReferenceOrder.Sort(current.Items, origin, InCurrentProject, path => solutionCode?.Includes(path) == true);
    }

    private void Rebuild()
    {
        var result = new List<object>();
        var ordered = new List<NavigationResultItem>();
        modeCount = 0;
        visibleFileCount = 0;
        if (current is not null)
        {
            var inMode = IsNarrowedByProject ? orderedItems.Where(item => InCurrentProject(item.FullPath)).ToArray() : orderedItems;
            modeCount = inMode.Count;
            var visible = filter.Length == 0 ? inMode : inMode.Where(item => item.Matches(filter)).ToArray();

            // 가까운 순서를 지키며 파일 단위로 묶습니다. 위치 수는 범위·필터 전의 그 파일 위치 수와 함께 보입니다.
            var totals = current.Items.GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var group in visible.GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                var expanded = !collapsed.Contains(group.Key);
                var members = group.ToArray();
                ordered.AddRange(members);
                visibleFileCount++;
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

/// <summary>
/// 참조 결과의 화면 순서입니다. clangd 참조 응답에는 관련도 순서가 없으므로 요청한 파일에서 가까운 순서를 VisualBoost가 정합니다.
/// 요청한 파일 → 이름이 같은 짝 파일(헤더·소스) → 현재 프로젝트 → Solution의 다른 프로젝트 코드 → 그 밖(엔진·소속 미확인) 순서이고,
/// 같은 묶음 안에서는 요청한 파일의 폴더에서 폴더 거리가 가까운 파일이 먼저입니다. 그다음은 경로, 파일 안에서는 줄·열 순서입니다.
/// </summary>
internal static class ReferenceOrder
{
    public static IReadOnlyList<NavigationResultItem> Sort(IReadOnlyList<NavigationResultItem> items, string? origin,
        Func<string, bool> inCurrentProject, Func<string, bool> inSolutionCode)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        var originFolders = origin is null ? null : Folders(Path.GetDirectoryName(origin));
        var originStem = origin is null ? null : Path.GetFileNameWithoutExtension(origin);
        var keys = new Dictionary<string, (int Rank, int Distance)>(StringComparer.OrdinalIgnoreCase);
        (int Rank, int Distance) Key(string path)
        {
            if (keys.TryGetValue(path, out var key)) return key;
            var rank = origin is not null && string.Equals(path, origin, StringComparison.OrdinalIgnoreCase) ? 0
                : originStem is not null && string.Equals(Path.GetFileNameWithoutExtension(path), originStem, StringComparison.OrdinalIgnoreCase) ? 1
                : inCurrentProject(path) ? 2
                : inSolutionCode(path) ? 3
                : 4;
            return keys[path] = (rank, Distance(originFolders, Folders(Path.GetDirectoryName(path))));
        }

        return items
            .OrderBy(item => Key(item.FullPath).Rank)
            .ThenBy(item => Key(item.FullPath).Distance)
            .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Location.Line)
            .ThenBy(item => item.Location.Character)
            .ToArray();
    }

    /// <summary>두 폴더 사이를 오가는 단계 수입니다(공통 상위 폴더까지 올라갔다 내려가는 수). 기준 폴더가 없으면 0입니다.</summary>
    public static int Distance(string[]? from, string[] to)
    {
        if (from is null) return 0;
        var common = 0;
        while (common < from.Length && common < to.Length && string.Equals(from[common], to[common], StringComparison.OrdinalIgnoreCase)) common++;
        return from.Length - common + to.Length - common;
    }

    public static string[] Folders(string? folder) =>
        string.IsNullOrEmpty(folder) ? Array.Empty<string>() : folder!.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
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
    public ReferenceMenuEntry(ReferenceMenuCommand command, string header, string gesture = "", bool isEnabled = true, string? toolTip = null)
    {
        Command = command;
        Header = header;
        Gesture = gesture;
        IsEnabled = isEnabled;
        ToolTip = toolTip;
    }

    public ReferenceMenuCommand Command { get; }

    public string Header { get; }

    /// <summary>메뉴 오른쪽에 보이는 키 안내입니다. 메뉴가 키를 등록하지는 않습니다.</summary>
    public string Gesture { get; }

    public bool IsEnabled { get; }

    /// <summary>머리글만으로 범위가 분명하지 않은 항목의 설명입니다. 없으면 null입니다.</summary>
    public string? ToolTip { get; }
}

/// <summary>참조 창 우클릭 메뉴의 구성과 복사 형식입니다. 화면과 떼어 두어 행 종류별 항목을 테스트로 고정합니다.</summary>
internal static class ReferenceMenu
{
    /// <summary>
    /// 메뉴를 연 행(<paramref name="row"/>)에 맞는 항목입니다. null 항목은 구분선입니다. 위치 행은 열기·복사, 파일 머리 행은 접기·펼치기와 경로,
    /// 빈 곳은 전부 펼치기·접기만 보입니다. 전부 펼치기·접기는 바꿀 파일이 보일 때만 켭니다.
    /// </summary>
    // 접힌 파일도 범위·필터를 통과했으면 "보이는 결과"에 넣습니다. 이름만 보면 펼친 행만으로 읽힐 수 있어 툴팁으로 알립니다.
    private static readonly ReferenceMenuEntry CopyVisible = new(ReferenceMenuCommand.CopyVisible, "보이는 결과 모두 복사",
        toolTip: "범위·필터를 통과한 위치를 모두 복사합니다. 접힌 파일의 위치도 넣습니다.");

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
                result.Add(CopyVisible);
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
                result.Add(CopyVisible);
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
