using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 폴더 열기 작업 영역(CMake 등)의 Ninja 빌드 파일에서 컴파일 명령을 뽑습니다.
/// </summary>
/// <remarks>
/// VS의 CMake 통합은 기본으로 Ninja 빌드 파일만 만들고 compile_commands.json은 만들지 않습니다.
/// <c>ninja -t compdb</c>는 빌드하지 않고 빌드 파일의 명령을 그대로 내보내는 공개 도구 기능입니다(CMake의 database 생성도 같은 방식).
/// cl 명령은 clang-cl 드라이버로 바꾸고 출력·의존성 옵션을 빼며, 링크·유틸리티 항목은 소스 확장자로 거릅니다.
/// MSVC·SDK include는 cl이 환경 변수로 받으므로 명령에 없고, clang-cl이 설치된 VS에서 찾습니다.
/// </remarks>
public static class NinjaCompileCommands
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".c", ".cc", ".cpp", ".cxx", ".c++" };
    private static readonly HashSet<string> ClCompilers = new(StringComparer.OrdinalIgnoreCase) { "cl.exe", "cl", "clang-cl.exe", "clang-cl" };

    /// <summary>VS 설치에 포함된 ninja입니다(C++ CMake 도구 구성 요소).</summary>
    public static string? FindNinja(string visualStudioRoot)
    {
        var candidate = Path.Combine(visualStudioRoot, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "Ninja", "ninja.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>흔한 CMake 출력 폴더(out/build/*, build, build/*)에서 가장 최근에 생성된 Ninja 빌드 폴더를 찾습니다.</summary>
    public static string? FindBuildDirectory(string workspace)
    {
        var candidates = new List<string> { Path.Combine(workspace, "build") };
        candidates.AddRange(SafeDirectories(Path.Combine(workspace, "out", "build")));
        candidates.AddRange(SafeDirectories(Path.Combine(workspace, "build")));
        return candidates
            .Select(directory => Path.Combine(directory, "build.ninja"))
            .Where(File.Exists)
            .OrderByDescending(SafeWriteTime)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault();
    }

    public static (IReadOnlyList<CompileCommand> Commands, string? Error) Query(string ninja, string buildDirectory, string compiler, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var run = ToolProcess.Run("Ninja", ninja, $"-C \"{buildDirectory}\" -t compdb", buildDirectory, timeout, 256 * 1024 * 1024, cancellationToken);
        if (run.Error is not null)
        {
            return (Array.Empty<CompileCommand>(), run.Error);
        }

        try
        {
            return (Convert(JsonValue.Parse(run.Output), compiler), null);
        }
        catch (FormatException exception)
        {
            return (Array.Empty<CompileCommand>(), "ninja 출력 형식 오류: " + exception.Message);
        }
    }

    /// <summary>compdb 항목 중 C/C++ 소스 컴파일만 clangd 명령으로 바꿉니다. 같은 파일은 처음 항목을 씁니다.</summary>
    public static IReadOnlyList<CompileCommand> Convert(JsonValue entries, string compiler)
    {
        var commands = new Dictionary<string, CompileCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Items)
        {
            if (CompileCommand.FromJson(entry) is not { } command || !SourceExtensions.Contains(Path.GetExtension(command.File)))
            {
                continue;
            }

            var source = UnrealCompileCommands.Normalize(command.File);
            if (commands.ContainsKey(source)) continue;
            var tool = Path.GetFileName(command.Arguments[0]);
            // gcc 형식 드라이버(clang++ 등)는 clangd가 그대로 이해하므로 바꾸지 않습니다.
            var arguments = ClCompilers.Contains(tool)
                ? new[] { compiler, "--driver-mode=cl" }.Concat(MsBuildCompileCommands.Sanitize(command.Arguments.Skip(1).ToArray())).ToArray()
                : command.Arguments.ToArray();
            commands[source] = new CompileCommand(UnrealCompileCommands.Normalize(command.Directory), source, arguments);
        }

        return commands.Values.OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase).ToArray();
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
