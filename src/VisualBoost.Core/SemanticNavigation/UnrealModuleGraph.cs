using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 모듈 규칙 파일(<c>*.Build.cs</c>)을 텍스트로 읽어 얻은 의존 모듈·포함 경로·정의입니다. 조건문은 해석하지 않고 모든 갈래를 합칩니다.
/// </summary>
public sealed class UnrealModuleRules
{
    internal UnrealModuleRules(string name, string directory, bool engine)
    {
        Name = name;
        Directory = directory;
        IsEngine = engine;
    }

    public string Name { get; }

    /// <summary>중간 파일 폴더에 쓰는 짧은 이름(<c>ShortName</c>)입니다. 규칙에 없으면 null입니다.</summary>
    public string? ShortName { get; internal set; }

    /// <summary>규칙 파일이 있는 모듈 루트 폴더입니다.</summary>
    public string Directory { get; }

    /// <summary>엔진(Engine 폴더 아래) 모듈입니다. 엔진 모듈끼리만 <c>Internal</c> 폴더를 볼 수 있습니다.</summary>
    public bool IsEngine { get; }

    public List<string> PublicDependencies { get; } = new();

    public List<string> PrivateDependencies { get; } = new();

    /// <summary>헤더만 쓰는 모듈입니다(<c>PublicIncludePathModuleNames</c>).</summary>
    public List<string> PublicIncludePathModules { get; } = new();

    public List<string> PrivateIncludePathModules { get; } = new();

    /// <summary>규칙이 직접 더한 공개 포함 경로(있는 폴더만, 절대 경로)입니다. 시스템 포함 경로도 여기에 둡니다.</summary>
    public List<string> PublicIncludePaths { get; } = new();

    public List<string> PrivateIncludePaths { get; } = new();

    /// <summary><c>이름=값</c> 또는 <c>이름</c> 형태의 공개 정의입니다.</summary>
    public List<string> PublicDefinitions { get; } = new();

    public List<string> PrivateDefinitions { get; } = new();
}

/// <summary>모듈 하나를 컴파일할 때의 근사 환경입니다(<see cref="UnrealModuleGraph.Environment"/>).</summary>
public sealed class UnrealModuleEnvironment
{
    internal UnrealModuleEnvironment(IReadOnlyList<string> includeDirectories, IReadOnlyList<string> apiModules, IReadOnlyList<string> definitions)
    {
        IncludeDirectories = includeDirectories;
        ApiModules = apiModules;
        Definitions = definitions;
    }

    /// <summary>찾는 순서대로의 포함 경로입니다. 대상 모듈 → 직접 의존 → 공개 의존 사슬 순입니다.</summary>
    public IReadOnlyList<string> IncludeDirectories { get; }

    /// <summary>헤더를 볼 수 있는 모듈 이름(대상 포함)입니다. API 매크로(<c>NAME_API</c>)를 정의할 때 씁니다.</summary>
    public IReadOnlyList<string> ApiModules { get; }

    /// <summary>대상 모듈의 비공개 정의와 볼 수 있는 모듈의 공개 정의입니다(<c>이름=값</c>).</summary>
    public IReadOnlyList<string> Definitions { get; }
}

/// <summary>
/// 엔진·프로젝트의 모듈 위치와 규칙을 읽어, 빌드 명령이 없는 모듈 파일(설치형 엔진의 엔진 모듈, 이 대상으로 빌드하지 않은 모듈)에 줄
/// 포함 경로와 정의를 만듭니다. 스레드 안전합니다.
/// </summary>
/// <remarks>
/// 설치형 엔진은 엔진 모듈의 응답 파일을 제공하지 않으므로, 프로젝트가 쓰지 않는 엔진 모듈의 파일은 의존 모듈 헤더 경로와 API 매크로를
/// 알 수 없어 분석 오류(헤더 없음, 모르는 매크로)로 심볼을 잃었습니다(2026-10-09 정확도 시험: 무작위 엔진 파일 50개 중 다수). UBT처럼
/// 직접 의존과 그 공개 의존 사슬의 공개 경로를 모읍니다. 규칙 파일은 C# 코드라 조건·계산식은 해석하지 않고 문자열 상수만 읽으므로
/// 근사입니다. 모듈 위치는 처음 쓸 때 한 번 훑고(설치형 엔진 약 2,400개, 수 초), 모르는 모듈을 찾으면 한 번 더 훑습니다.
/// </remarks>
public sealed class UnrealModuleGraph
{
    private const int MaxModules = 1500;
    private static readonly ConcurrentDictionary<string, UnrealModuleGraph> Graphs = new(StringComparer.OrdinalIgnoreCase);

    // 모듈 규칙 파일이 들어 있지 않은 큰 폴더입니다. 훑는 시간을 줄입니다.
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Intermediate", "Binaries", "Content", "Resources", "Shaders", "Config", "Documentation", "Saved", "DerivedDataCache", ".git", ".vs"
    };

    private static readonly Regex Call = new(
        @"\b(PublicDependencyModuleNames|PrivateDependencyModuleNames|PublicIncludePathModuleNames|PrivateIncludePathModuleNames|" +
        @"PublicIncludePaths|PrivateIncludePaths|PublicSystemIncludePaths|PublicDefinitions|PrivateDefinitions)\s*\.\s*(Add|AddRange)\s*\(" +
        @"|\b(AddEngineThirdPartyPrivateStaticDependencies|AddEngineThirdPartyPrivateDynamicDependencies|ConditionalAddModuleDirectory)\s*\(",
        RegexOptions.CultureInvariant);

    private static readonly Regex Literal = new("@?\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
    private static readonly Regex ModuleDirectoryCall = new("GetModuleDirectory\\s*\\(\\s*\"([^\"]+)\"\\s*\\)", RegexOptions.CultureInvariant);
    private static readonly Regex ShortNameAssignment = new("\\bShortName\\s*=\\s*\"(\\w+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex VariableAssignment = new(@"\b(?:string|var)\s+(\w+)\s*=\s*([^;]+);", RegexOptions.CultureInvariant);

    private readonly object gate = new();
    private readonly string engineDirectory;
    private readonly string? projectDirectory;
    private readonly ConcurrentDictionary<string, UnrealModuleRules?> rules = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? directories;
    private bool rescanned;

    private UnrealModuleGraph(string engineRoot, string? projectDirectory)
    {
        engineDirectory = Path.GetFullPath(Path.Combine(engineRoot, "Engine"));
        this.projectDirectory = projectDirectory is null ? null : Path.GetFullPath(projectDirectory);
    }

    /// <summary>엔진 설치 루트와 프로젝트 폴더마다 하나를 공유합니다.</summary>
    public static UnrealModuleGraph For(string engineRoot, string? projectDirectory) =>
        Graphs.GetOrAdd(Path.GetFullPath(engineRoot) + "|" + (projectDirectory is null ? string.Empty : Path.GetFullPath(projectDirectory)),
            _ => new UnrealModuleGraph(engineRoot, projectDirectory));

    /// <summary>모듈 위치를 미리 훑습니다. 첫 요청이 기다리지 않도록 작업 스레드에서 부릅니다.</summary>
    public void Prepare() => Directories(null);

    /// <summary>모듈 루트 폴더입니다. 모르는 이름이면 null입니다.</summary>
    public string? DirectoryOf(string module) => Directories(module).TryGetValue(module, out var directory) ? directory : null;

    /// <summary>모듈 규칙입니다. 모듈을 모르거나 규칙 파일을 읽지 못하면 null입니다.</summary>
    public UnrealModuleRules? RulesOf(string module) =>
        rules.GetOrAdd(module, name => DirectoryOf(name) is { } directory ? Read(name, directory) : null);

    /// <summary>
    /// 모듈 하나의 컴파일 환경을 만듭니다. 대상 모듈의 공개·비공개 경로, 직접 의존(공개·비공개·헤더 전용)의 공개 경로, 그 의존들의
    /// 공개 의존 사슬의 공개 경로 순으로 모읍니다(UBT의 전파 규칙).
    /// </summary>
    /// <param name="target">생성 헤더 폴더(<c>Intermediate/Build/플랫폼/대상/Inc</c>)의 대상 이름입니다.</param>
    public UnrealModuleEnvironment Environment(string module, string moduleDirectory, string platform = "Win64", string target = "UnrealEditor")
    {
        var root = RulesOf(module) ?? new UnrealModuleRules(module, moduleDirectory, IsUnder(moduleDirectory, engineDirectory));
        var includes = new List<string>();
        var seenIncludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Include(string directory)
        {
            if (seenIncludes.Add(directory) && System.IO.Directory.Exists(directory)) includes.Add(directory);
        }

        var definitions = new List<string>();
        var api = new List<string> { root.Name };
        foreach (var directory in OwnDirectories(root, platform, target)) Include(directory);
        foreach (var directory in root.PrivateIncludePaths) Include(directory);
        definitions.AddRange(root.PrivateDefinitions);
        definitions.AddRange(root.PublicDefinitions);
        // 모듈 상위 폴더 기준 포함(예: 같은 플러그인의 "Import/Private/…")에 쓰는 경로입니다. UBT는 이전 빌드 설정 모듈에만 더하지만 규칙
        // 파일만으로는 설정을 알 수 없어 모두 더하고, 같은 이름 헤더를 잘못 찾지 않게 다른 경로보다 뒤에 둡니다.
        var parents = new List<string> { Path.GetDirectoryName(root.Directory)! };

        // 직접 의존부터 넓이 우선으로 공개 의존 사슬을 따라갑니다.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Name };
        var queue = new Queue<string>();
        foreach (var name in root.PublicDependencies.Concat(root.PrivateDependencies).Concat(root.PublicIncludePathModules).Concat(root.PrivateIncludePathModules))
        {
            if (visited.Add(name)) queue.Enqueue(name);
        }

        while (queue.Count > 0 && visited.Count < MaxModules)
        {
            var dependency = RulesOf(queue.Dequeue());
            if (dependency is null) continue;
            api.Add(dependency.Name);
            parents.Add(Path.GetDirectoryName(dependency.Directory)!);
            foreach (var directory in PublicDirectories(dependency, root.IsEngine, platform, target)) Include(directory);
            definitions.AddRange(dependency.PublicDefinitions);
            foreach (var name in dependency.PublicDependencies.Concat(dependency.PublicIncludePathModules))
            {
                if (visited.Add(name)) queue.Enqueue(name);
            }
        }

        foreach (var parent in parents) Include(parent);
        return new UnrealModuleEnvironment(includes, api, definitions);
    }

    private IEnumerable<string> OwnDirectories(UnrealModuleRules module, string platform, string target)
    {
        yield return Path.Combine(module.Directory, "Private");
        foreach (var directory in PublicDirectories(module, true, platform, target)) yield return directory;
    }

    /// <summary>다른 모듈이 볼 수 있는 경로입니다. <c>Internal</c>은 엔진 모듈끼리만 봅니다.</summary>
    private IEnumerable<string> PublicDirectories(UnrealModuleRules module, bool consumerIsEngine, string platform, string target)
    {
        yield return Path.Combine(module.Directory, "Public");
        yield return Path.Combine(module.Directory, "Classes");
        if (consumerIsEngine && module.IsEngine) yield return Path.Combine(module.Directory, "Internal");
        foreach (var generated in GeneratedDirectories(module, platform, target)) yield return generated;
        foreach (var directory in module.PublicIncludePaths) yield return directory;
    }

    /// <summary>UHT 생성 헤더 폴더(<c>…/Inc/모듈/UHT</c> 등)입니다. 플러그인·엔진·프로젝트 중 가장 가까운 소유 폴더 아래에 있습니다.</summary>
    private IEnumerable<string> GeneratedDirectories(UnrealModuleRules module, string platform, string target)
    {
        var owner = OwnerDirectory(module.Directory);
        // 이름이 긴 모듈은 중간 파일 폴더에 짧은 이름을 씁니다(예: DynamicMaterial → DynMat).
        var inc = Path.Combine(owner, "Intermediate", "Build", platform, target, "Inc", module.Name);
        if (!System.IO.Directory.Exists(inc) && module.ShortName is { } shortName) inc = Path.Combine(owner, "Intermediate", "Build", platform, target, "Inc", shortName);
        if (!System.IO.Directory.Exists(inc)) return Array.Empty<string>();
        try
        {
            return System.IO.Directory.GetDirectories(inc).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private string OwnerDirectory(string moduleDirectory)
    {
        for (var current = moduleDirectory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (string.Equals(current, engineDirectory, StringComparison.OrdinalIgnoreCase)) return current;
            if (HasFile(current, "*.uplugin") || HasFile(current, "*.uproject")) return current;
        }

        return moduleDirectory;
    }

    private Dictionary<string, string> Directories(string? wanted)
    {
        lock (gate)
        {
            // 모르는 모듈을 찾으면 그 사이 추가된 플러그인일 수 있으므로 세션마다 한 번 다시 훑습니다.
            if (directories is null || wanted is not null && !directories.ContainsKey(wanted) && !rescanned)
            {
                rescanned = directories is not null;
                directories = Scan();
            }

            return directories;
        }
    }

    private Dictionary<string, string> Scan()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 같은 이름이면 프로젝트 모듈이 엔진 모듈보다 우선합니다(UBT 규칙). 그래서 프로젝트를 나중에 훑어 덮어씁니다.
        var roots = new List<string> { Path.Combine(engineDirectory, "Source"), Path.Combine(engineDirectory, "Plugins") };
        if (projectDirectory is not null)
        {
            roots.Add(Path.Combine(projectDirectory, "Source"));
            roots.Add(Path.Combine(projectDirectory, "Plugins"));
        }

        foreach (var root in roots.Where(System.IO.Directory.Exists))
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                try
                {
                    var rule = directory.EnumerateFiles("*.Build.cs").FirstOrDefault();
                    if (rule is not null)
                    {
                        // 모듈 폴더 안에는 다른 모듈이 없습니다(설치형 엔진 UE 5.8 확인).
                        found[rule.Name.Substring(0, rule.Name.Length - ".Build.cs".Length)] = directory.FullName;
                        continue;
                    }

                    foreach (var child in directory.EnumerateDirectories())
                    {
                        if ((child.Attributes & FileAttributes.ReparsePoint) == 0 && !SkippedFolders.Contains(child.Name)) pending.Push(child);
                    }
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    // 읽지 못한 폴더의 모듈은 모르는 모듈로 남습니다.
                }
            }
        }

        return found;
    }

    private UnrealModuleRules? Read(string name, string directory)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(directory, name + ".Build.cs"));
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return null;
        }

        var module = new UnrealModuleRules(name, directory, IsUnder(directory, engineDirectory));
        Parse(StripComments(text), module, engineDirectory, PluginDirectory(directory), DirectoryOf);
        return module;
    }

    /// <summary>
    /// 규칙 파일 본문(주석 제거)에서 의존·경로·정의 호출의 문자열 상수를 읽습니다. 경로를 담은 지역 변수(<c>string Root = ModuleDirectory + …;</c>)는
    /// 앞에서부터 풀어 경로 식에 씁니다.
    /// </summary>
    internal static void Parse(string text, UnrealModuleRules module, string engineDirectory, string? pluginDirectory, Func<string, string?> directoryOf)
    {
        var shortName = ShortNameAssignment.Match(text);
        if (shortName.Success) module.ShortName = shortName.Groups[1].Value;
        var context = new PathContext(module.Directory, engineDirectory, pluginDirectory, directoryOf);
        foreach (Match assignment in VariableAssignment.Matches(text))
        {
            if (context.Resolve(assignment.Groups[2].Value, requireBase: true) is { } value) context.Variables[assignment.Groups[1].Value] = value;
        }

        foreach (Match match in Call.Matches(text))
        {
            var open = match.Index + match.Length - 1;
            var close = MatchingParen(text, open);
            if (close < 0) continue;
            var body = text.Substring(open + 1, close - open - 1);
            var kind = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
            switch (kind)
            {
                case "PublicDependencyModuleNames": module.PublicDependencies.AddRange(Literals(body)); break;
                case "PrivateDependencyModuleNames": module.PrivateDependencies.AddRange(Literals(body)); break;
                case "PublicIncludePathModuleNames": module.PublicIncludePathModules.AddRange(Literals(body)); break;
                case "PrivateIncludePathModuleNames": module.PrivateIncludePathModules.AddRange(Literals(body)); break;
                case "AddEngineThirdPartyPrivateStaticDependencies":
                case "AddEngineThirdPartyPrivateDynamicDependencies":
                    module.PrivateIncludePathModules.AddRange(Literals(body));
                    break;
                case "PublicIncludePaths":
                case "PublicSystemIncludePaths":
                    module.PublicIncludePaths.AddRange(Paths(body, context));
                    break;
                case "PrivateIncludePaths":
                    module.PrivateIncludePaths.AddRange(Paths(body, context));
                    break;
                case "ConditionalAddModuleDirectory":
                    // 다른 폴더를 이 모듈의 일부로 더합니다(공개·비공개 폴더 포함).
                    if (context.Resolve(body, requireBase: true) is { } added)
                    {
                        module.PublicIncludePaths.AddRange(new[] { "Public", "Classes", "Internal" }.Select(f => Path.Combine(added, f)).Where(System.IO.Directory.Exists));
                        if (System.IO.Directory.Exists(Path.Combine(added, "Private"))) module.PrivateIncludePaths.Add(Path.Combine(added, "Private"));
                    }

                    break;
                case "PublicDefinitions": module.PublicDefinitions.AddRange(Definitions(body)); break;
                case "PrivateDefinitions": module.PrivateDefinitions.AddRange(Definitions(body)); break;
            }
        }
    }

    private static IEnumerable<string> Literals(string body) =>
        Literal.Matches(body).Cast<Match>().Select(m => m.Groups[1].Value).Where(v => v.Length > 0);

    /// <summary>문자열 상수로 끝나는 정의만 씁니다. <c>"WITH_X=" + (조건 ? "1" : "0")</c>처럼 값을 계산하는 정의는 값을 알 수 없어 뺍니다.</summary>
    private static IEnumerable<string> Definitions(string body) =>
        Elements(body).Select(e => e.Trim()).Where(e => Literal.IsMatch(e) && Literal.Match(e).Length == e.Length)
            .SelectMany(Literals).Where(d => d.IndexOf('(') < 0 && d.IndexOf(' ') < 0 && !d.EndsWith("=", StringComparison.Ordinal));

    /// <summary>경로 식마다 <see cref="PathContext.Resolve"/>로 푼 폴더 중 있는 것만 돌려줍니다.</summary>
    private static IEnumerable<string> Paths(string body, PathContext context)
    {
        foreach (var expression in Elements(body))
        {
            if (context.Resolve(expression, requireBase: false) is { } path && System.IO.Directory.Exists(path)) yield return path;
        }
    }

    /// <summary>규칙 파일의 경로 식을 푸는 문맥입니다.</summary>
    private sealed class PathContext
    {
        private readonly string moduleDirectory;
        private readonly string engineDirectory;
        private readonly string? pluginDirectory;
        private readonly Func<string, string?> directoryOf;

        public PathContext(string moduleDirectory, string engineDirectory, string? pluginDirectory, Func<string, string?> directoryOf)
        {
            this.moduleDirectory = moduleDirectory;
            this.engineDirectory = engineDirectory;
            this.pluginDirectory = pluginDirectory;
            this.directoryOf = directoryOf;
        }

        public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// 기준 폴더(<c>GetModuleDirectory("X")</c>·앞서 푼 지역 변수·<c>ModuleDirectory</c>·<c>PluginDirectory</c>·<c>EngineDirectory</c> 등)에
        /// 문자열 상수를 차례로 이어 붙입니다. 기준이 없으면 <paramref name="requireBase"/>가 거짓일 때만 <c>Engine/Source</c>를 기준으로 봅니다
        /// (UBT가 상대 경로를 푸는 기준). 풀 수 없으면 null입니다.
        /// </summary>
        public string? Resolve(string expression, bool requireBase)
        {
            string? baseDirectory;
            var literals = Literals(expression).ToList();
            var call = ModuleDirectoryCall.Match(expression);
            var variable = Variables.Keys.FirstOrDefault(name => Regex.IsMatch(expression, @"\b" + Regex.Escape(name) + @"\b"));
            if (call.Success)
            {
                baseDirectory = directoryOf(call.Groups[1].Value);
                literals.Remove(call.Groups[1].Value);
            }
            else if (variable is not null) baseDirectory = Variables[variable];
            else if (Contains(expression, "ModuleDirectory")) baseDirectory = moduleDirectory;
            else if (Contains(expression, "PluginDirectory")) baseDirectory = pluginDirectory;
            else if (Contains(expression, "UEThirdPartySourceDirectory")) baseDirectory = Path.Combine(engineDirectory, "Source", "ThirdParty");
            else if (Contains(expression, "EngineSourceDirectory")) baseDirectory = Path.Combine(engineDirectory, "Source");
            else if (Contains(expression, "EngineDirectory") || Contains(expression, "EngineDir")) baseDirectory = engineDirectory;
            else if (requireBase || literals.Count == 0 || expression.IndexOf('.') >= 0 && !expression.TrimStart().StartsWith("\"", StringComparison.Ordinal)) return null;
            else baseDirectory = Path.Combine(engineDirectory, "Source");

            if (baseDirectory is null) return null;
            try
            {
                return Path.GetFullPath(literals.Aggregate(baseDirectory, (current, part) => Path.Combine(current, part.Replace("\\\\", "\\").TrimStart('/', '\\'))));
            }
            catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
            {
                return null;
            }
        }

        private static bool Contains(string text, string word) => text.IndexOf(word, StringComparison.Ordinal) >= 0;
    }

    /// <summary><c>AddRange(new string[] { a, b })</c>·<c>Add(a)</c>의 인수 식들을 바깥 쉼표로 나눕니다.</summary>
    private static IEnumerable<string> Elements(string body)
    {
        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        if (start >= 0 && end > start) body = body.Substring(start + 1, end - start - 1);
        var depth = 0;
        var inString = false;
        var current = new StringBuilder();
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (inString)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < body.Length) current.Append(body[++i]);
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.ToString().Trim().Length > 0) yield return current.ToString();
    }

    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }

        return -1;
    }

    /// <summary>문자열 안을 건너뛰며 <c>//</c>·<c>/* */</c> 주석을 지웁니다.</summary>
    internal static string StripComments(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                var verbatim = i > 0 && text[i - 1] == '@';
                result.Append(c);
                for (i++; i < text.Length; i++)
                {
                    result.Append(text[i]);
                    if (!verbatim && text[i] == '\\' && i + 1 < text.Length) result.Append(text[++i]);
                    else if (text[i] == '"') break;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                result.Append('\n');
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                result.Append(' ');
                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    private string? PluginDirectory(string moduleDirectory)
    {
        for (var current = moduleDirectory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (HasFile(current, "*.uplugin")) return current;
            if (string.Equals(current, engineDirectory, StringComparison.OrdinalIgnoreCase)) return null;
        }

        return null;
    }

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, directory, StringComparison.OrdinalIgnoreCase);

    private static bool HasFile(string directory, string pattern)
    {
        try
        {
            return System.IO.Directory.EnumerateFiles(directory, pattern).Any();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return false;
        }
    }
}
