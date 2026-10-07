using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using VisualBoost.Core.Indexing;

namespace VisualBoost.UI;

/// <summary>참조 찾기 한 번의 결과입니다. 만든 뒤에는 바꾸지 않습니다.</summary>
internal sealed class ReferenceResultSet
{
    public ReferenceResultSet(string symbol, IReadOnlyList<NavigationResultItem> items, string notes, DateTime createdAt, string? originPath = null,
        string? pairPath = null)
    {
        Symbol = symbol ?? string.Empty;
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Notes = notes ?? string.Empty;
        CreatedAt = createdAt;
        OriginPath = string.IsNullOrEmpty(originPath) ? null : originPath;
        PairPath = string.IsNullOrEmpty(pairPath) ? null : pairPath;
        FileCount = items.Select(item => item.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    public string Symbol { get; }

    /// <summary>clangd가 돌려준 위치입니다. 화면 순서는 모델이 <see cref="ReferenceOrder"/>로 정합니다.</summary>
    public IReadOnlyList<NavigationResultItem> Items { get; }

    /// <summary>참조 찾기를 실행한 파일입니다. '현재 프로젝트'와 가까운 순서의 기준이며 모르면 null입니다.</summary>
    public string? OriginPath { get; }

    /// <summary>
    /// 결과 파일 중 요청한 파일의 헤더·구현 짝입니다. 헤더·구현 전환과 같은 규칙(<c>FilePairResolver</c>, 사용자 옵션)의 가장 높은 후보 하나이며,
    /// 이름만 같은 먼 파일을 짝으로 올리지 않게 명령에서 정합니다. 없으면 null입니다.
    /// </summary>
    public string? PairPath { get; }

    /// <summary>결과 제한·색인 진행처럼 함께 알릴 내용입니다.</summary>
    public string Notes { get; }

    public DateTime CreatedAt { get; }

    public int FileCount { get; }

    /// <summary>최근 결과 목록에 보이는 이름입니다.</summary>
    public string Title => $"{Symbol} · {Items.Count:N0}개 · {CreatedAt.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    public override string ToString() => Title;
}

/// <summary>
/// 참조 결과의 프로젝트 머리 행입니다. 모든 프로젝트 모드에서 결과가 둘 이상의 묶음(프로젝트·프로젝트 밖)에 걸칠 때만 둡니다.
/// XAML 형식별 템플릿이 찾을 수 있게 공개합니다.
/// </summary>
public sealed class ReferenceProjectRow
{
    internal ReferenceProjectRow(string key, string name, bool isOutside, int count, int total, bool isExpanded)
    {
        Key = key;
        Name = name;
        IsOutside = isOutside;
        Count = ReferenceRowText.Count(count, total);
        IsExpanded = isExpanded;
    }

    /// <summary>접기 상태와 선택을 기억하는 키입니다. 프로젝트 범위 Id이거나 프로젝트 밖 묶음 키(<see cref="ReferenceResultsModel.OutsideKey"/>)입니다.</summary>
    public string Key { get; }

    public string Name { get; }

    /// <summary>어느 프로젝트에도 속하지 않은 파일(엔진·소속 미확인)의 묶음입니다.</summary>
    public bool IsOutside { get; }

    /// <summary>머리 행 아이콘 이름입니다(<see cref="ProductIcons.Symbol"/>).</summary>
    public string IconKind => IsOutside ? "unknown" : "project";

    public string ToolTip => IsOutside ? "어느 프로젝트에도 속하지 않은 파일(엔진·소속 미확인)" : Name;

    /// <summary>이 묶음의 위치 수입니다. 필터로 일부만 보이면 "보이는 수/전체"입니다.</summary>
    public string Count { get; }

    public bool IsExpanded { get; }

    public string Glyph => IsExpanded ? "▾" : "▸";

    /// <summary>화면 읽기 프로그램이 읽는 이름입니다.</summary>
    public override string ToString() => $"{Name}, {Count}개 위치";
}

/// <summary>참조 결과의 파일 머리 행입니다. XAML 형식별 템플릿이 찾을 수 있게 공개합니다.</summary>
public sealed class ReferenceFileRow
{
    internal ReferenceFileRow(NavigationResultItem first, int count, int total, bool isExpanded, int level = 0)
    {
        FullPath = first.FullPath;
        FileName = first.FileName;
        Folder = first.Folder;
        Count = ReferenceRowText.Count(count, total);
        IsExpanded = isExpanded;
        Indent = ReferenceRowText.Indent(level);
    }

    /// <summary>프로젝트로 묶으면 한 단계 들여씁니다.</summary>
    public Thickness Indent { get; }

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

/// <summary>
/// 참조 위치 한 줄입니다. 항상 파일 머리 행 아래에 오므로 파일 이름은 보이지 않습니다. 왼쪽부터 줄 번호 · 역할 표식 · 소속 이름 · 코드 순서라
/// 행 왼쪽만 훑어도 어디에서 어떻게 쓰는지 읽힙니다.
/// </summary>
public sealed class ReferenceLineRow
{
    internal ReferenceLineRow(NavigationResultItem item, int lineDigits = 0, int fileLevel = 0)
    {
        Item = item;
        LineLabel = ReferenceRowText.LineLabel(item.Line, lineDigits);
        Indent = ReferenceRowText.LineIndent(fileLevel);
    }

    internal NavigationResultItem Item { get; }

    public string FullPath => Item.FullPath;

    public string FileName => Item.FileName;

    public string Folder => Item.Folder;

    public string Line => Item.Line;

    /// <summary>
    /// 결과에서 가장 긴 줄 번호 자릿수에 맞춰 앞을 숫자 폭 공백(U+2007)으로 채운 줄 번호입니다. 숫자 폭이 같은 글꼴에서 행마다 열 너비를
    /// 따로 재지 않아도 오른쪽 끝이 맞습니다(가상화 목록에서 크기 공유 그룹을 쓰면 스크롤 중 폭이 흔들림).
    /// </summary>
    public string LineLabel { get; }

    /// <summary>파일 머리 행의 펼침 표시만큼 더 들여씁니다.</summary>
    public Thickness Indent { get; }

    /// <summary>역할 표식("정의"·"선언")입니다. 근거가 없으면 빈 문자열이고 표식을 숨깁니다.</summary>
    public string RoleText => Item.RoleText;

    /// <summary>소속 이름의 마지막 마디입니다. 전체 이름은 <see cref="Container"/>입니다.</summary>
    public string ContainerShortName => Item.ContainerShortName;

    /// <summary>소속 이름 색에 쓸 종류입니다. 모르면 null(행 기본 글자색)입니다.</summary>
    public VisualBoost.Core.Coloring.CodePreviewKind? ContainerKind => Item.ContainerKind;

    public string Before => Item.Before;

    public string Match => Item.Match;

    public string After => Item.After;

    public string Code => Item.Code;

    public string Container => Item.Container;

    /// <summary>일치 구간이 가리키는 심볼의 종류입니다. 모르면 null입니다.</summary>
    public VisualBoost.Core.Analysis.SourceSymbolKind? SymbolKind => Item.SymbolKind;

    public IReadOnlyList<VisualBoost.Core.Coloring.CodePreviewSpan> PreviewSpans => Item.PreviewSpans;

    /// <summary>화면 읽기 프로그램이 읽는 이름입니다. 화면에 보이는 역할·소속을 함께 읽고, 소속은 전체 이름으로 읽습니다.</summary>
    public override string ToString()
    {
        var role = RoleText.Length == 0 ? string.Empty : " " + RoleText;
        var container = Container.Length == 0 ? string.Empty : " " + Container;
        return $"{FileName} {Line}줄{role}{container}: {Code}";
    }
}

/// <summary>참조 행들이 함께 쓰는 표시 규칙입니다.</summary>
internal static class ReferenceRowText
{
    /// <summary>단계마다 들이는 폭(DIP)입니다.</summary>
    public const double LevelWidth = 16;

    /// <summary>머리 행의 펼침 표시 열 폭(DIP)입니다. 위치 행은 이만큼 더 들여 줄 번호가 파일 아이콘 아래에서 시작합니다.</summary>
    public const double GlyphWidth = 18;

    public static string Count(int count, int total) => count == total ? count.ToString("N0", CultureInfo.CurrentCulture) : $"{count:N0}/{total:N0}";

    public static Thickness Indent(int level) => new(level * LevelWidth, 0, 0, 0);

    public static Thickness LineIndent(int fileLevel) => new(fileLevel * LevelWidth + GlyphWidth, 0, 0, 0);

    public static string LineLabel(string line, int digits) => digits > line.Length ? new string('\u2007', digits - line.Length) + line : line;
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
/// 도킹 참조 창의 표시 상태입니다. 현재 결과·최근 결과·범위 모드·필터·접힌 묶음으로 화면에 그릴 행 목록을 만듭니다.
/// 행은 항상 파일별로 묶고 요청한 파일에서 가까운 순서(<see cref="ReferenceOrder"/>)로 둡니다. 모든 프로젝트 모드에서 결과가 둘 이상의
/// 프로젝트(또는 프로젝트 밖)에 걸치면 그 위를 프로젝트 머리 행으로 한 번 더 묶습니다. UI thread에서만 씁니다.
/// </summary>
internal sealed class ReferenceResultsModel : INotifyPropertyChanged
{
    /// <summary>최근 결과는 이 수만큼만 남깁니다. 결과 하나가 최대 수천 위치이므로 메모리를 묶어 둡니다.</summary>
    public const int MaxHistory = 10;

    public const string NoResultsMessage = "C++ 편집기에서 참조 찾기(Shift+Alt+F)를 실행하면 결과가 여기에 표시됩니다.";
    public const string NoMatchMessage = "필터와 일치하는 위치가 없습니다.";
    public const string CurrentProjectTip = "현재 프로젝트 탐색";
    public const string AllProjectsTip = "모든 프로젝트 탐색";

    /// <summary>어느 프로젝트에도 속하지 않은 파일 묶음의 키입니다. 프로젝트 범위 Id와 같은 접두사라 파일 경로와 겹치지 않습니다.</summary>
    public const string OutsideKey = SymbolSearchScope.ProjectIdPrefix;

    public const string OutsideName = "프로젝트 밖";

    private readonly List<ReferenceResultSet> history = new();

    // 접은 파일 경로와 프로젝트 묶음 키입니다. 묶음 키는 'project:'로 시작해 드라이브 문자로 시작하는 경로와 겹치지 않습니다.
    private readonly HashSet<string> collapsed = new(StringComparer.OrdinalIgnoreCase);

    // 현재 결과의 파일마다 정한 프로젝트 묶음(키·이름)입니다. 결과·소속 목록이 바뀔 때 다시 정합니다.
    private readonly Dictionary<string, (string Key, string Name)> groups = new(StringComparer.OrdinalIgnoreCase);
    private bool grouped;
    private ReferenceResultSet? current;
    private string filter = string.Empty;

    // 범위 모드는 Solution과 무관한 선택이라 Solution을 닫아도 유지합니다. 소속 판정에 쓰는 범위 목록은 Solution마다 다시 받습니다.
    // 기본은 현재 프로젝트입니다(2026-10-07 사용자 피드백). 소속을 모르는 동안에는 이 모드에서도 모든 위치를 보입니다.
    private ReferenceScopeMode mode = ReferenceScopeMode.CurrentProject;
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

    /// <summary>지금 행 목록이 프로젝트 머리 행으로 묶였는지입니다.</summary>
    public bool IsGrouped => grouped;

    /// <summary>파일이 든 프로젝트 묶음의 키입니다. 현재 결과에 없는 파일이면 null입니다.</summary>
    public string? GroupKeyOf(string path) => groups.TryGetValue(path, out var group) ? group.Key : null;

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

    // 소속 목록이 게시되기 전('전체' 하나뿐인 초기 목록 포함)이면 확인 중입니다. 게시된 목록은 '프로젝트 코드'·'소속 미확인'을 늘 포함합니다.
    // 게시되었는데 프로젝트가 하나도 없거나(폴더 작업 영역 등) 요청한 파일이 어느 프로젝트에도 없으면(엔진·미등록 파일) 모든 위치를 보입니다.
    private string ProjectPendingText =>
        catalog is null || catalog.Count <= 1 ? "현재 프로젝트 확인 중"
        : !catalog.Any(IsProject) ? "프로젝트 정보가 없어 모든 위치 표시"
        : current?.OriginPath is not null ? "현재 파일이 속한 프로젝트가 없어 모든 프로젝트 표시"
        : "찾은 파일을 알 수 없어 모든 프로젝트 표시";

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

    /// <summary>파일 머리 행(파일 경로)이나 프로젝트 머리 행(묶음 키)을 접거나 펼칩니다.</summary>
    public void SetExpanded(string key, bool expanded)
    {
        var changed = expanded ? collapsed.Remove(key) : collapsed.Add(key);
        if (changed) Rebuild();
    }

    /// <summary>모든 파일과 프로젝트 묶음을 펼치거나 접습니다.</summary>
    public void SetAllExpanded(bool expanded)
    {
        if (current is null) return;
        collapsed.Clear();
        if (!expanded)
        {
            foreach (var item in current.Items) collapsed.Add(item.FullPath);
            foreach (var group in groups.Values) collapsed.Add(group.Key);
        }

        Rebuild();
    }

    public bool IsExpanded(string key) => !collapsed.Contains(key);

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
            : ReferenceOrder.Sort(current.Items, origin, current.PairPath, originProjects.Any(), InCurrentProject,
                path => solutionCode?.Includes(path) == true);

        // 파일마다 묶을 프로젝트를 하나 정합니다. 여러 프로젝트에 등록된 공유 파일은 요청한 파일의 프로젝트를, 아니면 목록 순서상 첫 프로젝트를 고릅니다.
        groups.Clear();
        if (current is null) return;
        var projects = catalog?.Where(IsProject).ToArray() ?? Array.Empty<SymbolSearchScope>();
        foreach (var item in current.Items)
        {
            if (groups.ContainsKey(item.FullPath)) continue;
            var owner = originProjects.FirstOrDefault(project => project.Includes(item.FullPath)) ??
                        projects.FirstOrDefault(project => project.Includes(item.FullPath));
            groups[item.FullPath] = owner is null
                ? (OutsideKey, OutsideName)
                : (owner.Id, owner.Name.StartsWith(SymbolSearchScope.ProjectNamePrefix, StringComparison.Ordinal)
                    ? owner.Name.Substring(SymbolSearchScope.ProjectNamePrefix.Length)
                    : owner.Name);
        }
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

            // 프로젝트 머리 행은 모든 프로젝트 모드에서 결과가 둘 이상의 묶음에 걸칠 때만 둡니다. 필터를 치는 동안 머리 행이
            // 나타났다 사라지지 않게 필터 전 위치로 정합니다. 현재 프로젝트 모드는 이미 한 프로젝트라 파일부터 시작해 한 행을 아낍니다.
            grouped = mode == ReferenceScopeMode.AllProjects &&
                      inMode.Select(item => GroupKeyOf(item.FullPath)).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
            var fileLevel = grouped ? 1 : 0;

            // 줄 번호 자릿수는 필터와 무관하게 결과 전체로 정해 필터를 바꿔도 열 너비가 흔들리지 않게 합니다.
            var digits = current.Items.Count == 0 ? 0 : current.Items.Max(item => item.Line.Length);

            // 가까운 순서를 지키며 파일 단위로 묶습니다. 위치 수는 범위·필터 전의 그 파일 위치 수와 함께 보입니다.
            var totals = current.Items.GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var files = visible.GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.ToArray()).ToArray();
            visibleFileCount = files.Length;

            void AddFile(NavigationResultItem[] members)
            {
                var path = members[0].FullPath;
                var expanded = !collapsed.Contains(path);
                ordered.AddRange(members);
                result.Add(new ReferenceFileRow(members[0], members.Length, totals[path], expanded, fileLevel));
                if (expanded) result.AddRange(members.Select(item => new ReferenceLineRow(item, digits, fileLevel)));
            }

            if (!grouped)
            {
                foreach (var members in files) AddFile(members);
            }
            else
            {
                // 묶음은 요청한 파일의 프로젝트가 먼저이고, 나머지는 가까운 순서상 처음 나오는 순서입니다. 묶음 안 파일 순서는 그대로입니다.
                var originKey = originProjects.FirstOrDefault()?.Id;
                var groupTotals = current.Items.GroupBy(item => GroupKeyOf(item.FullPath) ?? OutsideKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
                var byGroup = files.GroupBy(members => GroupKeyOf(members[0].FullPath) ?? OutsideKey, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => string.Equals(group.Key, originKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
                foreach (var group in byGroup)
                {
                    var expanded = !collapsed.Contains(group.Key);
                    var name = groups[group.First()[0].FullPath].Name;
                    result.Add(new ReferenceProjectRow(group.Key, name, group.Key == OutsideKey, group.Sum(members => members.Length),
                        groupTotals[group.Key], expanded));
                    foreach (var members in group)
                    {
                        if (expanded)
                        {
                            AddFile(members);
                        }
                        else
                        {
                            // 접힌 묶음의 위치도 보이는 결과(복사 대상)에는 남깁니다.
                            ordered.AddRange(members);
                        }
                    }
                }
            }
        }
        else
        {
            grouped = false;
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
    /// <param name="pair">요청 파일의 헤더·구현 짝입니다. 현재 프로젝트를 알 때는 현재 프로젝트 안의 짝만 앞에 둡니다.</param>
    /// <param name="hasCurrentProject">요청 파일의 프로젝트를 알면 true입니다.</param>
    public static IReadOnlyList<NavigationResultItem> Sort(IReadOnlyList<NavigationResultItem> items, string? origin, string? pair,
        bool hasCurrentProject, Func<string, bool> inCurrentProject, Func<string, bool> inSolutionCode)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        var originFolders = origin is null ? null : Folders(Path.GetDirectoryName(origin));
        // 짝이 다른 프로젝트·엔진에 있으면 현재 프로젝트 파일보다 앞에 두지 않습니다(현재 프로젝트 우선).
        var promotedPair = pair is not null && (!hasCurrentProject || inCurrentProject(pair)) ? pair : null;
        var keys = new Dictionary<string, (int Rank, int Distance)>(StringComparer.OrdinalIgnoreCase);
        (int Rank, int Distance) Key(string path)
        {
            if (keys.TryGetValue(path, out var key)) return key;
            var rank = origin is not null && string.Equals(path, origin, StringComparison.OrdinalIgnoreCase) ? 0
                : promotedPair is not null && string.Equals(path, promotedPair, StringComparison.OrdinalIgnoreCase) ? 1
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
    Toggle,
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
    // 접힌 파일도 범위·필터를 통과했으면 "보이는 결과"에 넣습니다. 이름만 보면 펼친 행만으로 읽힐 수 있어 툴팁으로 알립니다.
    private static readonly ReferenceMenuEntry CopyVisible = new(ReferenceMenuCommand.CopyVisible, "보이는 결과 모두 복사",
        toolTip: "범위·필터를 통과한 위치를 모두 복사합니다. 접힌 파일의 위치도 넣습니다.");

    /// <summary>
    /// 메뉴를 연 행(<paramref name="row"/>)에 맞는 항목입니다. null 항목은 구분선입니다. 위치 행은 열기·복사, 파일 머리 행은 접기·펼치기와 경로,
    /// 프로젝트 머리 행은 접기·펼치기, 빈 곳은 전부 펼치기·접기만 보입니다. 전부 펼치기·접기는 바꿀 머리 행이 보일 때만 켭니다.
    /// </summary>
    public static IReadOnlyList<ReferenceMenuEntry?> Entries(object? row, ReferenceResultsModel model)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        // 접고 펼 수 있는 보이는 머리 행(파일·프로젝트)의 펼침 상태입니다. 전부 펼치기·접기는 두 단계 모두에 적용합니다.
        var heads = model.Rows.Select(item => item switch
        {
            ReferenceFileRow file => (bool?)file.IsExpanded,
            ReferenceProjectRow project => project.IsExpanded,
            _ => null,
        }).Where(expanded => expanded is not null).Select(expanded => expanded!.Value).ToArray();
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
                    ? new ReferenceMenuEntry(ReferenceMenuCommand.Toggle, "접기", "←")
                    : new ReferenceMenuEntry(ReferenceMenuCommand.Toggle, "펼치기", "→"));
                result.Add(null);
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.CopyPath, "전체 경로 복사"));
                result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.ShowInExplorer, "탐색기에서 보기"));
                result.Add(null);
                result.Add(CopyVisible);
                result.Add(null);
                break;
            case ReferenceProjectRow project:
                result.Add(project.IsExpanded
                    ? new ReferenceMenuEntry(ReferenceMenuCommand.Toggle, "접기", "←")
                    : new ReferenceMenuEntry(ReferenceMenuCommand.Toggle, "펼치기", "→"));
                result.Add(null);
                result.Add(CopyVisible);
                result.Add(null);
                break;
        }

        result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.ExpandAll, "전부 펼치기", isEnabled: heads.Any(expanded => !expanded)));
        result.Add(new ReferenceMenuEntry(ReferenceMenuCommand.CollapseAll, "전부 접기", isEnabled: heads.Any(expanded => expanded)));
        return result;
    }

    /// <summary>위치 복사 형식 <c>전체경로(줄)</c>입니다. 파일 탐색 입력과 출력 창의 위치 형식에 맞춥니다.</summary>
    public static string Location(NavigationResultItem item) => $"{item.FullPath}({item.Line})";

    /// <summary>위치마다 한 줄씩 <c>전체경로(줄): 코드</c>로 적습니다. 코드는 미리보기의 생략 없이 원래 줄 그대로입니다.</summary>
    public static string Lines(IEnumerable<NavigationResultItem> items) =>
        string.Join(Environment.NewLine, items.Select(item => Location(item) + ": " + item.SourceLine));
}
