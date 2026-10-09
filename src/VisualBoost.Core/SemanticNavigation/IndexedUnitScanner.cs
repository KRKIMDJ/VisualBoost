using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// clangd 색인 폴더에서 정한 시각 뒤에 새로 쓴 소스 파일 색인 파일을 찾아, TU의 색인 완료와 분석 오류를 알아냅니다. 한 번에 한 호출자만 부릅니다.
/// </summary>
/// <remarks>
/// clangd 로그 문구(<c>Indexed …</c>, <c>Failed to compile …</c>)는 버전 표시 없이 바뀔 수 있어, clangd가 TU를 색인한 뒤 쓰는 그 소스의
/// 색인 파일을 신호로 씁니다(<see cref="ClangdIndexShards.TranslationUnitOf"/>). 색인 파일은 소스마다 하나(<c>이름.해시.idx</c>)이고 clangd는
/// 바뀐 TU만 다시 색인해 다시 씁니다. 소스 확장자의 색인 파일만 읽어 헤더 색인 파일(수천 개)은 열지 않습니다. 폴더 열거는 실제 Unreal 프로젝트
/// 캐시(색인 파일 6,267개)에서 1회 약 6 ms였습니다(2026-10-09).
/// </remarks>
public sealed class IndexedUnitScanner
{
    private readonly string directory;
    private readonly DateTime sinceUtc;
    private readonly Dictionary<string, DateTime> seen = new(StringComparer.OrdinalIgnoreCase);
    private int unreadable;
    private int read;

    /// <param name="sinceUtc">이 시각 뒤에 쓴 색인 파일만 봅니다. 이전 clangd가 남긴 색인 파일을 이번 색인으로 세지 않게 합니다.</param>
    public IndexedUnitScanner(string indexDirectory, DateTime sinceUtc)
    {
        directory = indexDirectory;
        this.sinceUtc = sinceUtc;
    }

    /// <summary>새로 쓴 소스 색인 파일 중 구조를 읽지 못한 것이 있었는지입니다. 참이면 clangd 색인 형식이 바뀌었을 수 있습니다.</summary>
    public bool FormatUnreadable => Volatile.Read(ref unreadable) != 0;

    /// <summary>새로 쓴 소스 색인 파일을 하나라도 구조대로 읽었는지입니다.</summary>
    public bool AnyRead => Volatile.Read(ref read) != 0;

    /// <summary>지난 훑기 뒤 새로 쓴 소스 색인 파일 중 그 소스가 TU로 색인된 기록입니다.</summary>
    public IReadOnlyList<IndexedTranslationUnit> Scan()
    {
        var found = new List<IndexedTranslationUnit>();
        try
        {
            if (!Directory.Exists(directory)) return found;
            // 소스 확장자(.cpp·.cc·.cxx·.c) 색인 파일만 열거합니다. 이름 형식은 아래에서 다시 확인합니다.
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.c*.idx"))
            {
                var source = ClangdIndexShards.SourceNameOfShard(file.Name);
                if (source is null || !DefinitionCandidates.IsSource(source)) continue;
                var written = file.LastWriteTimeUtc;
                if (written <= sinceUtc || seen.TryGetValue(file.FullName, out var known) && known == written) continue;
                if (Read(file.FullName, source, written) is { } unit) found.Add(unit);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // 폴더를 지우거나 옮기는 중입니다. 다음 훑기에서 다시 봅니다.
        }

        return found;
    }

    private IndexedTranslationUnit? Read(string shard, string source, DateTime written)
    {
        try
        {
            var unit = ClangdIndexShards.TranslationUnitOf(ClangdIndexShards.ReadShard(shard), source);
            seen[shard] = written;
            Volatile.Write(ref read, 1);
            return unit;
        }
        catch (InvalidDataException)
        {
            // clangd는 임시 파일에 쓴 뒤 이름을 바꾸므로 보통 완성된 파일만 보입니다. 읽는 사이 다시 썼으면 다음 훑기에서 다시 읽고,
            // 그대로인데 구조가 맞지 않을 때만 형식을 읽지 못한 것으로 봅니다.
            if (WriteTime(shard) == written)
            {
                seen[shard] = written;
                Volatile.Write(ref unreadable, 1);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // clangd가 바꿔 쓰는 중인 공유 위반처럼 일시적일 수 있어 다음 훑기에서 다시 읽습니다.
        }

        return null;
    }

    private static DateTime WriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
