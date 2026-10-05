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
}

/// <summary>clangd에 넘길 compilation database와 그 출처입니다.</summary>
public sealed class CompileContext
{
    public CompileContext(CompileContextKind kind, string directory, IReadOnlyList<CompileCommand> commands, string summary, string? reason,
        string? engineRoot, bool changed)
    {
        Kind = kind;
        Directory = directory;
        Commands = commands;
        Summary = summary;
        Reason = reason;
        EngineRoot = engineRoot;
        Changed = changed;
    }

    public CompileContextKind Kind { get; }

    /// <summary>compile_commands.json과 clangd 색인 캐시(.cache/clangd)를 두는 VisualBoost 전용 폴더입니다.</summary>
    public string Directory { get; }

    public IReadOnlyList<CompileCommand> Commands { get; }

    public string Summary { get; }

    public string? Reason { get; }

    /// <summary>Unreal 엔진 설치 루트(<c>Engine</c> 폴더의 부모)입니다.</summary>
    public string? EngineRoot { get; }

    /// <summary>이번 준비에서 compile_commands.json 내용이 바뀌었습니다.</summary>
    public bool Changed { get; }

    /// <summary>엔진 파일 근사 명령의 모듈 매크로 재정의 헤더 폴더입니다.</summary>
    public string OverrideDirectory => Path.Combine(Directory, "modules");

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
    public static CompileContext Prepare(string solutionPath, string cacheRoot, string? engineRoot, string compiler, CancellationToken cancellationToken = default,
        CompileCommandSources? sources = null)
    {
        var solutionDirectory = WorkspaceDirectory(solutionPath);
        var directory = CacheDirectory(cacheRoot, solutionPath);
        string? unrealReason = null;
        var project = FindUnrealProject(solutionDirectory);
        if (project is not null)
        {
            var variant = UnrealCompileCommands.DetectVariant(solutionDirectory);
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
                var result = UnrealCompileCommands.Build(solutionDirectory, engineRoot, variant, compiler, cancellationToken);
                if (result.Commands.Count > 0)
                {
                    var changed = CompileCommandDatabase.WriteIfChanged(directory, result.Commands);
                    var summary = $"Unreal {variant} · 컴파일 명령 {result.Commands.Count:N0}개(모듈 {result.Modules:N0})";
                    return new CompileContext(CompileContextKind.Unreal, directory, result.Commands, summary, null, engineRoot, changed);
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
                var changed = CompileCommandDatabase.WriteIfChanged(directory, commands);
                return new CompileContext(CompileContextKind.Database, directory, commands, $"{database} · 컴파일 명령 {commands.Count:N0}개", null,
                    engineRoot, changed);
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
                var changed = CompileCommandDatabase.WriteIfChanged(directory, ninjaCommands);
                var summary = $"Ninja 빌드 파일({buildDirectory}) · 컴파일 명령 {ninjaCommands.Count:N0}개";
                return new CompileContext(CompileContextKind.Ninja, directory, ninjaCommands, summary, null, engineRoot, changed);
            }

            toolReason = "Ninja 빌드 파일에서 컴파일 명령을 얻지 못했습니다" + (error is null ? "." : ": " + error);
        }

        if (project is null && sources?.MsBuildPath is not null && sources.Projects.Count > 0)
        {
            var result = MsBuildCompileCommands.Query(sources.MsBuildPath, solutionPath, sources.Projects, Path.Combine(directory, "msbuild"), compiler,
                TimeSpan.FromMinutes(3), cancellationToken);
            if (result.Commands.Count > 0)
            {
                var changed = CompileCommandDatabase.WriteIfChanged(directory, result.Commands);
                var summary = $"C++ 프로젝트 {result.AnsweredProjects:N0}/{result.Projects:N0}개의 MSBuild 설계 시점 명령 · 컴파일 명령 {result.Commands.Count:N0}개";
                return new CompileContext(CompileContextKind.MsBuild, directory, result.Commands, summary, null, engineRoot, changed);
            }

            toolReason = "C++ 프로젝트에서 컴파일 명령을 얻지 못했습니다" + (result.Error is null ? "." : ": " + result.Error);
        }

        return new CompileContext(CompileContextKind.None, directory, Array.Empty<CompileCommand>(), string.Empty,
            unrealReason ?? toolReason ?? "compile_commands.json을 찾지 못했습니다.", engineRoot, false);
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
