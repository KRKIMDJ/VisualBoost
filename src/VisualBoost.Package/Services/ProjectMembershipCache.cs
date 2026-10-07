using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace VisualBoost.Services;

/// <summary>
/// C++ 프로젝트 파일(vcxproj·vcxitems)의 항목 목록을 그 프로젝트 파일의 크기·수정 시각과 함께 저장합니다. Solution을 다시 열 때 프로젝트 파일이
/// 그대로면 VS 자동화로 항목을 다시 열거하지 않고 저장한 목록을 씁니다.
/// </summary>
/// <remarks>
/// Unreal Solution의 엔진 프로젝트는 항목이 10만 개를 넘어, UI thread에서 자동화로 열거하는 데 열 때마다 약 1분이 걸렸습니다(2026-10-07 측정).
/// 이 형식은 항목을 프로젝트 파일에 직접 적으므로 항목이 바뀌면 프로젝트 파일도 바뀝니다. 폴더 내용으로 항목을 정하는 SDK 형식 프로젝트는
/// 프로젝트 파일이 그대로여도 항목이 바뀔 수 있어 저장하지 않습니다. 저장하지 않은 변경이 있는 프로젝트는 호출자가 항상 다시 열거합니다.
/// </remarks>
internal sealed class ProjectMembershipCache
{
    private const int FormatVersion = 1;
    private const int MaximumEntries = 10_000;
    private const int MaximumFiles = 5_000_000;
    private const string Magic = "VisualBoost.ProjectMembership";

    private readonly string cacheDirectory;

    public ProjectMembershipCache(string? directory = null)
    {
        cacheDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualBoost", "Cache", "Projects");
    }

    public static bool IsCacheable(string? projectPath)
    {
        var extension = Path.GetExtension(projectPath ?? string.Empty);
        return string.Equals(extension, ".vcxproj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".vcxitems", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>프로젝트 파일의 현재 크기·수정 시각입니다. 읽을 수 없으면 null입니다.</summary>
    public static ProjectStamp? Stamp(string projectPath)
    {
        try
        {
            var info = new FileInfo(projectPath);
            return info.Exists ? new ProjectStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException ||
                                          exception is NotSupportedException || exception is SecurityException)
        {
            return null;
        }
    }

    /// <summary>저장된 항목 목록을 프로젝트 경로별로 읽습니다. 형식·Solution이 맞지 않거나 읽지 못하면 빈 목록입니다.</summary>
    public IReadOnlyDictionary<string, ProjectMembership> Load(string solutionPath)
    {
        var empty = new Dictionary<string, ProjectMembership>(StringComparer.OrdinalIgnoreCase);
        var cachePath = GetCachePath(solutionPath);
        if (cachePath is null || !File.Exists(cachePath)) return empty;
        try
        {
            using var reader = new BinaryReader(new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read), Encoding.UTF8, leaveOpen: false);
            if (reader.ReadString() != Magic || reader.ReadInt32() != FormatVersion || reader.ReadString() != NormalizeIdentity(solutionPath)) return empty;
            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumEntries) return empty;
            var result = new Dictionary<string, ProjectMembership>(StringComparer.OrdinalIgnoreCase);
            var total = 0;
            for (var i = 0; i < count; i++)
            {
                var project = reader.ReadString();
                var stamp = new ProjectStamp(reader.ReadInt64(), reader.ReadInt64());
                var fileCount = reader.ReadInt32();
                total += fileCount;
                if (fileCount < 0 || total > MaximumFiles) return empty;
                var files = new string[fileCount];
                for (var f = 0; f < fileCount; f++) files[f] = reader.ReadString();
                result[project] = new ProjectMembership(project, stamp, files);
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException ||
                                          exception is EndOfStreamException)
        {
            return empty;
        }
    }

    public void Save(string solutionPath, IEnumerable<ProjectMembership> entries)
    {
        var cachePath = GetCachePath(solutionPath);
        if (cachePath is null) return;
        var list = entries.Where(entry => IsCacheable(entry.ProjectPath)).Take(MaximumEntries).ToArray();
        var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            using (var writer = new BinaryWriter(new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None), Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(NormalizeIdentity(solutionPath));
                writer.Write(list.Length);
                foreach (var entry in list)
                {
                    writer.Write(entry.ProjectPath);
                    writer.Write(entry.Stamp.Length);
                    writer.Write(entry.Stamp.LastWriteUtcTicks);
                    writer.Write(entry.Files.Count);
                    foreach (var file in entry.Files) writer.Write(file);
                }
            }

            if (File.Exists(cachePath)) File.Replace(temporaryPath, cachePath, null);
            else File.Move(temporaryPath, cachePath);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
        {
            // 저장 실패는 다음 열기에서 자동화로 다시 열거하게 할 뿐입니다.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
            {
                // 다음 저장은 새 임시 이름을 쓰므로 남은 파일과 겹치지 않습니다.
            }
        }
    }

    private string? GetCachePath(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath)) return null;
        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(NormalizeIdentity(solutionPath)));
        return Path.Combine(cacheDirectory, BitConverter.ToString(bytes).Replace("-", string.Empty) + ".bin");
    }

    private static string NormalizeIdentity(string solutionPath)
    {
        try
        {
            return Path.GetFullPath(solutionPath).ToUpperInvariant();
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException ||
                                          exception is SecurityException)
        {
            return solutionPath.Trim().ToUpperInvariant();
        }
    }
}

internal readonly struct ProjectStamp : IEquatable<ProjectStamp>
{
    public ProjectStamp(long length, long lastWriteUtcTicks)
    {
        Length = length;
        LastWriteUtcTicks = lastWriteUtcTicks;
    }

    public long Length { get; }

    public long LastWriteUtcTicks { get; }

    public bool Equals(ProjectStamp other) => Length == other.Length && LastWriteUtcTicks == other.LastWriteUtcTicks;

    public override bool Equals(object? obj) => obj is ProjectStamp other && Equals(other);

    public override int GetHashCode() => Length.GetHashCode() ^ LastWriteUtcTicks.GetHashCode();
}

/// <summary>프로젝트 하나의 항목 목록과, 목록을 읽기 직전의 프로젝트 파일 크기·수정 시각입니다.</summary>
internal sealed class ProjectMembership
{
    public ProjectMembership(string projectPath, ProjectStamp stamp, IReadOnlyList<string> files)
    {
        ProjectPath = projectPath ?? throw new ArgumentNullException(nameof(projectPath));
        Stamp = stamp;
        Files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public string ProjectPath { get; }

    public ProjectStamp Stamp { get; }

    public IReadOnlyList<string> Files { get; }
}
