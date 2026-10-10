using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using VisualBoost.Core.Analysis;
using VisualBoost.Services;

namespace VisualBoost.Analysis;

internal sealed class SourceAnalysisCache
{
    private const string Magic = "VisualBoost.SourceAnalysis";
    private const int Version = 6;
    private const int MaximumFiles = 1_000_000;
    private const int MaximumItemsPerFile = 100_000;
    private readonly string directory;
    internal bool NeedsUpgrade { get; private set; }

    internal SourceAnalysisCache(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisualBoost", "Cache", "Analysis");
    }

    public IReadOnlyDictionary<string, CachedSourceAnalysis> Load(string solutionPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NeedsUpgrade = false;
        var path = GetPath(solutionPath);
        if (path is null || !File.Exists(path))
        {
            return new Dictionary<string, CachedSourceAnalysis>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, false);
            if (reader.ReadString() != Magic) return Empty();
            var version = reader.ReadInt32();
            if (version < 3 || version > Version)
            {
                return Empty();
            }

            var count = ReadCount(reader, MaximumFiles);
            var entries = new Dictionary<string, CachedSourceAnalysis>(count, StringComparer.OrdinalIgnoreCase);
            // 읽는 동안만 쓰는 풀입니다. 같은 이름·소속·시그니처를 한 인스턴스로 모읍니다.
            var pool = new StringPool();
            // 버린 include 목록이 있었습니다. 같은 v6 형식이라도 다시 저장해, 다음 열기부터 읽고 버리는 비용을 없앱니다(2026-10-10 검토 88).
            var hadIncludes = false;
            for (var fileIndex = 0; fileIndex < count; fileIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = reader.ReadString();
                // 분석 버전을 적지 않은 형식(v3·v4)은 분석 버전 1 시절의 결과입니다.
                var revision = version >= 5 ? reader.ReadInt32() : 1;
                var length = reader.ReadInt64();
                var ticks = reader.ReadInt64();
                // include 목록은 더 보관하지 않습니다(SolutionSourceAnalyzer.Entry). 예전에 저장한 목록은 읽고 버리며 다음 저장에서 빠집니다.
                var includeCount = ReadCount(reader, MaximumItemsPerFile);
                hadIncludes |= includeCount > 0;
                for (var includeIndex = 0; includeIndex < includeCount; includeIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    reader.ReadString();
                    reader.ReadBoolean();
                    reader.ReadInt32();
                }

                var symbolCount = ReadCount(reader, MaximumItemsPerFile);
                var symbols = new List<SourceSymbolLocation>(symbolCount);
                for (var symbolIndex = 0; symbolIndex < symbolCount; symbolIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = pool.Intern(reader.ReadString());
                    var line = reader.ReadInt32();
                    var column = reader.ReadInt32();
                    var kind = (SourceSymbolKind)reader.ReadInt32();
                    var scope = pool.Intern(reader.ReadString());
                    symbols.Add(new SourceSymbolLocation(name, file, line, column, kind, scope, pool.Intern(reader.ReadString())));
                    // 이전 실험 버전의 선언·정의 분류 값은 호환성 때문에 읽기만 하고 폐기합니다.
                    if (version == 4 || version == 5) reader.ReadInt32();
                }

                // 구형 캐시의 상세 종류 이전 항목만 갱신하고, 이후에는 명시적 분석 버전으로 판정합니다.
                if (version <= 4 && symbols.Any(s => s.Kind == SourceSymbolKind.Type)) revision = 0;
                entries[file] = new CachedSourceAnalysis(
                    length, ticks, new SourceFileAnalysis(file, Array.Empty<SourceIncludeReference>(), symbols), revision);
            }

            cancellationToken.ThrowIfCancellationRequested();
            NeedsUpgrade = version != Version || hadIncludes;
            return entries;
        }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException ||
            exception is SecurityException || exception is EndOfStreamException ||
            exception is InvalidDataException || exception is FormatException)
        {
            return Empty();
        }
    }

    /// <summary>이 Solution의 저장된 분석을 지웁니다(인덱스 다시 만들기). 지우지 못했으면 false입니다.</summary>
    public bool Delete(string solutionPath) => GetPath(solutionPath) is not { } path || CacheFiles.Delete(path);

    public void Save(string solutionPath, IEnumerable<KeyValuePair<string, CachedSourceAnalysis>> entries, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(solutionPath);
        if (path is null)
        {
            return;
        }

        CacheFiles.DeleteTemporaries(path);
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
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = pair.Value;
                    writer.Write(pair.Key);
                    writer.Write(entry.Revision);
                    writer.Write(entry.Length);
                    writer.Write(entry.LastWriteUtcTicks);
                    // include 목록은 보관하지 않으므로(SolutionSourceAnalyzer.Entry) 형식의 자리만 채웁니다.
                    writer.Write(0);

                    // 읽기는 파일당 항목이 MaximumItemsPerFile을 넘으면 손상으로 보고 캐시 전체를 버리므로 저장도 같은 상한에서 자릅니다. 예전에는
                    // 아주 큰 생성 헤더 하나가 다음 열기마다 Solution 전체를 다시 분석하게 했습니다. 넘는 이름은 다음 세션부터 빠집니다.
                    var symbols = entry.Analysis.Symbols;
                    var symbolCount = Math.Min(symbols.Count, MaximumItemsPerFile);
                    writer.Write(symbolCount);
                    foreach (var symbol in symbols.Take(symbolCount))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.Write(symbol.Name);
                        writer.Write(symbol.Line);
                        writer.Write(symbol.Column);
                        writer.Write((int)symbol.Kind);
                        writer.Write(symbol.Scope);
                        writer.Write(symbol.Signature);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
            NeedsUpgrade = false;
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
