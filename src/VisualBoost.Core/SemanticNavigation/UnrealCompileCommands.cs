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

public sealed class UnrealCompileCommandResult
{
    public UnrealCompileCommandResult(IReadOnlyList<CompileCommand> commands, UnrealBuildVariant variant, int modules, int responseFiles,
        int unityMembers, int skippedGenerated, int missingSources)
    {
        Commands = commands;
        Variant = variant;
        Modules = modules;
        ResponseFiles = responseFiles;
        UnityMembers = unityMembers;
        SkippedGenerated = skippedGenerated;
        MissingSources = missingSources;
    }

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
/// - 파일별 응답 파일은 그대로, unity 응답 파일은 unity cpp의 #include 목록으로 개별 cpp에 펼칩니다.
/// - MSVC PCH(/Yu /Yc /Fp)와 출력·로그 옵션을 빼고, /Yu 대상인 공유 PCH 헤더의 강제 include도 뺍니다.
///   텍스트로 넣으면 TU마다 거대한 헤더를 다시 분석해 비용이 몇 배가 되며, UBT의 clang database 모드도 PCH를 끕니다.
/// - 남은 강제 include는 clang driver가 옆의 MSVC .pch를 자동 선택하지 않도록 -Xclang -include로 바꿉니다.
/// - UBT 컴파일 작업 경로는 항상 &lt;Engine&gt;/Engine/Source입니다.
/// </remarks>
public static class UnrealCompileCommands
{
    private const int MaxResponseFileBytes = 4 * 1024 * 1024;
    private static readonly Regex UnityInclude = new("^\\s*#include\\s+\"([^\"]+)\"", RegexOptions.Multiline | RegexOptions.CultureInvariant);
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

    public static UnrealCompileCommandResult Build(string projectDirectory, string engineRoot, UnrealBuildVariant variant, string compiler,
        CancellationToken cancellationToken = default)
    {
        var directory = Normalize(Path.Combine(engineRoot, "Engine", "Source"));
        var commands = new Dictionary<string, CompileCommand>(StringComparer.OrdinalIgnoreCase);
        int modules = 0, responseFiles = 0, unityMembers = 0, skippedGenerated = 0, missingSources = 0;
        foreach (var root in BuildRoots(projectDirectory, variant.RelativeDirectory))
        {
            foreach (var module in SafeDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                modules++;
                var sources = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var responseFile in SafeFiles(module, "*.obj.rsp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (!TryConvert(Expand(responseFile, 0), out var source, out var arguments))
                    {
                        continue;
                    }

                    responseFiles++;
                    if (IsUnitySource(source, module))
                    {
                        foreach (var member in ReadUnityMembers(source))
                        {
                            unityMembers++;
                            var normalizedMember = Normalize(member);
                            if (!sources.ContainsKey(normalizedMember)) sources[normalizedMember] = arguments;
                        }
                    }
                    else
                    {
                        // 파일별 응답 파일은 unity 구성보다 우선합니다(적응형 unity로 따로 컴파일된 파일).
                        sources[Normalize(source)] = arguments;
                    }
                }

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
                    }
                }
            }
        }

        return new UnrealCompileCommandResult(commands.Values.OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase).ToArray(), variant,
            modules, responseFiles, unityMembers, skippedGenerated, missingSources);
    }

    /// <summary>
    /// 명령이 없는 엔진 파일에 프로젝트 명령을 바탕으로 소속 모듈 경로를 더한 근사 명령을 만듭니다.
    /// 설치형 엔진은 엔진 모듈 응답 파일을 제공하지 않기 때문입니다. 소속 모듈의 비공개 의존 경로는
    /// 알 수 없어 일부 진단이 남을 수 있으므로 정의 위치 확정처럼 오차를 견디는 용도로만 씁니다.
    /// 프로젝트 정의 헤더가 의존 모듈 API 매크로를 dllimport로 정의하므로, 소속 모듈 매크로는 그 뒤에
    /// 강제 include하는 작은 헤더(<paramref name="overrideDirectory"/>/&lt;Module&gt;.h)에서 다시 비웁니다.
    /// </summary>
    public static CompileCommand? Synthesize(string file, IReadOnlyList<CompileCommand> projectCommands, string overrideDirectory,
        string platform = "Win64", string target = "UnrealEditor")
    {
        if (projectCommands.Count == 0 || OwningModule(file) is not (string moduleDirectory, string module))
        {
            return null;
        }

        var baseCommand = projectCommands.OrderByDescending(c => c.Arguments.Count(a => a == "/I")).First();
        var head = baseCommand.Arguments.Take(baseCommand.Arguments.Count - 1);
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
                var header = Normalize(token.Substring(3));
                if (!pch.Contains(header))
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
