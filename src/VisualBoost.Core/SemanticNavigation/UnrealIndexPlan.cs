using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>Unreal 공유 PCH 헤더를 분석 명령에 넣는 방식입니다.</summary>
public enum UnrealPchMode
{
    /// <summary>넣지 않고 분석하고, 분석 오류가 난 색인 단위와 문서에만 넣습니다.</summary>
    Auto = 0,

    /// <summary>실제 빌드처럼 항상 넣습니다.</summary>
    Always = 1,

    /// <summary>넣지 않습니다.</summary>
    Never = 2
}

/// <summary>문서로 열 때 clangd에 덮어쓰기로 줄 명령입니다.</summary>
public sealed class UnrealDocumentCommand
{
    public UnrealDocumentCommand(CompileCommand command, bool withoutPch, IReadOnlyList<string>? supplements = null)
    {
        Command = command;
        WithoutPch = withoutPch;
        Supplements = supplements ?? Array.Empty<string>();
    }

    public CompileCommand Command { get; }

    /// <summary>빌드 명령에 있던 PCH 헤더를 뺐습니다. 분석 오류가 나면 넣은 명령으로 바꿀 수 있습니다.</summary>
    public bool WithoutPch { get; }

    /// <summary>공유 PCH 대신 강제 include로 넣은 보충 헤더입니다(<see cref="IncludeSupplements"/>).</summary>
    public IReadOnlyList<string> Supplements { get; }
}

/// <summary>
/// Unreal 프로젝트의 색인 단위(캐시 폴더의 합성 TU)와 단위별 공유 PCH 사용을 정하고 compile_commands.json을 씁니다. 스레드 안전합니다.
/// </summary>
/// <remarks>
/// 색인 단위는 UBT unity 묶음(구성원 둘 이상)이거나 묶이지 않은 파일 하나를 감싼 합성 TU입니다. 구성원 cpp마다 TU로 색인하면 TU마다
/// 공유 PCH 같은 큰 헤더를 다시 분석하고, UBT가 실제 빌드에서 함께 컴파일한 구성만 묶으므로 이름 충돌 없이 결과가 같습니다(테스트 전용
/// UE 샘플: 구성원 305개 → 묶음 12개, 첫 색인 445초 → 50초, 2026-10-09 측정). UBT unity cpp 대신 합성 TU를 쓰는 이유는 생성 파일과
/// 지운 파일, 따로 컴파일된 파일을 빼기 위해서입니다. database에 원래 파일이 없으므로 문서로 열 때는 <see cref="DocumentCommand"/>를
/// 덮어쓰기로 줘야 합니다.
///
/// 공유 PCH: 텍스트로 넣으면 TU 하나 분석에 약 2 GB와 10초 넘게 들지만(같은 샘플, Engine 단계 헤더가 대부분), 넣지 않으면 PCH가 넣어 주는
/// 엔진 헤더에 기대는 소스가 분석 오류로 정의·참조를 잃습니다. <see cref="UnrealPchMode.Auto"/>는 넣지 않고 색인한 뒤 clangd가 분석 오류를
/// 알린 단위(<see cref="MarkNeedsPch"/>)만 넣습니다. clangd는 명령만 바뀐 TU를 다시 색인하지 않으므로(실행 중·다시 시작 모두, 2026-10-09
/// 확인) PCH를 넣는 단위는 다른 합성 TU 경로(<c>.pch.cpp</c>)로 바꿔 새 TU로 색인하게 합니다. 이전 분석에 오류가 있었으므로 clangd는
/// 구성원 파일의 색인도 새 결과로 바꿉니다. 판단은 구성(인자·구성원)이 같은 동안 캐시 폴더에 남겨 다음 세션에서 다시 실패하지 않게 합니다.
/// clangd는 다시 시작해도 내용이 같으면 오류가 있던 TU를 다시 색인하지 않으므로(2026-10-09 확인) 판단은 실패를 알린 즉시 기록합니다
/// (<see cref="RecordFailure"/>). 판단을 잃으면 그 단위는 내용이 바뀔 때까지 PCH 없는 색인으로 남습니다.
///
/// 헤더 보충(자동이고 자체 이름 색인이 있을 때): 실패한 단위는 PCH 대신 그 모듈이 배운 보충 헤더(<see cref="IncludeSupplements"/>)를 앞에
/// include한 합성 TU(<c>.sup.cpp</c>, 다시 실패하면 더 배운 헤더로 <c>.sup2.cpp</c>)로 먼저 바꾸고, 그래도 실패하면 PCH로 바꿉니다. 단계마다
/// 경로가 달라 clangd가 새 TU로 색인합니다. 보충 헤더는 모듈(<c>*.Build.cs</c> 폴더)마다 배워 판단 기록에 함께 남기고, 그 모듈의 PCH 없는
/// 문서 명령에도 강제 include로 넣습니다.
/// </remarks>
public sealed class UnrealIndexPlan
{
    internal const string UnitsFolder = "units";
    internal const string DecisionsFileName = "pch-units.json";
    internal const string PchSuffix = ".pch.cpp";
    internal const string SupplementSuffix = ".sup.cpp";
    internal const string SecondSupplementSuffix = ".sup2.cpp";

    /// <summary>헤더 보충 단계의 상한입니다. 이 단계에서도 실패하면 PCH로 바꿉니다.</summary>
    internal const int MaxSupplementStage = 2;

    private readonly object gate = new();
    private readonly string directory;
    private readonly PathAliases paths;
    private readonly IReadOnlyList<Unit> units;
    private readonly Dictionary<string, Unit> unitOfMember = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CompileCommand> commandOfFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Unit> unitOfName = new(StringComparer.OrdinalIgnoreCase);
    // 구성원 파일 이름(확장자 제외)별 단위입니다. 색인 대기열 앞당기기에 씁니다(IndexQueuePriority).
    private readonly Dictionary<string, List<Unit>> unitsOfStem = new(StringComparer.OrdinalIgnoreCase);
    // PCH가 필요하다고 판단한 단위(단위 키 → 구성 지문)와 PCH를 넣고도 분석 오류가 난 단위(단위 키 → 그때의 구성원 파일 상태)입니다.
    private readonly Dictionary<string, string> decisions;
    private readonly Dictionary<string, string> failedWithPch;
    // 헤더 보충 단계로 바꾼 단위(단위 키 → "구성 지문:단계")와 모듈별로 배운 보충 헤더(모듈 폴더 → 헤더)입니다. gate로 보호합니다.
    private readonly Dictionary<string, string> stages;
    private readonly Dictionary<string, List<string>> supplements;
    // 보충 단계 단위에 실제로 넣은 헤더와 그 단계(단위 키 → 기록)입니다. 모듈이 그 뒤 더 배워도 다시 시작할 때 같은 합성 TU를 쓰게 하고
    // (내용이 바뀌면 clangd가 성공한 단위도 다시 색인함), 실패만 기록된 다음 단계에 더 넣을 헤더가 있는지 판단합니다.
    private readonly Dictionary<string, UnitSupplement> unitSupplements;
    // 보충 단계까지 해 보고도 실패해 PCH로 바꾼 단위(단위 키 → 구성 지문)입니다. 파일 하나짜리 단위면 문서도 처음부터 PCH로 엽니다.
    private readonly Dictionary<string, string> exhausted;
    // 파일 폴더별 소속 모듈 폴더입니다. 모듈 찾기는 위쪽 폴더를 열거하므로 기억합니다.
    private readonly ConcurrentDictionary<string, string?> moduleOfDirectory = new(StringComparer.OrdinalIgnoreCase);
    // 이 세션에서 합성 TU를 열어 보고도 배운 헤더가 없었던 횟수(모듈 폴더별)입니다. gate로 보호합니다.
    private readonly Dictionary<string, int> fruitlessProbes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>모듈마다 배운 것 없이 합성 TU를 열어 보는 세션당 최대 횟수입니다. 이름 색인이 원인 헤더를 모르면 실패 묶음마다 열어 메모리만 씁니다.</summary>
    internal const int MaxFruitlessProbes = 2;

    // 판단 기록 파일은 이전 세션의 늦은 기록과 새 세션의 준비가 겹칠 수 있어 캐시 폴더마다 잠그고, 읽는 쪽이 쓰다 만 파일을 보지 않게 바꿔치기로 씁니다.
    private static readonly ConcurrentDictionary<string, object> DecisionLocks = new(StringComparer.OrdinalIgnoreCase);

    private UnrealIndexPlan(string directory, PathAliases paths, UnrealPchMode mode, bool supplementsEnabled, IReadOnlyList<Unit> units,
        IEnumerable<CompileCommand> commands, Dictionary<string, string> decisions, Dictionary<string, string> failedWithPch,
        Dictionary<string, string> stages, Dictionary<string, List<string>> supplements, Dictionary<string, UnitSupplement> unitSupplements,
        Dictionary<string, string> exhausted)
    {
        this.directory = directory;
        this.paths = paths;
        Mode = mode;
        SupplementsEnabled = supplementsEnabled;
        this.units = units;
        this.decisions = decisions;
        this.failedWithPch = failedWithPch;
        this.stages = stages;
        this.supplements = supplements;
        this.unitSupplements = unitSupplements;
        this.exhausted = exhausted;
        foreach (var unit in units)
        {
            unitOfName[unit.Name] = unit;
            foreach (var member in unit.Members)
            {
                unitOfMember[FullPath(member)] = unit;
                var stem = Path.GetFileNameWithoutExtension(member);
                if (!unitsOfStem.TryGetValue(stem, out var list)) unitsOfStem[stem] = list = new List<Unit>();
                if (!list.Contains(unit)) list.Add(unit);
            }
        }

        foreach (var command in commands) commandOfFile[FullPath(command.File)] = command;
        GroupedUnitCount = units.Count(u => u.Members.Count > 1);
        GroupedMemberCount = units.Where(u => u.Members.Count > 1).Sum(u => u.Members.Count);
        SwitchableUnitCount = units.Count(u => u.Switchable);
    }

    public UnrealPchMode Mode { get; }

    /// <summary>자동에서 PCH 전에 헤더 보충을 합니다(자체 이름 색인이 있을 때).</summary>
    public bool SupplementsEnabled { get; }

    /// <summary>보충 헤더를 넣어 색인하는 단위 수입니다.</summary>
    public int SupplementUnitCount
    {
        get
        {
            lock (gate) return units.Count(UsesSupplement);
        }
    }

    /// <summary>구성원이 둘 이상인 unity 묶음 수입니다.</summary>
    public int GroupedUnitCount { get; }

    /// <summary>unity 묶음으로 색인하는 파일 수입니다.</summary>
    public int GroupedMemberCount { get; }

    /// <summary>PCH 헤더를 넣을지 고를 수 있는 단위 수입니다(빌드 명령에 PCH 헤더가 있는 단위).</summary>
    public int SwitchableUnitCount { get; }

    /// <summary>공유 PCH를 넣어 색인하는 단위 수입니다.</summary>
    public int PchUnitCount
    {
        get
        {
            lock (gate) return units.Count(UsesPch);
        }
    }

    internal string UnitsDirectory => Path.Combine(directory, UnitsFolder);

    /// <param name="directory">VisualBoost 캐시 폴더입니다. 합성 TU와 판단 기록을 그 아래에 둡니다.</param>
    /// <param name="supplements">자동에서 PCH 전에 헤더 보충을 할지입니다. 보충 헤더를 고를 자체 이름 색인이 있을 때만 켭니다.</param>
    public static UnrealIndexPlan Create(string directory, UnrealCompileCommandResult result, PathAliases paths, UnrealPchMode mode, bool supplements = false)
    {
        var units = new List<Unit>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unity in result.Units.Where(u => u.Members.Count > 1).OrderBy(u => u.Source, StringComparer.OrdinalIgnoreCase))
        {
            var name = UniqueName(Path.GetFileNameWithoutExtension(unity.Source), names);
            units.Add(new Unit(unity.Source, name, unity.Directory, unity.Arguments, unity.Members));
            grouped.UnionWith(unity.Members);
        }

        foreach (var command in result.Commands.Where(c => !grouped.Contains(c.File)).OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase))
        {
            // 같은 이름의 다른 폴더 파일과 겹치지 않게 경로 해시를 붙입니다.
            var name = UniqueName(Path.GetFileNameWithoutExtension(command.File) + "-" + Hash(FullPath(command.File).ToUpperInvariant(), 4), names);
            units.Add(new Unit(command.File, name, command.Directory, command.Arguments.Take(command.Arguments.Count - 1).ToArray(), new[] { command.File }));
        }

        var decisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failedWithPch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var learned = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unitHeaders = new Dictionary<string, UnitSupplement>(StringComparer.OrdinalIgnoreCase);
        var exhausted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var enabled = supplements && mode == UnrealPchMode.Auto;
        if (mode == UnrealPchMode.Auto) ReadDecisions(directory, decisions, failedWithPch, stages, learned, unitHeaders, exhausted);
        foreach (var unit in units)
        {
            unit.NeedsPch = mode == UnrealPchMode.Always ||
                            mode == UnrealPchMode.Auto && decisions.TryGetValue(unit.Key, out var stamp) && stamp == unit.Stamp;
            if (mode == UnrealPchMode.Auto && !unit.NeedsPch && unit.Switchable && StageOf(stages, unit) is int stage)
            {
                // 보충 단계 판단이 있는데 이번 세션에 보충할 수 없으면(이름 색인 없음) PCH로 색인합니다. PCH 없는 원래 합성 TU는 실패했던 것과
                // 내용이 같아 clangd가 다시 색인하지 않으므로 그대로 두면 계속 실패한 결과로 남습니다.
                if (enabled) unit.Stage = stage;
                else unit.NeedsPch = true;
            }

            // PCH를 넣고도 분석 오류가 났던 단위(편집 중 문법 오류, 아직 생성하지 않은 헤더 등)는 PCH가 원인이 아니었을 수 있습니다.
            // 그 뒤 구성원 파일이 바뀌었으면 PCH 없이 다시 판단합니다. 바뀌었으므로 clangd가 PCH 없는 합성 TU를 다시 색인해 실패 여부를
            // 다시 알립니다. 그대로면 다시 색인되지 않아 판단을 바꿀 근거가 없으므로 PCH를 유지합니다.
            if (unit.NeedsPch && mode == UnrealPchMode.Auto && failedWithPch.TryGetValue(unit.Key, out var print) && print != Fingerprint(unit.Members))
            {
                unit.NeedsPch = false;
            }
        }

        // 구성이 바뀌어 더 쓰지 않는 판단은 버립니다. 파일에는 다음 판단을 기록할 때 반영됩니다.
        var current = new HashSet<string>(units.Where(u => u.NeedsPch).Select(u => u.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var stale in decisions.Keys.Where(k => !current.Contains(k)).ToArray()) decisions.Remove(stale);
        foreach (var stale in failedWithPch.Keys.Where(k => !current.Contains(k)).ToArray()) failedWithPch.Remove(stale);
        // 보충 단계 판단은 구성이 같고 PCH 판단이 없는 단위만 남깁니다. 이번 세션에 보충할 수 없어 PCH로 색인하는 단위도 남겨 다음 세션이 보충으로 시작합니다.
        var staged = new HashSet<string>(units.Where(u => !decisions.ContainsKey(u.Key) && StageOf(stages, u) is not null).Select(u => u.Key),
            StringComparer.OrdinalIgnoreCase);
        foreach (var stale in stages.Keys.Where(k => !staged.Contains(k)).ToArray()) stages.Remove(stale);
        foreach (var stale in unitHeaders.Keys.Where(k => !staged.Contains(k)).ToArray()) unitHeaders.Remove(stale);
        foreach (var stale in exhausted.Where(e => !decisions.TryGetValue(e.Key, out var stamp) || stamp != e.Value).Select(e => e.Key).ToArray()) exhausted.Remove(stale);
        // 지운 헤더(엔진 업데이트, 프로젝트 정리)는 넣으면 치명 오류가 나므로 뺍니다.
        foreach (var headers in learned.Values) headers.RemoveAll(h => !File.Exists(h));
        foreach (var record in unitHeaders.Values) record.Headers.RemoveAll(h => !File.Exists(h));
        var plan = new UnrealIndexPlan(directory, paths, mode, enabled, units, result.Commands, decisions, failedWithPch, stages, learned, unitHeaders, exhausted);
        var nothingToAdd = new List<Unit>();
        var recorded = false;
        foreach (var unit in units.Where(u => u.Stage > 0))
        {
            unitHeaders.TryGetValue(unit.Key, out var own);
            if (own is { Headers.Count: > 0 } && own.Stage == unit.Stage)
            {
                unit.SupplementHeaders = own.Headers.ToArray();
                continue;
            }

            // 전환 전에 끝난 세션은 실패만 기록했습니다. 전환할 때처럼 모듈이 배운 헤더를 모두 넣되, 이전 단계보다 더 넣을 헤더가 없으면
            // 이전과 같은 내용을 새 경로로 다시 색인해 또 실패할 뿐이므로 PCH로 바꿉니다(피드백 검토 67).
            var module = plan.SupplementsOfModule(plan.ModuleOfUnit(unit));
            IReadOnlyList<string>? previous = own is not null && own.Stage == unit.Stage - 1 ? own.Headers : unit.Stage == 1 ? Array.Empty<string>() : null;
            if (module.Count == 0 || previous is not null && module.All(h => previous.Contains(h, StringComparer.OrdinalIgnoreCase)))
            {
                nothingToAdd.Add(unit);
                continue;
            }

            // 이 단계에 넣은 헤더를 기록해 다음 단계와 다시 시작할 때 비교합니다(전환할 때와 같음).
            unit.SupplementHeaders = module;
            unitHeaders[unit.Key] = new UnitSupplement(unit.Stage, module.ToList());
            recorded = true;
        }

        if (nothingToAdd.Count > 0) plan.PchWithoutSupplement(nothingToAdd);
        else if (recorded) plan.SaveDecisions();
        return plan;
    }

    /// <summary>기록한 보충 단계입니다. 구성 지문이 다르거나 기록이 없으면 null입니다.</summary>
    private static int? StageOf(Dictionary<string, string> stages, Unit unit)
    {
        if (!stages.TryGetValue(unit.Key, out var value)) return null;
        var colon = value.LastIndexOf(':');
        return colon > 0 && value.Substring(0, colon) == unit.Stamp &&
               int.TryParse(value.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var stage) && stage >= 1 && stage <= MaxSupplementStage
            ? stage
            : null;
    }

    /// <summary>
    /// 그 이름(확장자 제외)의 구성원 cpp를 색인하는 단위의 clangd 대기열 표시(합성 TU 파일 이름에서 확장자를 뺀 것)입니다
    /// (<see cref="IndexQueuePriority"/>). <paramref name="exceptMember"/>를 담은 단위는 뺍니다. 연 문서는 자기 명령으로 따로 색인되므로 그
    /// 단위까지 올리면 같은 분석을 앞에서 두 번 합니다. 자동 PCH에서 아직 PCH 없이 색인할 단위는 실패하면 <c>.pch.cpp</c>로 바뀌어 표시가
    /// 달라지므로 그 표시도 함께 돌려줍니다(clangd는 올린 표시를 나중에 들어온 작업에도 적용함).
    /// </summary>
    public IReadOnlyList<string> QueueTags(string stem, string? exceptMember = null)
    {
        lock (gate)
        {
            if (!unitsOfStem.TryGetValue(stem, out var list)) return Array.Empty<string>();
            var except = exceptMember is null ? null : unitOfMember.TryGetValue(FullPath(exceptMember), out var own) ? own : null;
            var tags = new List<string>();
            foreach (var unit in list)
            {
                if (ReferenceEquals(unit, except)) continue;
                tags.Add(IndexQueuePriority.TagOf(WrapperPath(unit)));
                if (Mode != UnrealPchMode.Auto || !unit.Switchable || UsesPch(unit)) continue;
                // 실패하면 바뀔 다음 단계의 표시입니다(보충 단계, 그다음 PCH).
                if (SupplementsEnabled && unit.Stage < MaxSupplementStage) tags.Add(IndexQueuePriority.TagOf(unit.Name + StageSuffix(unit.Stage + 1)));
                tags.Add(unit.Name + ".pch");
            }

            return tags;
        }
    }

    /// <summary>합성 TU로 색인하는 파일인지 봅니다.</summary>
    public bool IsMember(string path) => unitOfMember.ContainsKey(FullPath(path));

    /// <summary>그 파일이 속한 단위가 공유 PCH를 넣어 색인하는지 봅니다.</summary>
    public bool UnitUsesPch(string member)
    {
        lock (gate) return unitOfMember.TryGetValue(FullPath(member), out var unit) && UsesPch(unit);
    }

    /// <summary>
    /// 문서로 열 때 덮어쓰기로 줄 명령입니다. 구성원 cpp는 자기 명령을, 프로젝트 헤더는 소속 모듈의 cpp 명령(같은 이름 cpp 우선)을 헤더로
    /// 옮겨 씁니다. database의 명령이 모두 캐시 폴더의 합성 TU라 clangd가 가까운 파일로 고른 추정 명령은 다른 모듈의 것일 수 있기 때문입니다.
    /// 해당 없으면 null입니다.
    /// </summary>
    /// <remarks>
    /// <see cref="UnrealPchMode.Auto"/>는 묶음 구성원과 헤더를 단위 판단과 상관없이 PCH 없이 시작합니다. 묶음은 구성원 하나만 PCH에 기대도
    /// PCH로 바뀌는데, 그 묶음의 다른 문서까지 PCH로 열면 문서 하나에 4 GB 넘게 들었습니다(테스트 전용 UE 샘플, 2026-10-09). 혼자 열어 실패하는
    /// 문서는 호출자가 진단을 보고 PCH를 넣어 다시 분석합니다. 파일 하나만 담은 단위는 단위 판단이 곧 그 문서의 판단이므로 PCH 단위면
    /// 처음부터 넣어, PCH 없는 분석을 한 번 더 하지 않습니다(피드백 검토 38: 실제 프로젝트 첫 요청 9.8~13.0초).
    /// </remarks>
    /// <param name="pch">PCH 헤더를 넣을지입니다. null이면 <see cref="UnrealPchMode.Always"/>이거나 자동에서 파일 하나짜리 PCH 단위일 때 넣습니다.</param>
    /// <param name="extra">PCH 없는 명령에 모듈 보충 헤더 뒤로 더 넣을 헤더입니다(그 문서의 분석에서 고른 것, 모듈에는 배우지 않음).</param>
    public UnrealDocumentCommand? DocumentCommand(string path, bool? pch = null, IEnumerable<string>? extra = null)
    {
        var header = DefinitionCandidates.IsHeader(path);
        var source = header ? HeaderSource(path) : IsMember(path) ? FullPath(path) : null;
        if (source is null || !commandOfFile.TryGetValue(source, out var command)) return null;
        var head = command.Arguments.Take(command.Arguments.Count - 1).ToArray();
        var stripped = UnrealCompileCommands.RemovePrecompiledHeaders(head);
        var hasPch = stripped.Count != head.Length;
        var withPch = pch ?? (Mode == UnrealPchMode.Always || Mode == UnrealPchMode.Auto && !header && SingleUnitUsesPch(source));
        var supplied = hasPch && !withPch && SupplementsEnabled ? WithExtra(SupplementsOfModule(ModuleOf(path)), extra) : Array.Empty<string>();
        var arguments = new List<string>(withPch || !hasPch ? head : IncludeSupplements.WithForcedIncludes(stripped, supplied.Select(paths.ToReal)));
        // PCH 없이 열면 조건식의 정의되지 않은 매크로를 경고로 받아, PCH가 정의하던 매크로에 기대는 문서도 PCH로 다시 분석하게 합니다
        // (분석 오류가 없어 그 구역이 조용히 비활성이 되기 때문, ConditionMacros 참고).
        if (hasPch && !withPch) arguments.Add("-Wundef");
        // 문서 경로 그대로 씁니다. clangd는 명령을 바꾼 열린 문서를 경로 문자열이 같을 때만 다시 분석합니다(ClangdSession.UpdateCompileCommands).
        var file = FullPath(path);
        if (header && !arguments.Contains("/TP"))
        {
            // 확장자로 언어를 정할 수 없는 헤더는 C++로 분석하게 합니다(clangd가 추정 명령을 옮길 때와 같은 처리).
            arguments.Add("/TP");
        }

        arguments.Add(file);
        return new UnrealDocumentCommand(new CompileCommand(command.Directory, file, arguments), hasPch && !withPch, supplied);
    }

    /// <summary>
    /// 문서·합성 TU가 속한 모듈에 보충 헤더를 배웁니다. 새로 배운 헤더를 돌려주며, 있으면 판단 기록에 바로 남깁니다. 모듈을 모르거나 상한
    /// (<see cref="IncludeSupplements.MaxHeadersPerModule"/>)에 닿으면 배우지 않습니다. 파일을 쓰므로 작업 스레드에서 부릅니다.
    /// </summary>
    /// <param name="file">문서 경로이거나 이 계획의 합성 TU 경로입니다.</param>
    public IReadOnlyList<string> Learn(string file, IEnumerable<string> headers)
    {
        if (!SupplementsEnabled) return Array.Empty<string>();
        lock (gate)
        {
            var module = UnitOfWrapper(file) is { } unit ? ModuleOfUnit(unit) : ModuleOf(file);
            if (module is null) return Array.Empty<string>();
            if (!supplements.TryGetValue(module, out var list)) supplements[module] = list = new List<string>();
            var added = new List<string>();
            foreach (var header in headers)
            {
                if (list.Count >= IncludeSupplements.MaxHeadersPerModule) break;
                var full = FullPath(header);
                if (list.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;
                list.Add(full);
                added.Add(full);
            }

            if (added.Count > 0) WriteDecisions();
            else if (UnitOfWrapper(file) is not null) fruitlessProbes[module] = (fruitlessProbes.TryGetValue(module, out var count) ? count : 0) + 1;
            return added;
        }
    }

    /// <summary>문서·합성 TU의 명령에 있는 include 폴더입니다(보충 헤더 후보를 고를 때). 명령을 모르면 빈 목록입니다.</summary>
    public IReadOnlyList<string> IncludeDirectoriesOf(string file)
    {
        if (CommandOfWrapper(file) is { } command) return IncludeSupplements.IncludeDirectories(command);
        return DocumentCommand(file, pch: false) is { } choice ? IncludeSupplements.IncludeDirectories(choice.Command) : Array.Empty<string>();
    }

    /// <summary>이 계획의 합성 TU를 지금 색인하는 명령입니다(실패 원인을 컴파일러로 볼 때). 합성 TU가 아니면 null입니다.</summary>
    public CompileCommand? CommandOfWrapper(string wrapper)
    {
        lock (gate) return UnitOfWrapper(wrapper) is { } unit ? DatabaseCommand(unit) : null;
    }

    /// <summary>
    /// clangd가 분석 오류를 알린 합성 TU 중 실패 원인(모르는 이름)을 검사해 볼 것입니다. 보충 단계가 아닌 단위는 모듈마다 하나를, 그 모듈이 아직
    /// 배운 헤더가 없을 때만 고릅니다(배운 헤더가 있으면 먼저 그것으로 다시 색인). 보충 단계에서 실패한 단위는 더 배울 수 있으면 모두 고릅니다.
    /// </summary>
    public IReadOnlyList<string> ProbeTargets(IEnumerable<string> translationUnits)
    {
        if (!SupplementsEnabled) return Array.Empty<string>();
        lock (gate)
        {
            var targets = new List<string>();
            var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in translationUnits)
            {
                if (UnitOfWrapper(path) is not { Switchable: true, NeedsPch: false } unit) continue;
                var module = ModuleOfUnit(unit);
                if (module is null) continue;
                var known = SupplementsOfModule(module).Count;
                if (fruitlessProbes.TryGetValue(module, out var fruitless) && fruitless >= MaxFruitlessProbes) continue;
                if (unit.Stage == 0 ? known == 0 && modules.Add(module) : unit.Stage < MaxSupplementStage && known < IncludeSupplements.MaxHeadersPerModule)
                {
                    targets.Add(WrapperPath(unit));
                }
            }

            return targets;
        }
    }

    /// <summary>
    /// clangd가 분석 오류를 알린 단위를 다음 단계로 바꿉니다(<see cref="UnrealPchMode.Auto"/>만). 모듈이 배운 보충 헤더 중 그 단위에 아직 넣지 않은
    /// 것이 있으면 보충 단계(<see cref="MaxSupplementStage"/>까지)로, 없으면 PCH로 바꿉니다. 보충을 하지 않으면 <see cref="MarkNeedsPch"/>와
    /// 같습니다. 바꾼 단위의 새 합성 TU 명령을 돌려줍니다.
    /// </summary>
    public IReadOnlyList<CompileCommand> SwitchFailed(IEnumerable<string> translationUnits)
    {
        if (!SupplementsEnabled) return MarkNeedsPch(translationUnits);
        lock (gate)
        {
            var toPch = new List<Unit>();
            var toSupplement = new List<Unit>();
            foreach (var path in translationUnits)
            {
                if (UnitOfWrapper(path) is not { Switchable: true, NeedsPch: false } unit || toPch.Contains(unit) || toSupplement.Contains(unit)) continue;
                var headers = SupplementsOfModule(ModuleOfUnit(unit));
                var more = headers.Any(h => !unit.SupplementHeaders.Contains(h, StringComparer.OrdinalIgnoreCase));
                if (unit.Stage < MaxSupplementStage && more) toSupplement.Add(unit);
                else toPch.Add(unit);
            }

            var switched = new List<CompileCommand>();
            foreach (var unit in toSupplement)
            {
                var previous = WrapperPath(unit);
                var (stage, headers) = (unit.Stage, unit.SupplementHeaders);
                unit.Stage++;
                unit.SupplementHeaders = SupplementsOfModule(ModuleOfUnit(unit));
                try
                {
                    WriteWrapper(unit);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    // 새 합성 TU를 쓰지 못하면 이번에는 바꾸지 않습니다. 기록한 판단으로 다음 세션이 이 단계로 시작합니다.
                    (unit.Stage, unit.SupplementHeaders) = (stage, headers);
                    continue;
                }

                TryDelete(previous);
                stages[unit.Key] = unit.Stamp + ":" + unit.Stage.ToString(CultureInfo.InvariantCulture);
                unitSupplements[unit.Key] = new UnitSupplement(unit.Stage, unit.SupplementHeaders.ToList());
                switched.Add(DatabaseCommand(unit));
            }

            switched.AddRange(PchLocked(toPch));
            foreach (var unit in toPch.Where(u => u.Stage > 0 && u.NeedsPch)) exhausted[unit.Key] = unit.Stamp;
            if (switched.Count == 0) return switched;
            TryWriteDatabase();
            WriteDecisions();
            return switched;
        }
    }

    private static IReadOnlyList<string> WithExtra(IReadOnlyList<string> headers, IEnumerable<string>? extra)
    {
        if (extra is null) return headers;
        var result = headers.ToList();
        foreach (var header in extra.Select(FullPath))
        {
            if (!result.Contains(header, StringComparer.OrdinalIgnoreCase)) result.Add(header);
        }

        return result;
    }

    /// <summary>그 모듈이 배운 보충 헤더입니다(배운 순서).</summary>
    private IReadOnlyList<string> SupplementsOfModule(string? module)
    {
        lock (gate) return module is not null && supplements.TryGetValue(module, out var list) ? list.ToArray() : Array.Empty<string>();
    }

    private string? ModuleOfUnit(Unit unit) => unit.Members.Count == 0 ? null : ModuleOf(unit.Members[0]);

    /// <summary>파일이 속한 모듈 폴더(<c>*.Build.cs</c>가 있는 폴더)입니다. 모르면 null입니다.</summary>
    private string? ModuleOf(string file)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(file));
        if (folder is null) return null;
        return moduleOfDirectory.GetOrAdd(folder, _ => UnrealCompileCommands.OwningModule(file) is (string module, _) ? FullPath(module) : null);
    }

    /// <summary>합성 TU를 모두 쓰고 쓰지 않는 합성 TU를 지운 뒤 compile_commands.json을 씁니다. database 내용이 바뀌었으면 참입니다.</summary>
    public bool Write()
    {
        lock (gate)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in units)
            {
                names.Add(Path.GetFileName(WrapperPath(unit)));
                WriteWrapper(unit);
            }

            RemoveStaleUnits(names);
            return WriteDatabaseLocked();
        }
    }

    /// <summary>
    /// clangd가 분석 오류를 알린 TU의 판단을 바로 기록합니다(<see cref="UnrealPchMode.Auto"/>만). PCH 없이 색인한 단위는 다음 세션부터 PCH로
    /// 색인하도록 판단을 남기고(전환은 <see cref="MarkNeedsPch"/>), PCH로 색인한 단위는 그때의 구성원 파일 상태를 남깁니다. 실패를 모아 전환하기
    /// 전에 clangd를 다시 시작해도 판단을 잃지 않게 하려는 것입니다. 작은 파일 하나를 쓰므로 실패 알림을 받은 스레드에서 불러도 됩니다.
    /// </summary>
    /// <param name="translationUnit">clangd가 알린 TU 경로입니다. 이 계획의 합성 TU가 아니면 무시합니다.</param>
    public void RecordFailure(string translationUnit)
    {
        if (Mode != UnrealPchMode.Auto) return;
        lock (gate)
        {
            if (UnitOfWrapper(translationUnit) is not { Switchable: true } unit) return;
            if (UsesPch(unit))
            {
                failedWithPch[unit.Key] = Fingerprint(unit.Members);
            }
            else if (SupplementsEnabled && unit.Stage < MaxSupplementStage)
            {
                // 다음 세션은 다음 보충 단계로 시작합니다(새 경로라 다시 색인). 이번 세션의 전환에서 배울 헤더가 없으면 PCH로 다시 기록합니다.
                var next = unit.Stamp + ":" + (unit.Stage + 1).ToString(CultureInfo.InvariantCulture);
                if (stages.TryGetValue(unit.Key, out var recorded) && recorded == next) return;
                // 다음 단계에 넣을 헤더는 전환할 때 정해집니다. 그 전에 끝나면 다음 세션이 이번 단계의 헤더 기록과 모듈 헤더를 비교해 정합니다.
                stages[unit.Key] = next;
            }
            else if (!decisions.TryGetValue(unit.Key, out var stamp) || stamp != unit.Stamp)
            {
                decisions[unit.Key] = unit.Stamp;
            }
            else
            {
                return;
            }

            WriteDecisions();
        }
    }

    /// <summary>
    /// clangd가 분석 오류를 알린 TU 중 공유 PCH 없이 색인한 단위를 PCH로 바꿉니다(<see cref="UnrealPchMode.Auto"/>만).
    /// 바꾼 단위의 새 합성 TU 명령을 돌려줍니다. 호출자는 clangd에 덮어쓰기로 보내 바로 색인하게 합니다. 파일을 쓰므로 UI thread에서 부르지 않습니다.
    /// </summary>
    /// <param name="translationUnits">clangd가 알린 TU 경로입니다. 합성 TU가 아닌 경로는 무시합니다.</param>
    public IReadOnlyList<CompileCommand> MarkNeedsPch(IEnumerable<string> translationUnits)
    {
        if (Mode != UnrealPchMode.Auto) return Array.Empty<CompileCommand>();
        lock (gate)
        {
            var switching = new List<Unit>();
            foreach (var path in translationUnits)
            {
                if (UnitOfWrapper(path) is { Switchable: true, NeedsPch: false } unit && !switching.Contains(unit)) switching.Add(unit);
            }

            return SwitchLocked(switching);
        }
    }

    private IReadOnlyList<CompileCommand> SwitchLocked(IReadOnlyList<Unit> switching)
    {
        var switched = PchLocked(switching);
        if (switched.Count == 0) return switched;
        TryWriteDatabase();
        WriteDecisions();
        return switched;
    }

    /// <summary>단위를 PCH 합성 TU로 바꿉니다. database와 판단 기록은 호출자가 씁니다. <see cref="gate"/> 안에서 부릅니다.</summary>
    private List<CompileCommand> PchLocked(IReadOnlyList<Unit> switching)
    {
        var switched = new List<CompileCommand>(switching.Count);
        foreach (var unit in switching)
        {
            var previous = WrapperPath(unit);
            unit.NeedsPch = true;
            try
            {
                WriteWrapper(unit);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // 새 합성 TU를 쓰지 못하면 이번에는 바꾸지 않습니다. 같은 단위가 다시 실패를 알리면 다시 시도합니다.
                unit.NeedsPch = false;
                continue;
            }

            TryDelete(previous);
            decisions[unit.Key] = unit.Stamp;
            stages.Remove(unit.Key);
            unitSupplements.Remove(unit.Key);
            switched.Add(DatabaseCommand(unit));
        }

        return switched;
    }

    /// <summary>
    /// 시작할 때 더 넣을 보충 헤더가 없는 보충 단계 단위를 PCH 판단으로 바꿉니다. 합성 TU와 database는 이어서 <see cref="Write"/>가 씁니다.
    /// </summary>
    private void PchWithoutSupplement(IReadOnlyList<Unit> pending)
    {
        lock (gate)
        {
            foreach (var unit in pending)
            {
                unit.Stage = 0;
                unit.SupplementHeaders = Array.Empty<string>();
                unit.NeedsPch = true;
                decisions[unit.Key] = unit.Stamp;
                stages.Remove(unit.Key);
                unitSupplements.Remove(unit.Key);
            }

            WriteDecisions();
        }
    }

    private void SaveDecisions()
    {
        lock (gate) WriteDecisions();
    }

    private void TryWriteDatabase()
    {
        try
        {
            // 다음 시작과 clangd가 database를 다시 읽을 때도 같은 명령을 보게 합니다.
            WriteDatabaseLocked();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // 이번 세션은 덮어쓰기 명령으로 색인하고, 다음 시작 때 판단 기록으로 database를 다시 씁니다.
        }
    }

    private bool WriteDatabaseLocked() => CompileContextBuilder.WriteDatabase(directory, paths.ToReal(units.Select(DatabaseCommand).ToArray()));

    private CompileCommand DatabaseCommand(Unit unit)
    {
        var path = WrapperPath(unit);
        return new CompileCommand(unit.Directory, path, (UsesPch(unit) ? unit.WithPch : unit.WithoutPch).Concat(new[] { path }).ToArray());
    }

    private string WrapperPath(Unit unit) =>
        Path.Combine(UnitsDirectory, unit.Name + (UsesPch(unit) ? PchSuffix : StageSuffix(UsesSupplement(unit) ? unit.Stage : 0))).Replace('\\', '/');

    private static string StageSuffix(int stage) => stage switch
    {
        1 => SupplementSuffix,
        2 => SecondSupplementSuffix,
        _ => ".cpp"
    };

    private static bool UsesPch(Unit unit) => unit.NeedsPch && unit.Switchable;

    private static bool UsesSupplement(Unit unit) => !UsesPch(unit) && unit.Switchable && unit.Stage > 0;

    /// <summary>공유 PCH나 보충 헤더를 넣어 다시 색인하는 합성 TU 경로입니다. 이런 TU를 색인했으면 다시 읽기 재시작이 필요합니다.</summary>
    public static bool IsSwitchedWrapper(string path) =>
        path.EndsWith(PchSuffix, StringComparison.OrdinalIgnoreCase) || path.EndsWith(SupplementSuffix, StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(SecondSupplementSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>합성 TU 경로에 해당하는 단위입니다. 지금 쓰는 경로(단계에 맞는 확장자)가 아니면 null입니다. <see cref="gate"/> 안에서 부릅니다.</summary>
    private Unit? UnitOfWrapper(string path)
    {
        var name = Path.GetFileName(path);
        var suffix = new[] { PchSuffix, SupplementSuffix, SecondSupplementSuffix }.FirstOrDefault(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        var stem = suffix is not null ? name.Substring(0, name.Length - suffix.Length) : Path.GetFileNameWithoutExtension(name);
        return unitOfName.TryGetValue(stem, out var unit) && string.Equals(FullPath(path), FullPath(WrapperPath(unit)), StringComparison.OrdinalIgnoreCase)
            ? unit
            : null;
    }

    /// <summary>
    /// 파일 하나짜리 단위가 PCH로 색인해, 그 문서도 처음부터 PCH로 열지 봅니다. 헤더 보충을 하면 보충까지 해 보고도 실패한 단위만 그렇습니다.
    /// 이전 판(보충 없음)이 남긴 PCH 판단이나 배운 헤더 없이 PCH로 바꾼 단위의 문서는 보충 헤더로 먼저 열어, 공유 PCH(문서 하나 약 2 GB)
    /// 대신 필요한 헤더만 넣게 합니다.
    /// </summary>
    private bool SingleUnitUsesPch(string member)
    {
        lock (gate)
        {
            return unitOfMember.TryGetValue(member, out var unit) && unit.Members.Count == 1 && UsesPch(unit) &&
                   (!SupplementsEnabled || exhausted.TryGetValue(unit.Key, out var stamp) && stamp == unit.Stamp);
        }
    }

    /// <summary>구성원 파일의 크기·수정 시각을 묶은 값입니다. 내용 해시보다 싸고, 판단을 다시 할지 정하는 데만 씁니다.</summary>
    private static string Fingerprint(IEnumerable<string> members)
    {
        var text = new StringBuilder();
        foreach (var member in members)
        {
            var info = new FileInfo(member);
            text.Append(member).Append('|');
            if (info.Exists) text.Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('|').Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            text.Append('\n');
        }

        return Hash(text.ToString(), 8);
    }

    /// <summary>합성 TU를 씁니다. 내용이 같으면 다시 쓰지 않습니다.</summary>
    private void WriteWrapper(Unit unit)
    {
        var text = new StringBuilder("// VisualBoost 색인 단위: ").Append(unit.Key).Append('\n');
        if (Mode == UnrealPchMode.Never && unit.Switchable)
        {
            // clangd는 내용이 같으면 오류가 있던 TU를 다시 색인하지 않으므로, 넣지 않음으로 색인한 결과가 자동으로 바꾼 뒤에도 남아 실패를
            // 다시 알리지 않습니다. 내용을 달리해 방식을 바꿀 때 다시 색인하게 합니다.
            text.Append("// 공유 PCH 넣지 않음\n");
        }

        if (UsesSupplement(unit))
        {
            // 공유 PCH 대신 모듈이 배운 헤더를 구성원보다 먼저 넣습니다. 명령의 강제 include(모듈 정의 헤더) 뒤라 API 매크로가 정의되어 있습니다.
            text.Append("// 보충 헤더(").Append(unit.Stage.ToString(CultureInfo.InvariantCulture)).Append("단계)\n");
            foreach (var header in unit.SupplementHeaders) text.Append("#include \"").Append(paths.ToReal(header)).Append("\"\n");
        }

        foreach (var member in unit.Members)
        {
            // clangd에는 실제 경로를 줍니다(PathAliases 참고). Windows 경로에는 따옴표가 올 수 없습니다.
            text.Append("#include \"").Append(paths.ToReal(member)).Append("\"\n");
        }

        if (Mode == UnrealPchMode.Auto && unit.Switchable && !UsesPch(unit)) AppendConditionChecks(text, unit.Members);
        var path = WrapperPath(unit);
        var content = text.ToString();
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// 구성원의 조건식(<c>#if</c>)에 쓴 매크로가 TU 끝에서 정의되지 않았으면 <c>#error</c>를 내게 합니다. PCH가 정의하던 매크로(예:
    /// <c>WITH_AUTOMATION_TESTS</c>)가 없으면 clang은 그 구역을 오류 없이 비활성으로 분석해 정의·참조를 잃으므로, 분석 오류로 알려
    /// PCH 단위로 바꾸게 합니다(<see cref="ConditionMacros"/>). PCH 단위의 합성 TU에는 넣지 않습니다.
    /// </summary>
    private static void AppendConditionChecks(StringBuilder text, IEnumerable<string> members)
    {
        var names = new List<string>();
        foreach (var member in members)
        {
            var source = SourceLinePreview.ReadText(member);
            if (source is null) continue;
            foreach (var name in ConditionMacros.Used(source))
            {
                if (!names.Contains(name)) names.Add(name);
            }
        }

        for (var i = 0; i < names.Count; i += 16)
        {
            text.Append("#if ").Append(string.Join(" || ", names.Skip(i).Take(16).Select(n => "!defined(" + n + ")"))).Append('\n')
                .Append("#error VisualBoost: condition macro undefined without shared PCH\n#endif\n");
        }
    }

    /// <summary>이번에 쓰지 않은 합성 TU를 지웁니다. 지우지 못해도 database에 없으므로 색인에는 영향이 없어 건너뜁니다.</summary>
    private void RemoveStaleUnits(ISet<string> keep)
    {
        string[] files;
        try
        {
            files = Directory.Exists(UnitsDirectory) ? Directory.GetFiles(UnitsDirectory, "*.cpp") : Array.Empty<string>();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files.Where(f => !keep.Contains(Path.GetFileName(f)))) TryDelete(file);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
        }
    }

    /// <summary>헤더의 소속 모듈에서 명령을 빌릴 cpp입니다. 같은 이름 cpp를 먼저, 없으면 경로 순서로 첫 cpp를 고릅니다.</summary>
    private string? HeaderSource(string header)
    {
        if (UnrealCompileCommands.OwningModule(header) is not (string moduleDirectory, _)) return null;
        var prefix = FullPath(moduleDirectory).TrimEnd('/') + "/";
        var stem = Path.GetFileNameWithoutExtension(header);
        string? first = null;
        foreach (var file in commandOfFile.Keys)
        {
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase)) return file;
            if (first is null || StringComparer.OrdinalIgnoreCase.Compare(file, first) < 0) first = file;
        }

        return first;
    }

    private void WriteDecisions()
    {
        static JsonValue Map(Dictionary<string, string> map) =>
            JsonValue.Object(map.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase).Select(d => new KeyValuePair<string, JsonValue>(d.Key, d.Value)));

        static JsonValue List(IEnumerable<string> headers) => JsonValue.Array(headers.Select(h => (JsonValue)h));

        var learned = JsonValue.Object(supplements.Where(s => s.Value.Count > 0).OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Select(s => new KeyValuePair<string, JsonValue>(s.Key, List(s.Value))));
        var perUnit = JsonValue.Object(unitSupplements.Where(s => s.Value.Headers.Count > 0).OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Select(s => new KeyValuePair<string, JsonValue>(s.Key, JsonValue.Object(("stage", s.Value.Stage), ("headers", List(s.Value.Headers))))));
        var json = JsonValue.Object(("units", Map(decisions)), ("failedWithPch", Map(failedWithPch)), ("stages", Map(stages)),
            ("supplements", learned), ("unitSupplements", perUnit), ("exhausted", Map(exhausted))).ToJson();
        var path = Path.Combine(directory, DecisionsFileName);
        lock (DecisionLock(directory))
        {
            try
            {
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, json);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // 기록하지 못하면 이번 세션의 전환만 남습니다. 다음 세션은 이전 기록으로 시작합니다.
            }
        }
    }

    private static void ReadDecisions(string directory, Dictionary<string, string> decisions, Dictionary<string, string> failedWithPch,
        Dictionary<string, string> stages, Dictionary<string, List<string>> supplements, Dictionary<string, UnitSupplement> unitSupplements,
        Dictionary<string, string> exhausted)
    {
        lock (DecisionLock(directory))
        {
            try
            {
                var path = Path.Combine(directory, DecisionsFileName);
                if (!File.Exists(path)) return;
                var root = JsonValue.Parse(File.ReadAllText(path));
                foreach (var pair in root["units"].Properties)
                {
                    if (pair.Value.AsString() is string stamp) decisions[pair.Key] = stamp;
                }

                foreach (var pair in root["failedWithPch"].Properties)
                {
                    if (pair.Value.AsString() is string print) failedWithPch[pair.Key] = print;
                }

                foreach (var pair in root["exhausted"].Properties)
                {
                    if (pair.Value.AsString() is string stamp) exhausted[pair.Key] = stamp;
                }

                foreach (var pair in root["stages"].Properties)
                {
                    if (pair.Value.AsString() is string stage) stages[pair.Key] = stage;
                }

                foreach (var pair in root["supplements"].Properties)
                {
                    var headers = pair.Value.Items.Select(i => i.AsString()).OfType<string>().Take(IncludeSupplements.MaxHeadersPerModule).ToList();
                    if (headers.Count > 0) supplements[pair.Key] = headers;
                }

                foreach (var pair in root["unitSupplements"].Properties)
                {
                    var headers = pair.Value["headers"].Items.Select(i => i.AsString()).OfType<string>().Take(IncludeSupplements.MaxHeadersPerModule).ToList();
                    if (pair.Value["stage"].AsInt32() is int stage && stage >= 1 && stage <= MaxSupplementStage && headers.Count > 0)
                    {
                        unitSupplements[pair.Key] = new UnitSupplement(stage, headers);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is FormatException)
            {
                // 읽지 못한 기록은 버리고 PCH 없이 다시 판단합니다.
                decisions.Clear();
                failedWithPch.Clear();
                stages.Clear();
                exhausted.Clear();
                supplements.Clear();
                unitSupplements.Clear();
            }
        }
    }

    private static object DecisionLock(string directory) => DecisionLocks.GetOrAdd(Path.GetFullPath(directory), _ => new object());

    private static string UniqueName(string name, ISet<string> used)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++) candidate = name + "-" + n.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private static string Hash(string text, int bytes)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Take(bytes).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static string FullPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    /// <summary>보충 단계 단위에 넣은 헤더와 그 단계입니다(판단 기록 <c>unitSupplements</c>).</summary>
    private sealed class UnitSupplement
    {
        public UnitSupplement(int stage, List<string> headers)
        {
            Stage = stage;
            Headers = headers;
        }

        public int Stage { get; }

        public List<string> Headers { get; }
    }

    private sealed class Unit
    {
        public Unit(string key, string name, string directory, IReadOnlyList<string> withPch, IReadOnlyList<string> members)
        {
            Key = key;
            Name = name;
            Directory = directory;
            WithPch = withPch;
            WithoutPch = UnrealCompileCommands.RemovePrecompiledHeaders(withPch);
            Members = members;
            Switchable = WithoutPch.Count != WithPch.Count;
            // 인자(PCH 제외)나 구성원이 바뀌면 이전 판단을 쓰지 않습니다. 구성원 내용이 바뀌면 clangd가 다시 색인하며 다시 판단합니다.
            Stamp = Hash(string.Join("\n", WithoutPch) + "\n--\n" + string.Join("\n", members), 8);
        }

        /// <summary>UBT unity cpp 경로이거나 묶이지 않은 파일의 경로입니다.</summary>
        public string Key { get; }

        /// <summary>합성 TU 파일 이름(확장자 제외)입니다.</summary>
        public string Name { get; }

        public string Directory { get; }

        public IReadOnlyList<string> WithPch { get; }

        public IReadOnlyList<string> WithoutPch { get; }

        public IReadOnlyList<string> Members { get; }

        public bool Switchable { get; }

        public string Stamp { get; }

        /// <summary>PCH를 넣어 색인하기로 했습니다. <see cref="gate"/>로 보호합니다.</summary>
        public bool NeedsPch { get; set; }

        /// <summary>헤더 보충 단계입니다(0이면 보충 없음). <see cref="gate"/>로 보호합니다.</summary>
        public int Stage { get; set; }

        /// <summary>보충 단계의 합성 TU에 넣은 헤더입니다. <see cref="gate"/>로 보호합니다.</summary>
        public IReadOnlyList<string> SupplementHeaders { get; set; } = Array.Empty<string>();
    }
}
