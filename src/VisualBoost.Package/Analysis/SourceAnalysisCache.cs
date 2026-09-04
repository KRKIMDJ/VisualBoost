using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Analysis;

internal sealed class SourceAnalysisCache
{
    private const string Magic = "VisualBoost.SourceAnalysis";
    private const int Version = 1;
    private const int MaximumFiles = 1_000_000;
    private const int MaximumItemsPerFile = 100_000;
    private readonly string directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VisualBoost", "Cache", "Analysis");

    public IReadOnlyDictionary<string, CachedSourceAnalysis> Load(string solutionPath)
    {
        var path = GetPath(solutionPath);
        if (path is null || !File.Exists(path))
        {
            return new Dictionary<string, CachedSourceAnalysis>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, false);
            if (reader.ReadString() != Magic || reader.ReadInt32() != Version)
            {
                return Empty();
            }

            var count = ReadCount(reader, MaximumFiles);
            var entries = new Dictionary<string, CachedSourceAnalysis>(count, StringComparer.OrdinalIgnoreCase);
            for (var fileIndex = 0; fileIndex < count; fileIndex++)
            {
                var file = reader.ReadString();
                var length = reader.ReadInt64();
                var ticks = reader.ReadInt64();
                var includeCount = ReadCount(reader, MaximumItemsPerFile);
                var includes = new List<SourceIncludeReference>(includeCount);
                for (var includeIndex = 0; includeIndex < includeCount; includeIndex++)
                {
                    includes.Add(new SourceIncludeReference(reader.ReadString(), reader.ReadBoolean(), reader.ReadInt32()));
                }

                var symbolCount = ReadCount(reader, MaximumItemsPerFile);
                var symbols = new List<SourceSymbolLocation>(symbolCount);
                for (var symbolIndex = 0; symbolIndex < symbolCount; symbolIndex++)
                {
                    var name = reader.ReadString();
                    var line = reader.ReadInt32();
                    var column = reader.ReadInt32();
                    var kind = (SourceSymbolKind)reader.ReadInt32();
                    symbols.Add(new SourceSymbolLocation(name, file, line, column, kind));
                }

                entries[file] = new CachedSourceAnalysis(
                    length, ticks, new SourceFileAnalysis(file, includes, symbols));
            }

            return entries;
        }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException ||
            exception is SecurityException || exception is EndOfStreamException ||
            exception is InvalidDataException)
        {
            return Empty();
        }
    }

    public void Save(string solutionPath, IEnumerable<KeyValuePair<string, CachedSourceAnalysis>> entries)
    {
        var path = GetPath(solutionPath);
        if (path is null)
        {
            return;
        }

        var values = entries.Take(MaximumFiles).ToArray();
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = File.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, false))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(values.Length);
                foreach (var pair in values)
                {
                    var entry = pair.Value;
                    writer.Write(pair.Key);
                    writer.Write(entry.Length);
                    writer.Write(entry.LastWriteUtcTicks);
                    writer.Write(entry.Analysis.Includes.Count);
                    foreach (var include in entry.Analysis.Includes)
                    {
                        writer.Write(include.Value);
                        writer.Write(include.IsSystem);
                        writer.Write(include.Line);
                    }

                    writer.Write(entry.Analysis.Symbols.Count);
                    foreach (var symbol in entry.Analysis.Symbols)
                    {
                        writer.Write(symbol.Name);
                        writer.Write(symbol.Line);
                        writer.Write(symbol.Column);
                        writer.Write((int)symbol.Kind);
                    }
                }
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
        {
            // 분석 캐시는 성능 최적화이며 저장 실패가 탐색 기능을 중단시키지 않습니다.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
            {
                // 고유 임시 파일은 다음 저장과 충돌하지 않으므로 정리 실패를 무시합니다.
            }
        }
    }

    private string? GetPath(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath) || string.IsNullOrWhiteSpace(directory)) return null;
        using var sha = SHA256.Create();
        var identity = Path.GetFullPath(solutionPath).ToUpperInvariant();
        var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", string.Empty);
        return Path.Combine(directory, hash + ".bin");
    }

    private static int ReadCount(BinaryReader reader, int maximum)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum) throw new InvalidDataException("캐시 항목 수가 유효하지 않습니다.");
        return count;
    }

    private static IReadOnlyDictionary<string, CachedSourceAnalysis> Empty() =>
        new Dictionary<string, CachedSourceAnalysis>(StringComparer.OrdinalIgnoreCase);
}
