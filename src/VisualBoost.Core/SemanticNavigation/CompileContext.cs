using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace VisualBoost.Core.SemanticNavigation;

public enum CompileContextKind
{
    /// <summary>쓸 수 있는 컴파일 명령이 없습니다. <see cref="CompileContext.Reason"/>에 이유가 있습니다.</summary>
    None,

    /// <summary>Unreal 일반 빌드 응답 파일을 변환했습니다.</summary>
    Unreal,

    /// <summary>Solution 근처의 기존 compile_commands.json을 가져왔습니다.</summary>
    Database,

    /// <summary>C++ 프로젝트(vcxproj)의 MSBuild 설계 시점 대상으로 명령을 만들었습니다.</summary>
    MsBuild,

    /// <summary>CMake 등이 만든 Ninja 빌드 파일에서 명령을 뽑았습니다(폴더 열기 작업 영역).</summary>
    Ninja
}

/// <summary>compile_commands.json이 없을 때 명령을 얻을 빌드 도구들입니다. 없는 도구의 단계는 건너뜁니다.</summary>
public sealed class CompileCommandSources
{
    /// <summary>C++ 프로젝트에 명령을 물어볼 MSBuild입니다.</summary>
    public string? MsBuildPath { get; set; }

    /// <summary>Solution의 C++ 프로젝트와 활성 구성입니다.</summary>
    public IReadOnlyList<MsBuildProjectConfiguration> Projects { get; set; } = Array.Empty<MsBuildProjectConfiguration>();

    /// <summary>Ninja 빌드 파일에서 명령을 뽑을 ninja입니다.</summary>
    public string? NinjaPath { get; set; }

    /// <summary>VS의 활성 Solution 구성 이름입니다. Unreal 빌드 대상·구성 선택에 씁니다.</summary>
    public string? SolutionConfiguration { get; set; }
}

/// <summary>clangd에 넘길 compilation database와 그 출처입니다.</summary>
public sealed class CompileContext
{
    public CompileContext(CompileContextKind kind, string directory, IReadOnlyList<CompileCommand> commands, string summary, string? reason,
        string? engineRoot, bool changed, PathAliases? paths = null, UnrealIndexPlan? plan = null, UnrealModuleGraph? moduleGraph = null)
    {
        Kind = kind;
        Directory = directory;
        Commands = commands;
        Summary = summary;
        Reason = reason;
        EngineRoot = engineRoot;
        Changed = changed;
        Paths = paths ?? PathAliases.None;
        Plan = plan;
        ModuleGraph = moduleGraph;
    }

    public CompileContextKind Kind { get; }

    /// <summary>compile_commands.json과 clangd 색인 캐시(.cache/clangd)를 두는 VisualBoost 전용 폴더입니다.</summary>
    public string Directory { get; }

    /// <summary>
    /// 파일별 컴파일 명령입니다. Unreal 프로젝트는 compile_commands.json에 이 대신 색인 단위 명령이 들어갑니다(<see cref="Plan"/>).
    /// </summary>
    public IReadOnlyList<CompileCommand> Commands { get; }

    /// <summary>Unreal 프로젝트의 색인 단위와 공유 PCH 사용 판단입니다. 다른 문맥이면 null입니다.</summary>
    public UnrealIndexPlan? Plan { get; }

    /// <summary>Unreal 엔진·프로젝트 모듈 규칙입니다. 명령이 없는 모듈 파일의 근사 명령에 씁니다. 다른 문맥이면 null입니다.</summary>
    public UnrealModuleGraph? ModuleGraph { get; }

    public string Summary { get; }

    public string? Reason { get; }

    /// <summary>Unreal 엔진 설치 루트(<c>Engine</c> 폴더의 부모)입니다.</summary>
    public string? EngineRoot { get; }

    /// <summary>이번 준비에서 compile_commands.json 내용이 바뀌었습니다.</summary>
    public bool Changed { get; }

    /// <summary>
    /// 링크를 거쳐 연 작업 영역·엔진 루트의 실제 경로 대응입니다. compile_commands.json에는 실제 경로로 썼고,
    /// <see cref="Commands"/>는 연 경로 그대로입니다. clangd와 주고받는 경로는 이 대응으로 바꿉니다.
    /// </summary>
    public PathAliases Paths { get; }

    /// <summary>엔진 파일 근사 명령의 모듈 매크로 재정의 헤더 폴더입니다.</summary>
    public string OverrideDirectory => OverrideDirectoryOf(Directory);

    internal static string OverrideDirectoryOf(string directory) => Path.Combine(directory, "modules");

    /// <summary>
    /// clangd가 database를 읽고 background index를 시작하도록 잠시 여는 빈 문서입니다. database에 자기 명령을 두어
    /// clangd가 가까운 TU의 명령(공유 PCH 포함)을 빌려 빈 문서에 큰 헤더 분석을 하지 않게 합니다.
    /// </summary>
    public string IndexStartPath => Path.Combine(Directory, IndexStartFileName);

    internal const string IndexStartFileName = "visualboost-index-start.cpp";

    public bool IsAvailable => Kind != CompileContextKind.None;
}

/// <summary>
/// Solution에서 컴파일 문맥을 찾아 VisualBoost 캐시 폴더에 compilation database를 만듭니다.
/// 사용자 프로젝트 폴더에는 아무것도 쓰지 않습니다.
/// </summary>
public static class CompileContextBuilder
{
    /// <summary>
    /// Solution 파일이면 그 폴더, 폴더 열기 작업 영역이면 그 폴더 자체입니다. VS는 폴더 열기 모드에서
    /// Solution 경로 대신 폴더 경로를 줍니다.
    /// </summary>
    public static string WorkspaceDirectory(string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        return Directory.Exists(full) ? full.TrimEnd('\\', '/') : Path.GetDirectoryName(full)!;
    }

    public static string CacheDirectory(string cacheRoot, string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        var name = new string(Path.GetFileNameWithoutExtension(full).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').Take(40).ToArray());
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(full.ToUpperInvariant()));
        var suffix = string.Concat(hash.Take(4).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        return Path.Combine(cacheRoot, (name.Length == 0 ? "solution" : name) + "-" + suffix);
    }

    public static string? FindUnrealProject(string solutionDirectory) =>
        SafeFiles(solutionDirectory, "*.uproject").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    /// <summary>Solution 폴더와 흔한 CMake 출력 폴더에서 가장 최근 compile_commands.json을 찾습니다.</summary>
    public static string? FindDatabase(string solutionDirectory)
    {
        var candidates = new List<string>
        {
            Path.Combine(solutionDirectory, CompileCommandDatabase.FileName),
            Path.Combine(solutionDirectory, "build", CompileCommandDatabase.FileName)
        };
        foreach (var parent in new[] { Path.Combine(solutionDirectory, "build"), Path.Combine(solutionDirectory, "out", "build") })
        {
            candidates.AddRange(SafeDirectories(parent).Select(d => Path.Combine(d, CompileCommandDatabase.FileName)));
        }

        return candidates.Where(File.Exists).OrderByDescending(SafeWriteTime).FirstOrDefault();
    }

    /// <param name="engineRoot">Unreal 프로젝트일 때 호출자가 찾은 엔진 설치 루트. 찾지 못했으면 null.</param>
    /// <param name="compiler">명령의 첫 인자로 쓸 cl 호환 컴파일러 경로. clangd는 이 이름으로 드라이버 모드를 고릅니다.</param>
    /// <param name="solutionPath">Solution 파일 또는 폴더 열기 작업 영역 폴더입니다.</param>
    /// <param name="sources">compile_commands.json이 없을 때 쓸 빌드 도구입니다.</param>
    /// <param name="pchMode">Unreal 공유 PCH 헤더를 분석 명령에 넣는 방식입니다.</param>
    /// <param name="resetIndex">
    /// 색인 형식 번호가 바뀌었으면 색인 파일을 지웁니다. clangd를 띄우기 전에만 참으로 부릅니다. 실행 중인 clangd 아래에서 지우면 쓰는 중인
    /// 파일만 남아 일부만 지워집니다(2026-10-09 검토 52).
    /// </param>
    public static CompileContext Prepare(string solutionPath, string cacheRoot, string? engineRoot, string compiler, CancellationToken cancellationToken = default,
        CompileCommandSources? sources = null, UnrealPchMode pchMode = UnrealPchMode.Auto, bool resetIndex = true)
    {
        var solutionDirectory = WorkspaceDirectory(solutionPath);
        var directory = CacheDirectory(cacheRoot, solutionPath);
        if (resetIndex) ResetIndexIfFormatChanged(directory);
        // 링크를 거쳐 연 작업 영역이면 clangd에는 실제 경로를 줍니다(PathAliases 참고).
        var paths = PathAliases.ForRoots(new[] { solutionDirectory, engineRoot });
        string? unrealReason = null;
        var project = FindUnrealProject(solutionDirectory);
        if (project is not null)
        {
            var variant = UnrealCompileCommands.DetectVariant(solutionDirectory, sources?.SolutionConfiguration);
            if (engineRoot is null)
            {
                unrealReason = "Unreal 엔진 설치 경로를 찾지 못했습니다.";
            }
            else if (variant is null)
            {
                unrealReason = "Unreal 빌드 응답 파일이 없습니다. 프로젝트를 한 번 빌드하세요.";
            }
            else
            {
                var result = UnrealCompileCommands.Build(solutionDirectory, engineRoot, variant, compiler, cancellationToken,
                    CompileContext.OverrideDirectoryOf(directory));
                if (result.Commands.Count > 0)
                {
                    var plan = UnrealIndexPlan.Create(directory, result, paths, pchMode);
                    var changed = plan.Write();
                    var summary = $"Unreal {variant} · 컴파일 명령 {result.Commands.Count:N0}개(모듈 {result.Modules:N0}" +
                                  (plan.GroupedUnitCount > 0 ? $", unity 묶음 {plan.GroupedUnitCount:N0}개로 {plan.GroupedMemberCount:N0}개 색인" : string.Empty) +
                                  PchSummary(plan) +
                                  (result.Supplemented > 0 ? $", 빌드 기록 없는 파일 {result.Supplemented:N0}개 보완" : string.Empty) +
                                  (result.UnreadableDirectories > 0 ? $", 읽지 못해 건너뛴 소스 폴더 {result.UnreadableDirectories:N0}개" : string.Empty) + ")";
                    return new CompileContext(CompileContextKind.Unreal, directory, result.Commands, summary, null, engineRoot, changed, paths, plan,
                        UnrealModuleGraph.For(engineRoot, solutionDirectory));
                }

                unrealReason = $"Unreal 빌드 응답 파일({variant})에서 컴파일 명령을 만들지 못했습니다.";
            }
        }

        var database = FindDatabase(solutionDirectory);
        if (database is not null)
        {
            var commands = CompileCommandDatabase.Read(database);
            if (commands.Count > 0)
            {
                var changed = WriteDatabase(directory, paths.ToReal(commands));
                return new CompileContext(CompileContextKind.Database, directory, commands, $"{database} · 컴파일 명령 {commands.Count:N0}개", null,
                    engineRoot, changed, paths);
            }
        }

        // Unreal의 NMake 프로젝트는 설계 시점 명령이 엔진 전체를 가리키므로 응답 파일 경로만 씁니다.
        string? toolReason = null;
        var buildDirectory = project is null && sources?.NinjaPath is not null ? NinjaCompileCommands.FindBuildDirectory(solutionDirectory) : null;
        if (buildDirectory is not null)
        {
            var (ninjaCommands, error) = NinjaCompileCommands.Query(sources!.NinjaPath!, buildDirectory, compiler, TimeSpan.FromMinutes(1), cancellationToken);
            if (ninjaCommands.Count > 0)
            {
                var changed = WriteDatabase(directory, paths.ToReal(ninjaCommands));
                var summary = $"Ninja 빌드 파일({buildDirectory}) · 컴파일 명령 {ninjaCommands.Count:N0}개";
                return new CompileContext(CompileContextKind.Ninja, directory, ninjaCommands, summary, null, engineRoot, changed, paths);
            }

            toolReason = "Ninja 빌드 파일에서 컴파일 명령을 얻지 못했습니다" + (error is null ? "." : ": " + error);
        }

        if (project is null && sources?.MsBuildPath is not null && sources.Projects.Count > 0)
        {
            var result = MsBuildCompileCommands.Query(sources.MsBuildPath, solutionPath, sources.Projects, Path.Combine(directory, "msbuild"), compiler,
                TimeSpan.FromMinutes(3), cancellationToken);
            if (result.Commands.Count > 0)
            {
                var changed = WriteDatabase(directory, paths.ToReal(result.Commands));
                var summary = $"C++ 프로젝트 {result.AnsweredProjects:N0}/{result.Projects:N0}개의 MSBuild 설계 시점 명령 · 컴파일 명령 {result.Commands.Count:N0}개";
                return new CompileContext(CompileContextKind.MsBuild, directory, result.Commands, summary, null, engineRoot, changed, paths);
            }

            toolReason = "C++ 프로젝트에서 컴파일 명령을 얻지 못했습니다" + (result.Error is null ? "." : ": " + result.Error);
        }

        return new CompileContext(CompileContextKind.None, directory, Array.Empty<CompileCommand>(), string.Empty,
            unrealReason ?? toolReason ?? "compile_commands.json을 찾지 못했습니다.", engineRoot, false);
    }

    /// <summary>
    /// VisualBoost가 clangd에 주는 색인 단위의 분석 결과가 달라지는 변경마다 올리는 번호입니다. 2: PCH 없는 단위의 조건식 매크로 확인과
    /// 엔진 모듈 규칙 근사 명령.
    /// </summary>
    public const int IndexFormat = 2;

    public const string IndexFormatFileName = "index-format.txt";

    /// <summary>
    /// 색인 형식 번호가 다르면 clangd 색인 파일을 지웁니다. clangd background index는 내용이 같은 파일의 색인 파일을 이전 색인에 분석 오류가
    /// 있었을 때만 다시 씁니다(clangd Background.cpp). 그래서 색인 단위가 바뀌어 같은 파일을 더 완전히 분석해도(예: PCH 없이 조건부 구역이
    /// 오류 없이 꺼졌던 파일) 이전의 빈 색인 파일이 남습니다(2026-10-09 정확도 시험). clangd를 띄우기 전에 부르며, 지우지 못하면 번호를
    /// 쓰지 않아 다음 준비에서 다시 시도합니다.
    /// </summary>
    internal static void ResetIndexIfFormatChanged(string directory)
    {
        var stamp = Path.Combine(directory, IndexFormatFileName);
        var current = IndexFormat.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == current) return;
            var index = Path.Combine(directory, ".cache", "clangd", "index");
            if (Directory.Exists(index)) Directory.Delete(index, true);
            Directory.CreateDirectory(directory);
            File.WriteAllText(stamp, current);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // 다른 프로세스가 색인 파일을 쓰는 중이면 다음 준비에서 다시 시도합니다.
        }
    }

    private static string PchSummary(UnrealIndexPlan plan)
    {
        if (plan.SwitchableUnitCount == 0) return string.Empty;
        return plan.Mode switch
        {
            UnrealPchMode.Always => ", 공유 PCH 포함",
            UnrealPchMode.Auto when plan.PchUnitCount > 0 => $", 공유 PCH 포함 단위 {plan.PchUnitCount:N0}개",
            _ => string.Empty
        };
    }

    /// <summary>
    /// database를 쓰고 색인 시작 문서(<see cref="CompileContext.IndexStartPath"/>)의 명령을 덧붙입니다. 명령이 없으면 clangd가
    /// 가까운 TU 명령을 빌려 빈 문서에도 강제 include(Unreal 공유 PCH 등)를 분석했습니다(테스트 전용 UE 샘플에서 15.7초, 수 GB).
    /// 덮어쓰기 명령(<c>compilationDatabaseChanges</c>)으로 주면 clangd가 database를 찾지 않아 background index가 시작하지 않으므로
    /// database 안에 둡니다. background index도 이 빈 파일을 읽으므로 캐시 폴더에 빈 파일을 둡니다.
    /// </summary>
    internal static bool WriteDatabase(string directory, IEnumerable<CompileCommand> commands)
    {
        var list = commands.ToList();
        if (list.Count > 0)
        {
            var probe = Path.Combine(directory, CompileContext.IndexStartFileName).Replace('\\', '/');
            var sample = list[0].Arguments;
            var arguments = new List<string> { sample[0] };
            if (sample.Count > 1 && sample[1].StartsWith("--driver-mode=", StringComparison.Ordinal)) arguments.Add(sample[1]);
            arguments.Add(probe);
            list.Add(new CompileCommand(directory.Replace('\\', '/'), probe, arguments.ToArray()));
            System.IO.Directory.CreateDirectory(directory);
            if (!File.Exists(probe) || new FileInfo(probe).Length != 0) File.WriteAllText(probe, string.Empty);
        }

        return CompileCommandDatabase.WriteIfChanged(directory, list);
    }

    private static IEnumerable<string> SafeFiles(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly) : Array.Empty<string>();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeDirectories(string directory)
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
}
