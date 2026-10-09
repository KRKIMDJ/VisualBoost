using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 공유 PCH 없이 연 문서의 분석 오류를 풀려고 넣은 보충 헤더(<see cref="IncludeSupplements"/>)를 문서마다 캐시 폴더에 기억합니다. 스레드 안전합니다.
/// </summary>
/// <remarks>
/// 문서 보충은 첫 분석(대형 TU 4~10초)에서 오류를 본 뒤 헤더를 골라 다시 분석하므로, 세션마다 그 문서의 첫 탐색이 분석 한 번을 더 기다렸습니다.
/// 디스크 내용 그대로인 문서가 보충 헤더로 오류 없이 분석된 경우만 기억해, 다음 세션에서 그 문서를 열 때 처음부터 넣습니다. 그 문서에만 넣고
/// 모듈에는 배우지 않습니다(편집 중 코드로 고른 헤더가 다른 파일에 퍼지지 않게, 피드백 검토 65). 공유 PCH로 돌아간 문서는 기억을 지웁니다.
/// 최근에 쓴 순서로 <see cref="Capacity"/>개 문서까지 둡니다.
/// </remarks>
public sealed class DocumentSupplementStore
{
    public const string FileName = "document-supplements.json";

    /// <summary>기억할 문서 수 상한입니다. 기록 파일 크기와 처음 읽는 시간을 묶어 둡니다.</summary>
    public const int Capacity = 512;

    // 같은 캐시 폴더를 여러 탐색기(재시작 전후)가 함께 쓸 수 있어 폴더마다 잠급니다.
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string path;
    private readonly object gate;

    public DocumentSupplementStore(string directory)
    {
        path = Path.Combine(Path.GetFullPath(directory), FileName);
        gate = Locks.GetOrAdd(path, _ => new object());
    }

    /// <summary>문서별 보충 헤더입니다. 지금 없는 헤더는 빼고, 남은 헤더가 없거나 문서가 없으면 뺍니다.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Load()
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        List<(string Document, IReadOnlyList<string> Headers)>? entries;
        lock (gate) entries = Read();
        foreach (var (document, headers) in entries ?? new List<(string, IReadOnlyList<string>)>())
        {
            var present = headers.Where(File.Exists).ToArray();
            if (present.Length > 0 && File.Exists(document) && !result.ContainsKey(document)) result[document] = present;
        }

        return result;
    }

    /// <summary>
    /// 문서의 보충 헤더를 맨 앞에 기억합니다. 이미 맨 앞에 같은 헤더로 있으면 쓰지 않습니다. 기록 파일을 읽거나 쓰지 못하면 false입니다. 읽기가
    /// 일시적으로 실패했을 때 빈 목록 위에 쓰면 그동안의 기억을 모두 잃으므로 이번 기록을 건너뜁니다.
    /// </summary>
    public bool Record(string document, IReadOnlyList<string> headers)
    {
        var full = Path.GetFullPath(document);
        var normalized = headers.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (gate)
        {
            var entries = Read();
            if (entries is null) return false;
            if (entries.Count > 0 && string.Equals(entries[0].Document, full, StringComparison.OrdinalIgnoreCase) &&
                entries[0].Headers.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            entries.RemoveAll(e => string.Equals(e.Document, full, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, (full, normalized));
            if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);
            return Write(entries);
        }
    }

    /// <summary>문서의 기억을 지웁니다. 기억이 없으면 쓰지 않습니다. 기록 파일을 읽거나 쓰지 못하면 false입니다.</summary>
    public bool Forget(string document)
    {
        var full = Path.GetFullPath(document);
        lock (gate)
        {
            var entries = Read();
            if (entries is null) return false;
            return entries.RemoveAll(e => string.Equals(e.Document, full, StringComparison.OrdinalIgnoreCase)) == 0 || Write(entries);
        }
    }

    private bool Write(List<(string Document, IReadOnlyList<string> Headers)> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            var json = JsonValue.Object(("documents", JsonValue.Array(entries.Select(e =>
                JsonValue.Object(("path", e.Document), ("headers", JsonValue.Array(e.Headers.Select(h => (JsonValue)h))))))));
            File.WriteAllText(temporary, json.ToJson());
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            return true;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>기록입니다(최근 순). 기록이 없거나 깨졌으면 빈 목록, 읽지 못했으면(일시적 잠금) null입니다.</summary>
    private List<(string Document, IReadOnlyList<string> Headers)>? Read()
    {
        string text;
        try
        {
            if (!File.Exists(path)) return new List<(string, IReadOnlyList<string>)>();
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return JsonValue.Parse(text)["documents"].Items
                .Select(item => (Document: item["path"].AsString(), Headers: (IReadOnlyList<string>)item["headers"].Items.Select(h => h.AsString()).OfType<string>().ToArray()))
                .Where(e => e.Document is not null && e.Headers.Count > 0)
                .Select(e => (e.Document!, e.Headers))
                .ToList();
        }
        catch (FormatException)
        {
            // 깨진 기록은 버리고 새로 기억합니다. 잃는 것은 다음 세션의 미리 넣기뿐입니다.
            return new List<(string, IReadOnlyList<string>)>();
        }
    }
}
