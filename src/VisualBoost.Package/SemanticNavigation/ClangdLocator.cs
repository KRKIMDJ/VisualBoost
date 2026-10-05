using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace VisualBoost.SemanticNavigation;

/// <summary>clangd 실행 파일을 찾습니다. 제품에 포함하지 않고 Visual Studio의 C++ Clang 도구를 씁니다.</summary>
internal static class ClangdLocator
{
    /// <param name="configuredPath">옵션에서 지정한 경로. 비어 있으면 자동으로 찾습니다.</param>
    public static string? Find(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            // 사용자가 지정한 경로가 없으면 다른 버전으로 몰래 대체하지 않습니다.
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }

        var install = CurrentInstallDirectory();
        var current = install is null ? null : InInstall(install);
        if (current is not null) return current;

        // 현재 VS에 구성 요소가 없어도 다른 VS 설치나 LLVM 설치의 clangd를 쓸 수 있습니다(독립 실행 파일).
        var other = InstallRoots()
            .Select(InInstall)
            .Where(path => path is not null)
            .Select(path => path!)
            .Concat(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", "clangd.exe") }
                .Where(File.Exists))
            .OrderByDescending(FileVersion)
            .FirstOrDefault();
        if (other is not null) return other;

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => SafeCombine(directory.Trim('"'), "clangd.exe"))
            .FirstOrDefault(path => path is not null && File.Exists(path));
    }

    private static string? InInstall(string install)
    {
        foreach (var relative in new[] { Path.Combine("VC", "Tools", "Llvm", "x64", "bin"), Path.Combine("VC", "Tools", "Llvm", "bin") })
        {
            var candidate = Path.Combine(install, relative, "clangd.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>표준 설치 폴더(<c>Microsoft Visual Studio\&lt;버전&gt;\&lt;에디션&gt;</c>)를 나열합니다.</summary>
    private static string[] InstallRoots()
    {
        try
        {
            return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Microsoft Visual Studio"))
                .Where(Directory.Exists)
                .SelectMany(Directory.GetDirectories)
                .SelectMany(Directory.GetDirectories)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static Version FileVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    /// <summary>devenv.exe가 있는 <c>Common7\IDE</c>의 두 단계 위가 설치 루트입니다.</summary>
    internal static string? CurrentInstallDirectory()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var ide = Path.GetDirectoryName(process.MainModule?.FileName ?? string.Empty);
            var common = ide is null ? null : Path.GetDirectoryName(ide);
            return common is null ? null : Path.GetDirectoryName(common);
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is System.ComponentModel.Win32Exception ||
                                          exception is NotSupportedException)
        {
            return null;
        }
    }

    private static string? SafeCombine(string directory, string file)
    {
        try
        {
            return Path.Combine(directory, file);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
