using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>컴파일 명령을 물어볼 C++ 프로젝트와 Solution의 활성 구성에 대응하는 프로젝트 구성입니다.</summary>
public sealed class MsBuildProjectConfiguration
{
    public MsBuildProjectConfiguration(string path, string configuration, string platform)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Configuration = configuration ?? string.Empty;
        Platform = platform ?? string.Empty;
    }

    public string Path { get; }

    public string Configuration { get; }

    public string Platform { get; }

    public override string ToString() => $"{System.IO.Path.GetFileName(Path)} ({Configuration}|{Platform})";
}

public sealed class MsBuildCompileCommandResult
{
    public MsBuildCompileCommandResult(IReadOnlyList<CompileCommand> commands, int projects, int answeredProjects, string? error)
    {
        Commands = commands;
        Projects = projects;
        AnsweredProjects = answeredProjects;
        Error = error;
    }

    public IReadOnlyList<CompileCommand> Commands { get; }

    /// <summary>물어본 프로젝트 수입니다.</summary>
    public int Projects { get; }

    /// <summary>명령을 돌려준 프로젝트 수입니다. 나머지는 열 수 없거나 C++ 설계 시점 대상이 없는 프로젝트입니다.</summary>
    public int AnsweredProjects { get; }

    /// <summary>MSBuild 실행 자체가 실패한 이유입니다.</summary>
    public string? Error { get; }
}

/// <summary>
/// compile_commands.json이 없는 일반 C++ 프로젝트(vcxproj)의 컴파일 명령을 MSBuild 설계 시점 대상으로 얻습니다.
/// </summary>
/// <remarks>
/// VS가 IntelliSense에 쓰는 공개 대상 <c>GetClCommandLines</c>·<c>GetProjectDirectories</c>를 빌드 없이 실행합니다.
/// 결과 형식 옵션(-getTargetResult)은 VS 2022의 MSBuild가 따옴표가 든 항목에서 실패하므로, 캐시 폴더의 래퍼 프로젝트가
/// 결과를 파일로 씁니다. 사용자 프로젝트 폴더에는 아무것도 쓰지 않습니다.
/// - 시스템 include(VC++ 디렉터리)는 cl.exe가 환경 변수로 받으므로 명령에 없어 /imsvc로 더합니다.
/// - PCH·출력 옵션은 Unreal 변환과 같이 빼되, 강제 include는 PCH 헤더라도 남깁니다. 일반 프로젝트는 소스가 PCH 헤더를
///   직접 포함하지 않을 수 있기 때문입니다.
/// - C++/CLI·WinRT 옵션처럼 clang이 이해하지 못하는 옵션은 빼고, 모듈 인터페이스(.ixx)는 제외합니다.
/// </remarks>
public static class MsBuildCompileCommands
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".c", ".cc", ".cpp", ".cxx", ".c++" };
    private static readonly string[] ExtraDropPrefixes = { "/showIncludes", "/clr", "/ZW", "/FU", "/AI", "/scanDependencies", "/scanModules", "/interface", "/libraryModuleName" };
    private static readonly string[] ExtraDropWithValue = { "/ifcOutput", "/ifcSearchDir", "/reference", "/headerUnit", "/headerName:quote", "/headerName:angle" };

    /// <summary>VS 설치 루트의 MSBuild를 찾습니다. 64비트 MSBuild를 우선합니다.</summary>
    public static string? FindMsBuild(string visualStudioRoot)
    {
        foreach (var relative in new[] { Path.Combine("MSBuild", "Current", "Bin", "amd64", "MSBuild.exe"), Path.Combine("MSBuild", "Current", "Bin", "MSBuild.exe") })
        {
            var candidate = Path.Combine(visualStudioRoot, relative);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <param name="workDirectory">래퍼 프로젝트와 결과 파일을 둘 VisualBoost 캐시 폴더입니다.</param>
    /// <param name="compiler">명령의 첫 인자로 쓸 cl 호환 컴파일러 경로입니다.</param>
    public static MsBuildCompileCommandResult Query(string msbuild, string solutionPath, IReadOnlyList<MsBuildProjectConfiguration> projects,
        string workDirectory, string compiler, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (projects.Count == 0)
        {
            return new MsBuildCompileCommandResult(Array.Empty<CompileCommand>(), 0, 0, null);
        }

        Directory.CreateDirectory(workDirectory);
        var wrapper = Path.Combine(workDirectory, "collect.proj");
        var output = Path.Combine(workDirectory, "commands.txt");
        File.WriteAllText(wrapper, CreateWrapper(solutionPath, projects), new UTF8Encoding(false));
        if (File.Exists(output)) File.Delete(output);

        var error = ToolProcess.Run("MSBuild", msbuild, $"\"{wrapper}\" -nologo -nodeReuse:false -m:{Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2))} -v:q " +
            $"-t:Collect \"-p:VisualBoostOutputFile={Escape(output)}\"", workDirectory, timeout, 64 * 1024, cancellationToken).Error;
        if (!File.Exists(output))
        {
            return new MsBuildCompileCommandResult(Array.Empty<CompileCommand>(), projects.Count, 0, error ?? "MSBuild가 결과를 쓰지 않았습니다.");
        }

        var commands = Parse(File.ReadAllLines(output, Encoding.UTF8), compiler, out var answered);
        return new MsBuildCompileCommandResult(commands, projects.Count, answered, commands.Count == 0 ? error : null);
    }

    /// <summary>
    /// 래퍼가 쓴 줄(대상, 프로젝트, 작업 폴더, 파일 목록, include 경로, 외부 include 경로, 명령줄; 탭 구분)을 명령으로 바꿉니다.
    /// </summary>
    public static IReadOnlyList<CompileCommand> Parse(IEnumerable<string> lines, string compiler, out int answeredProjects)
    {
        var rows = lines.Select(line => line.Split(new[] { '\t' }, 7)).Where(parts => parts.Length == 7).ToArray();
        var includes = rows.Where(r => r[0] == "GetProjectDirectories")
            .GroupBy(r => r[1], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => SystemIncludes(g.First()[4] + ";" + g.First()[5]), StringComparer.OrdinalIgnoreCase);
        var commands = new Dictionary<string, CompileCommand>(StringComparer.OrdinalIgnoreCase);
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => r[0] == "GetClCommandLines"))
        {
            var directory = row[2].Length > 0 ? row[2] : Path.GetDirectoryName(row[1]) ?? string.Empty;
            var arguments = Sanitize(CommandLine.Split(row[6]));
            var system = includes.TryGetValue(row[1], out var found) ? found : Array.Empty<string>();
            foreach (var file in row[3].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!SourceExtensions.Contains(Path.GetExtension(file))) continue;
                var source = UnrealCompileCommands.Normalize(Path.IsPathRooted(file) ? file : Path.Combine(directory, file));
                answered.Add(row[1]);
                if (commands.ContainsKey(source)) continue;
                commands[source] = new CompileCommand(UnrealCompileCommands.Normalize(directory), source,
                    new[] { compiler, "--driver-mode=cl" }.Concat(arguments).Concat(system.SelectMany(dir => new[] { "/imsvc", dir })).Concat(new[] { source }).ToArray());
            }
        }

        answeredProjects = answered.Count;
        return commands.Values.OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> Sanitize(IReadOnlyList<string> tokens)
    {
        var result = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (UnrealCompileCommands.DropWithValue.Contains(token, StringComparer.Ordinal) || ExtraDropWithValue.Contains(token, StringComparer.Ordinal))
            {
                i++;
                continue;
            }

            if (UnrealCompileCommands.DropPrefixes.Any(p => token.StartsWith(p, StringComparison.Ordinal)) ||
                ExtraDropPrefixes.Any(p => token.StartsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }

            if (token.StartsWith("/FI", StringComparison.Ordinal))
            {
                var header = token.Length > 3 ? token.Substring(3) : i + 1 < tokens.Count ? tokens[++i] : string.Empty;
                // clang driver가 옆의 MSVC .pch를 자동 선택하지 않도록 -Xclang -include로 넘깁니다.
                if (header.Length > 0) result.AddRange(new[] { "-Xclang", "-include", "-Xclang", UnrealCompileCommands.Normalize(header) });
                continue;
            }

            result.Add(token);
        }

        return result;
    }

    public static string CreateWrapper(string solutionPath, IReadOnlyList<MsBuildProjectConfiguration> projects)
    {
        var solution = Path.GetFullPath(solutionPath);
        var solutionDirectory = Path.GetDirectoryName(solution)!.TrimEnd('\\', '/') + "\\";
        var builder = new StringBuilder();
        builder.AppendLine("<Project>");
        builder.AppendLine("  <ItemGroup>");
        foreach (var project in projects)
        {
            // VS가 프로젝트를 빌드할 때 주는 Solution 속성을 같이 줘야 $(SolutionDir)을 쓰는 include 경로가 맞습니다.
            var properties = string.Join(";", new[]
            {
                "Configuration=" + Escape(project.Configuration),
                "Platform=" + Escape(project.Platform),
                "DesignTimeBuild=true",
                "SolutionDir=" + Escape(solutionDirectory),
                "SolutionPath=" + Escape(solution),
                "SolutionName=" + Escape(Path.GetFileNameWithoutExtension(solution)),
                "SolutionFileName=" + Escape(Path.GetFileName(solution)),
                "SolutionExt=" + Escape(Path.GetExtension(solution))
            });
            builder.Append("    <VisualBoostProject Include=\"").Append(SecurityElement.Escape(Escape(project.Path)))
                .Append("\" AdditionalProperties=\"").Append(SecurityElement.Escape(properties)).AppendLine("\" />");
        }

        builder.AppendLine("  </ItemGroup>");
        builder.AppendLine("  <Target Name=\"Collect\">");
        builder.AppendLine("    <MSBuild Projects=\"@(VisualBoostProject)\" Targets=\"GetProjectDirectories;GetClCommandLines\" BuildInParallel=\"true\" ContinueOnError=\"true\">");
        builder.AppendLine("      <Output TaskParameter=\"TargetOutputs\" ItemName=\"VisualBoostOutput\" />");
        builder.AppendLine("    </MSBuild>");
        builder.AppendLine("    <WriteLinesToFile File=\"$(VisualBoostOutputFile)\" Overwrite=\"true\" Encoding=\"utf-8\" Lines=\"@(VisualBoostOutput->'" +
                           "%(MSBuildSourceTargetName)%09%(MSBuildSourceProjectFile)%09%(WorkingDirectory)%09%(Files)%09%(IncludePath)%09%(ExternalIncludePath)%09%(Identity)')\" />");
        builder.AppendLine("  </Target>");
        builder.AppendLine("</Project>");
        return builder.ToString();
    }

    /// <summary>MSBuild 특수 문자를 %xx로 이스케이프합니다(경로의 ; $ @ 등이 목록·속성으로 해석되지 않게).</summary>
    public static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '%' or '$' or '@' or '\'' or ';' or '?' or '*')
            {
                builder.Append('%').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string[] SystemIncludes(string paths) =>
        paths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && Path.IsPathRooted(p))
            .Select(p => UnrealCompileCommands.Normalize(p).TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
