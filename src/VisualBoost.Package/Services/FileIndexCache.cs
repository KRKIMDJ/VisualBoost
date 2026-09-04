using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace VisualBoost.Services;

internal sealed class FileIndexCache
{
    private const int FormatVersion = 1;
    private const int MaximumCachedFiles = 5_000_000;
    private const string Magic = "VisualBoost.FileIndex";

    private readonly string cacheDirectory;

    public FileIndexCache()
    {
        cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisualBoost",
            "Cache",
            "FileIndex");
    }

    public IReadOnlyList<string> Load(string solutionPath)
    {
        var cachePath = GetCachePath(solutionPath);
        if (cachePath is null || !File.Exists(cachePath))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (!string.Equals(reader.ReadString(), Magic, StringComparison.Ordinal) ||
                reader.ReadInt32() != FormatVersion ||
                !string.Equals(reader.ReadString(), NormalizeIdentity(solutionPath), StringComparison.Ordinal))
            {
                return Array.Empty<string>();
            }

            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumCachedFiles)
            {
                return Array.Empty<string>();
            }

            var paths = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                var path = reader.ReadString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is SecurityException ||
            exception is EndOfStreamException)
        {
            return Array.Empty<string>();
        }
    }

    public void Save(string solutionPath, IEnumerable<string> paths)
    {
        var cachePath = GetCachePath(solutionPath);
        if (cachePath is null)
        {
            return;
        }

        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumCachedFiles)
            .ToArray();
        var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            Directory.CreateDirectory(cacheDirectory);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(NormalizeIdentity(solutionPath));
                writer.Write(normalizedPaths.Length);
                foreach (var path in normalizedPaths)
                {
                    writer.Write(path);
                }
            }

            if (File.Exists(cachePath))
            {
                File.Replace(temporaryPath, cachePath, null);
            }
            else
            {
                File.Move(temporaryPath, cachePath);
            }
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is SecurityException)
        {
            // 캐시 저장 실패는 현재 실행의 메모리 인덱스를 무효화하지 않습니다.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is SecurityException)
            {
                // 다음 저장 시 고유 임시 파일을 사용하므로 남은 파일과 충돌하지 않습니다.
            }
        }
    }

    private string? GetCachePath(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath) || string.IsNullOrWhiteSpace(cacheDirectory))
        {
            return null;
        }

        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(NormalizeIdentity(solutionPath)));
        var fileName = BitConverter.ToString(bytes).Replace("-", string.Empty) + ".bin";
        return Path.Combine(cacheDirectory, fileName);
    }

    private static string NormalizeIdentity(string solutionPath)
    {
        try
        {
            return Path.GetFullPath(solutionPath).ToUpperInvariant();
        }
        catch (Exception exception) when (
            exception is ArgumentException ||
            exception is NotSupportedException ||
            exception is PathTooLongException)
        {
            return solutionPath.Trim().ToUpperInvariant();
        }
    }
}
