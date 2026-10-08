using System;
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
    public UnrealDocumentCommand(CompileCommand command, bool withoutPch)
    {
        Command = command;
        WithoutPch = withoutPch;
    }

    public CompileCommand Command { get; }

    /// <summary>빌드 명령에 있던 PCH 헤더를 뺐습니다. 분석 오류가 나면 넣은 명령으로 바꿀 수 있습니다.</summary>
    public bool WithoutPch { get; }
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
/// </remarks>
public sealed class UnrealIndexPlan
{
    internal const string UnitsFolder = "units";
    internal const string DecisionsFileName = "pch-units.json";
    internal const string PchSuffix = ".pch.cpp";

    private readonly object gate = new();
    private readonly string directory;
    private readonly PathAliases paths;
    private readonly IReadOnlyList<Unit> units;
    private readonly Dictionary<string, Unit> unitOfMember = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CompileCommand> commandOfFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> decisions;

    private UnrealIndexPlan(string directory, PathAliases paths, UnrealPchMode mode, IReadOnlyList<Unit> units, IEnumerable<CompileCommand> commands,
        Dictionary<string, string> decisions)
    {
        this.directory = directory;
        this.paths = paths;
        Mode = mode;
        this.units = units;
        this.decisions = decisions;
        foreach (var unit in units)
        {
            foreach (var member in unit.Members) unitOfMember[FullPath(member)] = unit;
        }

        foreach (var command in commands) commandOfFile[FullPath(command.File)] = command;
        GroupedUnitCount = units.Count(u => u.Members.Count > 1);
        GroupedMemberCount = units.Where(u => u.Members.Count > 1).Sum(u => u.Members.Count);
        SwitchableUnitCount = units.Count(u => u.Switchable);
    }

    public UnrealPchMode Mode { get; }

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
    public static UnrealIndexPlan Create(string directory, UnrealCompileCommandResult result, PathAliases paths, UnrealPchMode mode)
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

        var decisions = mode == UnrealPchMode.Auto ? ReadDecisions(Path.Combine(directory, DecisionsFileName)) : new Dictionary<string, string>();
        foreach (var unit in units)
        {
            unit.NeedsPch = mode == UnrealPchMode.Always ||
                            mode == UnrealPchMode.Auto && decisions.TryGetValue(unit.Key, out var stamp) && stamp == unit.Stamp;
        }

        // 구성이 바뀌어 더 쓰지 않는 판단은 버립니다. 파일에는 다음 판단을 기록할 때 반영됩니다.
        var current = new HashSet<string>(units.Where(u => u.NeedsPch).Select(u => u.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var stale in decisions.Keys.Where(k => !current.Contains(k)).ToArray()) decisions.Remove(stale);
        return new UnrealIndexPlan(directory, paths, mode, units, result.Commands, decisions);
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
    /// <see cref="UnrealPchMode.Auto"/>는 단위 판단과 상관없이 PCH 없이 시작합니다. 단위는 구성원 하나만 PCH에 기대도 PCH로 바뀌는데, 그 단위의
    /// 다른 문서까지 PCH로 열면 문서 하나에 4 GB 넘게 들었습니다(테스트 전용 UE 샘플, 2026-10-09). 혼자 열어 실패하는 문서는 호출자가 진단을
    /// 보고 PCH를 넣어 다시 분석합니다.
    /// </remarks>
    /// <param name="pch">PCH 헤더를 넣을지입니다. null이면 <see cref="UnrealPchMode.Always"/>일 때만 넣습니다.</param>
    public UnrealDocumentCommand? DocumentCommand(string path, bool? pch = null)
    {
        var header = DefinitionCandidates.IsHeader(path);
        var source = header ? HeaderSource(path) : IsMember(path) ? FullPath(path) : null;
        if (source is null || !commandOfFile.TryGetValue(source, out var command)) return null;
        var head = command.Arguments.Take(command.Arguments.Count - 1).ToArray();
        var stripped = UnrealCompileCommands.RemovePrecompiledHeaders(head);
        var hasPch = stripped.Count != head.Length;
        var withPch = pch ?? Mode == UnrealPchMode.Always;
        var arguments = new List<string>(withPch || !hasPch ? head : stripped);
        // 문서 경로 그대로 씁니다. clangd는 명령을 바꾼 열린 문서를 경로 문자열이 같을 때만 다시 분석합니다(ClangdSession.UpdateCompileCommands).
        var file = FullPath(path);
        if (header && !arguments.Contains("/TP"))
        {
            // 확장자로 언어를 정할 수 없는 헤더는 C++로 분석하게 합니다(clangd가 추정 명령을 옮길 때와 같은 처리).
            arguments.Add("/TP");
        }

        arguments.Add(file);
        return new UnrealDocumentCommand(new CompileCommand(command.Directory, file, arguments), hasPch && !withPch);
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
    /// clangd가 분석 오류를 알린 TU 중 공유 PCH 없이 색인한 단위를 PCH로 바꿉니다(<see cref="UnrealPchMode.Auto"/>만).
    /// 바꾼 단위의 새 합성 TU 명령을 돌려줍니다. 호출자는 clangd에 덮어쓰기로 보내 바로 색인하게 합니다. 파일을 쓰므로 UI thread에서 부르지 않습니다.
    /// </summary>
    /// <param name="translationUnits">clangd가 알린 TU 경로입니다. 합성 TU가 아닌 경로는 무시합니다.</param>
    public IReadOnlyList<CompileCommand> MarkNeedsPch(IEnumerable<string> translationUnits)
    {
        if (Mode != UnrealPchMode.Auto) return Array.Empty<CompileCommand>();
        var failed = new HashSet<string>(translationUnits.Select(FullPath), StringComparer.OrdinalIgnoreCase);
        lock (gate)
        {
            return SwitchLocked(units.Where(u => u.Switchable && !u.NeedsPch && failed.Contains(FullPath(WrapperPath(u)))).ToArray());
        }
    }

    private IReadOnlyList<CompileCommand> SwitchLocked(IReadOnlyList<Unit> switching)
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
            switched.Add(DatabaseCommand(unit));
        }

        if (switched.Count == 0) return switched;
        try
        {
            // 다음 시작과 clangd가 database를 다시 읽을 때도 같은 명령을 보게 합니다.
            WriteDatabaseLocked();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // 이번 세션은 덮어쓰기 명령으로 색인하고, 다음 시작 때 판단 기록으로 database를 다시 씁니다.
        }

        WriteDecisions();
        return switched;
    }

    private bool WriteDatabaseLocked() => CompileContextBuilder.WriteDatabase(directory, paths.ToReal(units.Select(DatabaseCommand).ToArray()));

    private CompileCommand DatabaseCommand(Unit unit)
    {
        var path = WrapperPath(unit);
        return new CompileCommand(unit.Directory, path, (UsesPch(unit) ? unit.WithPch : unit.WithoutPch).Concat(new[] { path }).ToArray());
    }

    private string WrapperPath(Unit unit) => Path.Combine(UnitsDirectory, unit.Name + (UsesPch(unit) ? PchSuffix : ".cpp")).Replace('\\', '/');

    private static bool UsesPch(Unit unit) => unit.NeedsPch && unit.Switchable;

    /// <summary>합성 TU를 씁니다. 내용이 같으면 다시 쓰지 않습니다.</summary>
    private void WriteWrapper(Unit unit)
    {
        var text = new StringBuilder("// VisualBoost 색인 단위: ").Append(unit.Key).Append('\n');
        foreach (var member in unit.Members)
        {
            // clangd에는 실제 경로를 줍니다(PathAliases 참고). Windows 경로에는 따옴표가 올 수 없습니다.
            text.Append("#include \"").Append(paths.ToReal(member)).Append("\"\n");
        }

        var path = WrapperPath(unit);
        var content = text.ToString();
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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
        var json = JsonValue.Object(("units", JsonValue.Object(decisions.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
            .Select(d => new KeyValuePair<string, JsonValue>(d.Key, d.Value))))).ToJson();
        try
        {
            File.WriteAllText(Path.Combine(directory, DecisionsFileName), json);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // 기록하지 못하면 다음 세션에서 다시 PCH 없이 색인해 같은 판단에 이릅니다.
        }
    }

    private static Dictionary<string, string> ReadDecisions(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return result;
            foreach (var pair in JsonValue.Parse(File.ReadAllText(path))["units"].Properties)
            {
                if (pair.Value.AsString() is string stamp) result[pair.Key] = stamp;
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is FormatException)
        {
            // 읽지 못한 기록은 버리고 PCH 없이 다시 판단합니다.
            result.Clear();
        }

        return result;
    }

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
    }
}
