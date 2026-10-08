using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>마지막 Unreal 빌드에서 고른 대상·구성입니다(예: UnrealEditor / Development).</summary>
public sealed class UnrealBuildVariant
{
    public UnrealBuildVariant(string platform, string architecture, string target, string configuration, DateTime newestResponseFile)
    {
        Platform = platform;
        Architecture = architecture;
        Target = target;
        Configuration = configuration;
        NewestResponseFile = newestResponseFile;
    }

    public string Platform { get; }

    public string Architecture { get; }

    public string Target { get; }

    public string Configuration { get; }

    public DateTime NewestResponseFile { get; }

    public string RelativeDirectory => Path.Combine("Intermediate", "Build", Platform, Architecture, Target, Configuration);

    public override string ToString() => $"{Target} {Platform} {Configuration}";
}

/// <summary>UBT unity 묶음 하나입니다. 구성원 cpp는 실제 빌드에서 이 묶음 명령으로 함께 컴파일되었습니다.</summary>
public sealed class UnityUnit
{
    public UnityUnit(string source, string directory, IReadOnlyList<string> arguments, IReadOnlyList<string> members)
    {
        Source = source;
        Directory = directory;
        Arguments = arguments;
        Members = members;
    }

    /// <summary>UBT가 만든 unity cpp(Intermediate 아래)입니다.</summary>
    public string Source { get; }

    /// <summary>컴파일 작업 경로(Engine/Source)입니다.</summary>
    public string Directory { get; }

    /// <summary>source를 뺀 명령 인자(컴파일러 포함)입니다. 구성원 명령에서 마지막 source를 뺀 것과 같습니다.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// 색인할 구성원을 unity cpp의 포함 순서대로 담습니다. 생성 파일, 없는 파일, 파일별 응답 파일로 따로 컴파일된 파일은 뺍니다.
    /// 경로는 <see cref="UnrealCompileCommandResult.Commands"/>의 파일 경로와 같은 표기입니다.
    /// </summary>
    public IReadOnlyList<string> Members { get; }
}

public sealed class UnrealCompileCommandResult
{
    public UnrealCompileCommandResult(IReadOnlyList<CompileCommand> commands, UnrealBuildVariant variant, int modules, int responseFiles,
        int unityMembers, int skippedGenerated, int missingSources, int supplemented = 0, int unreadableDirectories = 0,
        IReadOnlyList<UnityUnit>? units = null)
    {
        Commands = commands;
        Variant = variant;
        Modules = modules;
        ResponseFiles = responseFiles;
        UnityMembers = unityMembers;
        SkippedGenerated = skippedGenerated;
        MissingSources = missingSources;
        Supplemented = supplemented;
        UnreadableDirectories = unreadableDirectories;
        Units = units ?? Array.Empty<UnityUnit>();
    }

    /// <summary>UBT unity 묶음입니다. 구성원은 <see cref="Commands"/>에도 파일별 명령으로 들어 있습니다.</summary>
    public IReadOnlyList<UnityUnit> Units { get; }

    /// <summary>응답 파일이 없어 같은 모듈 명령이나 근사 명령으로 보완한 프로젝트 소스 수입니다.</summary>
    public int Supplemented { get; }

    /// <summary>보완할 소스를 찾다가 접근 거부·경로 길이 초과 등으로 읽지 못해 건너뛴 폴더 수입니다.</summary>
    public int UnreadableDirectories { get; }

    public IReadOnlyList<CompileCommand> Commands { get; }

    public UnrealBuildVariant Variant { get; }

    public int Modules { get; }

    public int ResponseFiles { get; }

    public int UnityMembers { get; }

    public int SkippedGenerated { get; }

    public int MissingSources { get; }
}

/// <summary>
/// Unreal 프로젝트의 일반 빌드가 남긴 응답 파일로 clangd compilation database를 만듭니다.
/// </summary>
/// <remarks>
/// UBT를 다시 실행하지 않고 프로젝트·엔진 폴더에 아무것도 쓰지 않습니다. 근거와 측정은 R&D
/// `ClangdFindings`의 "일반 빌드 응답 파일 변환"에 있습니다.
/// - 파일별 응답 파일은 그대로, unity 응답 파일은 unity cpp의 #include 목록으로 개별 cpp에 펼치고 묶음 구성(<see cref="UnityUnit"/>)도
///   함께 돌려줍니다. 색인은 묶음 단위로 합니다(<see cref="CompileContextBuilder"/>).
/// - MSVC PCH(/Yu /Yc /Fp)와 출력·로그 옵션을 빼되, /Yu 대상인 PCH 헤더의 강제 include는 텍스트 포함으로 남깁니다.
///   Unreal 프로젝트 소스는 공유 PCH가 넣어 주는 엔진 헤더에 기대는 경우가 흔해, 빼면 실제 빌드는 통과하는 TU가 불완전 타입·
///   미선언 이름 오류로 분석되고 그 TU의 정의·참조가 색인에서 조용히 빠집니다(2026-10-09 검토: 실제 프로젝트 TU 82개 중 30개 오류,
///   포함하면 0개). TU마다 큰 헤더를 다시 분석하는 비용(첫 분석 약 2배)은 정확성을 위해 감수합니다. 엔진 cpp 근사 명령은
///   <see cref="Synthesize"/>에서 다시 뺍니다.
/// - 강제 include는 clang driver가 옆의 MSVC .pch를 자동 선택하지 않도록 -Xclang -include로 바꿉니다.
/// - UBT 컴파일 작업 경로는 항상 &lt;Engine&gt;/Engine/Source입니다.
/// </remarks>
public static class UnrealCompileCommands
{
    private const int MaxResponseFileBytes = 4 * 1024 * 1024;
    private static readonly Regex UnityInclude = new("^\\s*#include\\s+\"([^\"]+)\"", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // 응답 파일 없는 소스를 보완할 때 들어가지 않는 폴더입니다. 외부 라이브러리 소스와 Win64 대상으로 컴파일하지 않는
    // Unreal 플랫폼 전용 폴더(Windows·Win64·Microsoft는 남김)입니다.
    private static readonly HashSet<string> SkippedSupplementFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "ThirdParty", "Intermediate", "Binaries",
        "Android", "IOS", "TVOS", "VisionOS", "Mac", "Apple", "Linux", "LinuxArm64", "Unix", "HoloLens",
        "PS4", "PS5", "XboxOne", "XboxOneGDK", "XSX", "WinGDK", "Switch"
    };
    internal static readonly string[] DropWithValue = { "/experimental:log", "/sourceDependencies" };
    internal static readonly string[] DropPrefixes = { "/Yu", "/Yc", "/Fp", "/Fo", "/Fd", "/Fa", "/analyze", "/errorReport", "/d1", "/d2" };

    /// <summary>
    /// 프로젝트와 프로젝트 플러그인의 빌드 폴더에서 명령을 만들 대상·구성을 고릅니다.
    /// VS의 활성 Solution 구성(예: "Development Editor")과 맞는 것을 우선하고, 없으면 응답 파일이 가장 최근인 것을 고릅니다.
    /// </summary>
    /// <remarks>
    /// 마지막 빌드가 게임 빌드여도 에디터 구성으로 작업 중이면 에디터 모듈 명령이 필요하기 때문입니다.
    /// 공유 PCH 헤더 응답 파일(*.h.obj.rsp)만 있는 대상 폴더와 데이터베이스 생성 모드의 별도 폴더(*GCD)는 판정에서 뺍니다.
    /// </remarks>
    public static UnrealBuildVariant? DetectVariant(string projectDirectory, string? solutionConfiguration = null, string platform = "Win64",
        string architecture = "x64")
    {
        var candidates = new Dictionary<(string Target, string Configuration), DateTime>();
        foreach (var root in BuildRoots(projectDirectory, Path.Combine("Intermediate", "Build", platform, architecture)))
        {
            foreach (var target in SafeDirectories(root))
            {
                var targetName = Path.GetFileName(target);
                if (targetName.EndsWith("GCD", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var configuration in SafeDirectories(target))
                {
                    var newest = SafeDirectories(configuration)
                        .SelectMany(module => SafeFiles(module, "*.obj.rsp"))
                        .Where(file => !file.EndsWith(".h.obj.rsp", StringComparison.OrdinalIgnoreCase))
                        .Select(file => SafeWriteTime(file))
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max();
                    if (newest == DateTime.MinValue) continue;
                    var key = (targetName, Path.GetFileName(configuration));
                    candidates[key] = candidates.TryGetValue(key, out var known) && known > newest ? known : newest;
                }
            }
        }

        var preferred = candidates.Where(c => MatchesSolutionConfiguration(c.Key.Target, c.Key.Configuration, solutionConfiguration)).ToArray();
        var pool = preferred.Length > 0 ? preferred : candidates.ToArray();
        if (pool.Length == 0) return null;
        var best = pool.OrderByDescending(c => c.Value).First();
        return new UnrealBuildVariant(platform, architecture, best.Key.Target, best.Key.Configuration, best.Value);
    }

    /// <summary>
    /// Unreal Solution 구성 이름("Development Editor", "DebugGame", "Development Client")이 빌드 폴더의 대상·구성과 맞는지 봅니다.
    /// 두 번째 단어가 대상 종류이고, 없으면 게임 대상입니다.
    /// </summary>
    public static bool MatchesSolutionConfiguration(string target, string configuration, string? solutionConfiguration)
    {
        if (string.IsNullOrWhiteSpace(solutionConfiguration)) return false;
        var parts = solutionConfiguration!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (!string.Equals(parts[0], configuration, StringComparison.OrdinalIgnoreCase)) return false;
        if (parts.Length > 1) return target.EndsWith(parts[1], StringComparison.OrdinalIgnoreCase);
        return !new[] { "Editor", "Client", "Server" }.Any(kind => target.EndsWith(kind, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="overrideDirectory">
    /// 이 대상·구성에 빌드하지 않은 프로젝트 모듈의 근사 명령이 쓰는 API 매크로 재정의 헤더 폴더입니다(<see cref="Synthesize"/>).
    /// null이면 그런 모듈은 보완하지 않습니다.
    /// </param>
    public static UnrealCompileCommandResult Build(string projectDirectory, string engineRoot, UnrealBuildVariant variant, string compiler,
        CancellationToken cancellationToken = default, string? overrideDirectory = null)
    {
        var directory = Normalize(Path.Combine(engineRoot, "Engine", "Source"));
        var commands = new Dictionary<string, CompileCommand>(StringComparer.OrdinalIgnoreCase);
        var units = new List<UnityUnit>();
        int modules = 0, responseFiles = 0, unityMembers = 0, skippedGenerated = 0, missingSources = 0;
        foreach (var root in BuildRoots(projectDirectory, variant.RelativeDirectory))
        {
            foreach (var module in SafeDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                modules++;
                var sources = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                // 구성원이 명령을 받은 unity cpp입니다. 파일별 응답 파일이 있는 구성원은 따로 컴파일되었으므로 묶음에서 뺍니다.
                var unitOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var unity = new List<(string Source, IReadOnlyList<string> Arguments, string[] Members)>();
                foreach (var responseFile in SafeFiles(module, "*.obj.rsp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (!TryConvert(Expand(responseFile, 0), out var source, out var arguments))
                    {
                        continue;
                    }

                    responseFiles++;
                    if (IsUnitySource(source, module))
                    {
                        var members = ReadUnityMembers(source).Select(Normalize).ToArray();
                        unity.Add((Normalize(source), arguments, members));
                        foreach (var member in members)
                        {
                            unityMembers++;
                            if (sources.ContainsKey(member)) continue;
                            sources[member] = arguments;
                            unitOf[member] = Normalize(source);
                        }
                    }
                    else
                    {
                        // 파일별 응답 파일은 unity 구성보다 우선합니다(적응형 unity로 따로 컴파일된 파일).
                        var normalized = Normalize(source);
                        sources[normalized] = arguments;
                        unitOf.Remove(normalized);
                    }
                }

                var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in sources)
                {
                    var source = Normalize(pair.Key);
                    if (IsGenerated(source))
                    {
                        skippedGenerated++;
                        continue;
                    }

                    if (!File.Exists(source))
                    {
                        missingSources++;
                        continue;
                    }

                    if (!commands.ContainsKey(source))
                    {
                        commands[source] = new CompileCommand(directory, source, new[] { compiler, "--driver-mode=cl" }.Concat(pair.Value).Concat(new[] { source }).ToArray());
                        if (unitOf.ContainsKey(source)) grouped.Add(source);
                    }
                }

                foreach (var (unitySource, arguments, members) in unity)
                {
                    var kept = members.Where(m => grouped.Contains(m) && string.Equals(unitOf[m], unitySource, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (kept.Length > 0) units.Add(new UnityUnit(unitySource, directory, new[] { compiler, "--driver-mode=cl" }.Concat(arguments).ToArray(), kept));
                }
            }
        }

        var (supplemented, unreadable) = SupplementMissing(projectDirectory, commands, overrideDirectory, cancellationToken);
        return new UnrealCompileCommandResult(commands.Values.OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase).ToArray(), variant,
            modules, responseFiles, unityMembers, skippedGenerated, missingSources, supplemented, unreadable, units);
    }

    /// <summary>
    /// 응답 파일이 없는 프로젝트·프로젝트 플러그인 소스(빌드 뒤 추가한 파일, 이 대상·구성에 빌드하지 않은 모듈)에 명령을 보완합니다.
    /// </summary>
    /// <remarks>
    /// 이런 파일은 database에 없어 background index가 처리하지 않으므로, 그 안의 정의·참조는 편집기에서 파일을 열기 전까지
    /// 찾을 수 없었습니다(2026-10-08 회사 사용 피드백). 같은 모듈에 명령이 있으면 그 인자를 그대로 써서 정확하고,
    /// 모듈 전체가 빠졌으면 <see cref="Synthesize"/>의 근사 명령을 씁니다(생성 헤더가 없으면 일부 진단이 남음).
    /// 이 대상으로 컴파일하지 않는 소스는 보완하지 않습니다: Windows 밖 플랫폼 전용 폴더(Win64 인자로 색인하면 플랫폼별 정의가
    /// 후보·참조에 섞임), <c>ThirdParty</c> 폴더와 <c>ModuleType.External</c> 모듈(외부 라이브러리 소스까지 색인하면 메모리·CPU가 늘어남).
    /// </remarks>
    private static (int Added, int Unreadable) SupplementMissing(string projectDirectory, Dictionary<string, CompileCommand> commands,
        string? overrideDirectory, CancellationToken cancellationToken)
    {
        // 모듈 판정은 폴더마다 *.Build.cs를 찾으므로 폴더 단위로 기억합니다.
        var owners = new Dictionary<string, (string Directory, string Module)?>(StringComparer.OrdinalIgnoreCase);
        (string Directory, string Module)? ModuleOf(string file)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
            if (!owners.TryGetValue(directory, out var owner))
            {
                owner = OwningModule(file);
                owners[directory] = owner;
            }

            return owner;
        }

        var samples = new Dictionary<string, CompileCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands.Values)
        {
            if (ModuleOf(command.File) is (string directory, _) && !samples.ContainsKey(directory)) samples[directory] = command;
        }

        var external = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool IsExternal(string directory, string module)
        {
            if (!external.TryGetValue(directory, out var value))
            {
                value = ReadRules(Path.Combine(directory, module + ".Build.cs")).IndexOf("ModuleType.External", StringComparison.Ordinal) >= 0;
                external[directory] = value;
            }

            return value;
        }

        var built = commands.Values.ToArray();
        var added = 0;
        var unreadable = 0;
        foreach (var root in BuildRoots(projectDirectory, "Source"))
        {
            foreach (var file in SupplementSources(root, ref unreadable, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Normalize(file);
                if (commands.ContainsKey(source) || IsGenerated(source) || ModuleOf(source) is not (string directory, string module))
                {
                    continue;
                }

                CompileCommand? command = null;
                if (samples.TryGetValue(directory, out var sample))
                {
                    // 마지막 인자가 source입니다(Build 참고). 모듈 정의 헤더·포함 경로가 같은 모듈이므로 그대로 맞습니다.
                    command = new CompileCommand(sample.Directory, source, sample.Arguments.Take(sample.Arguments.Count - 1).Concat(new[] { source }).ToArray());
                }
                else if (overrideDirectory is not null && built.Length > 0 && !IsExternal(directory, module))
                {
                    command = Synthesize(source, built, overrideDirectory, sharedPrecompiledHeader: true);
                }

                if (command is null) continue;
                commands[source] = command;
                added++;
            }
        }

        return (added, unreadable);
    }

    /// <summary>
    /// 보완할 소스를 폴더 단위로 모읍니다. 읽지 못한 폴더(접근 거부·경로 길이 초과)는 그 폴더만 건너뛰고 <paramref name="unreadable"/>에 셉니다.
    /// </summary>
    /// <remarks>
    /// 정션·심볼릭 링크 폴더는 따라가지 않습니다. 상위를 가리키는 링크가 있으면 같은 파일을 다른 경로로 거듭 보완하고 경로 길이 한계까지
    /// 내려가기 때문입니다. 링크 안의 파일도 빌드했으면 응답 파일 명령으로 들어오므로, 빠지는 것은 빌드 전 새 파일뿐입니다.
    /// </remarks>
    private static List<string> SupplementSources(string root, ref int unreadable, CancellationToken cancellationToken)
    {
        var sources = new List<string>();
        if (!Directory.Exists(root)) return sources;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                sources.AddRange(directory.EnumerateFiles().Select(f => f.FullName).Where(DefinitionCandidates.IsSource));
                foreach (var child in directory.EnumerateDirectories())
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) == 0 && !SkippedSupplementFolders.Contains(child.Name)) pending.Push(child);
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                unreadable++;
            }
        }

        return sources;
    }

    private static string ReadRules(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 명령이 없는 엔진 파일에 프로젝트 명령을 바탕으로 소속 모듈 경로를 더한 근사 명령을 만듭니다.
    /// 설치형 엔진은 엔진 모듈 응답 파일을 제공하지 않기 때문입니다. 소속 모듈의 비공개 의존 경로는
    /// 알 수 없어 일부 진단이 남을 수 있으므로 정의 위치 확정처럼 오차를 견디는 용도로만 씁니다.
    /// 프로젝트 정의 헤더가 의존 모듈 API 매크로를 dllimport로 정의하므로, 소속 모듈 매크로는 그 뒤에
    /// 강제 include하는 작은 헤더(<paramref name="overrideDirectory"/>/&lt;Module&gt;.h)에서 다시 비웁니다.
    /// </summary>
    /// <param name="sharedPrecompiledHeader">
    /// 프로젝트 명령의 공유 PCH 헤더(<c>SharedPCH.*</c>)를 남깁니다. 같은 프로젝트의 빌드하지 않은 모듈처럼 공유 PCH에 기댈 수 있는
    /// 소스에 씁니다. 엔진 cpp는 include를 스스로 갖추므로 빼서 정의 확정 분석 시간을 줄입니다. 다른 모듈의 전용 PCH(<c>PCH.*</c>)는 항상 뺍니다.
    /// </param>
    public static CompileCommand? Synthesize(string file, IReadOnlyList<CompileCommand> projectCommands, string overrideDirectory,
        string platform = "Win64", string target = "UnrealEditor", bool sharedPrecompiledHeader = false)
    {
        if (projectCommands.Count == 0 || OwningModule(file) is not (string moduleDirectory, string module))
        {
            return null;
        }

        var baseCommand = projectCommands.OrderByDescending(c => c.Arguments.Count(a => a == "/I")).First();
        var head = WithoutPrecompiledHeaders(baseCommand.Arguments.Take(baseCommand.Arguments.Count - 1).ToArray(), sharedPrecompiledHeader);
        var plugin = Ancestors(moduleDirectory).FirstOrDefault(d => SafeFiles(d, "*.uplugin").Any());
        var engineDirectory = Ancestors(moduleDirectory).FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Engine", StringComparison.OrdinalIgnoreCase));
        var generatedRoot = Path.Combine(plugin ?? engineDirectory ?? moduleDirectory, "Intermediate", "Build", platform, target, "Inc", module);
        var extra = new List<string>();
        foreach (var include in new[]
                 {
                     moduleDirectory, Path.Combine(moduleDirectory, "Public"), Path.Combine(moduleDirectory, "Private"),
                     Path.Combine(moduleDirectory, "Classes"), Path.Combine(moduleDirectory, "Internal"),
                     Path.GetDirectoryName(file)!, Path.Combine(generatedRoot, "UHT"), generatedRoot
                 })
        {
            if (Directory.Exists(include))
            {
                extra.Add("/I");
                extra.Add(Normalize(include));
            }
        }

        Directory.CreateDirectory(overrideDirectory);
        var overrideHeader = Path.Combine(overrideDirectory, module + ".h");
        var api = module.ToUpperInvariant() + "_API";
        var content = $"#undef {api}\n#define {api}\n#undef UE_MODULE_NAME\n#define UE_MODULE_NAME \"{module}\"\n";
        if (!File.Exists(overrideHeader) || File.ReadAllText(overrideHeader) != content)
        {
            File.WriteAllText(overrideHeader, content);
        }

        extra.AddRange(new[] { "-Xclang", "-include", "-Xclang", Normalize(overrideHeader) });
        var normalizedFile = Normalize(file);
        return new CompileCommand(baseCommand.Directory, normalizedFile, head.Concat(extra).Concat(new[] { normalizedFile }).ToArray());
    }

    /// <summary>UBT가 만든 PCH 래퍼 헤더(<c>SharedPCH.*</c>, 모듈 전용 <c>PCH.*</c>)의 강제 include를 뺍니다.</summary>
    private static IReadOnlyList<string> WithoutPrecompiledHeaders(IReadOnlyList<string> arguments, bool keepShared)
    {
        var result = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i + 3 < arguments.Count && arguments[i] == "-Xclang" && arguments[i + 1] == "-include" && arguments[i + 2] == "-Xclang")
            {
                var name = Path.GetFileName(arguments[i + 3]);
                var shared = name.StartsWith("SharedPCH.", StringComparison.OrdinalIgnoreCase);
                if (shared && !keepShared || !shared && name.StartsWith("PCH.", StringComparison.OrdinalIgnoreCase))
                {
                    i += 3;
                    continue;
                }
            }

            result.Add(arguments[i]);
        }

        return result;
    }

    /// <summary>파일 위쪽에서 <c>*.Build.cs</c>가 있는 폴더를 모듈 루트로 봅니다.</summary>
    public static (string Directory, string Module)? OwningModule(string file)
    {
        foreach (var directory in Ancestors(Path.GetDirectoryName(Path.GetFullPath(file))!))
        {
            var rules = SafeFiles(directory, "*.Build.cs").FirstOrDefault();
            if (rules is not null)
            {
                var name = Path.GetFileName(rules);
                return (directory, name.Substring(0, name.Length - ".Build.cs".Length));
            }

            var leaf = Path.GetFileName(directory);
            if (string.Equals(leaf, "Source", StringComparison.OrdinalIgnoreCase) || string.Equals(leaf, "Engine", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> Expand(string responseFile, int depth)
    {
        if (depth > 4)
        {
            throw new InvalidDataException("응답 파일 중첩이 너무 깊습니다: " + responseFile);
        }

        var info = new FileInfo(responseFile);
        if (!info.Exists || info.Length > MaxResponseFileBytes)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var token in CommandLine.Split(File.ReadAllText(responseFile)))
        {
            if (token.StartsWith("@", StringComparison.Ordinal))
            {
                result.AddRange(Expand(token.Substring(1), depth + 1));
            }
            else
            {
                result.Add(token);
            }
        }

        return result;
    }

    /// <summary>첫 비옵션 인자를 source로 보고 나머지를 clangd가 해석할 인자로 바꿉니다.</summary>
    internal static bool TryConvert(IReadOnlyList<string> tokens, out string source, out IReadOnlyList<string> arguments)
    {
        source = string.Empty;
        var result = new List<string>();
        var pch = new HashSet<string>(tokens.Where(t => t.StartsWith("/Yu", StringComparison.Ordinal) && t.Length > 3)
            .Select(t => Normalize(t.Substring(3))), StringComparer.OrdinalIgnoreCase);
        var skip = false;
        foreach (var token in tokens)
        {
            if (skip)
            {
                skip = false;
                continue;
            }

            if (DropWithValue.Contains(token, StringComparer.Ordinal))
            {
                skip = true;
                continue;
            }

            if (DropPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            if (source.Length == 0 && !token.StartsWith("/", StringComparison.Ordinal) && !token.StartsWith("-", StringComparison.Ordinal))
            {
                source = token;
                continue;
            }

            if (token.StartsWith("/FI", StringComparison.Ordinal))
            {
                // PCH 헤더도 실제 빌드처럼 텍스트로 포함합니다. 단, 생성 폴더를 지워 헤더가 없으면 모든 TU가 치명 오류로 멈추므로 뺍니다.
                var header = Normalize(token.Substring(3));
                if (!pch.Contains(header) || File.Exists(header))
                {
                    result.AddRange(new[] { "-Xclang", "-include", "-Xclang", header });
                }

                continue;
            }

            result.Add(token);
        }

        arguments = result;
        return source.Length > 0;
    }

    private static bool IsUnitySource(string source, string moduleDirectory) =>
        Path.GetFileName(source).StartsWith("Module.", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Normalize(Path.GetDirectoryName(source)!), Normalize(moduleDirectory), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ReadUnityMembers(string unitySource)
    {
        if (!File.Exists(unitySource))
        {
            return Array.Empty<string>();
        }

        return UnityInclude.Matches(File.ReadAllText(unitySource)).Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
    }

    private static bool IsGenerated(string source) =>
        source.EndsWith(".gen.cpp", StringComparison.OrdinalIgnoreCase) ||
        source.IndexOf("/Intermediate/", StringComparison.OrdinalIgnoreCase) >= 0;

    private static IEnumerable<string> BuildRoots(string projectDirectory, string relative)
    {
        yield return Path.Combine(projectDirectory, relative);
        var plugins = Path.Combine(projectDirectory, "Plugins");
        if (!Directory.Exists(plugins))
        {
            yield break;
        }

        // 플러그인은 깊이가 제각각이므로 .uplugin이 있는 폴더를 찾아 그 아래 빌드 폴더를 봅니다.
        string[] descriptors;
        try
        {
            descriptors = Directory.GetFiles(plugins, "*.uplugin", SearchOption.AllDirectories);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var descriptor in descriptors.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(Path.GetDirectoryName(descriptor)!, relative);
        }
    }

    private static IEnumerable<string> Ancestors(string directory)
    {
        for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            yield return current;
        }
    }

    private static string[] SafeDirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetDirectories(directory) : Array.Empty<string>();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string[] SafeFiles(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern) : Array.Empty<string>();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static DateTime SafeWriteTime(string file)
    {
        try
        {
            return File.GetLastWriteTimeUtc(file);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    internal static string Normalize(string path) => path.Replace('\\', '/');
}
