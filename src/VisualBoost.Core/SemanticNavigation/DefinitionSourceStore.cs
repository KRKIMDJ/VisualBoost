using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// database 밖에서 근사 명령으로 연 소스(엔진 cpp, 명령이 없던 프로젝트 cpp)를 캐시 폴더에 기억합니다. 스레드 안전합니다.
/// </summary>
/// <remarks>
/// clangd는 시작할 때 database의 TU와 그 include만 저장된 색인에서 읽습니다. 덮어쓰기 명령으로 색인한 파일은 색인 파일이 남아도 다음
/// clangd(새 세션, 메모리 정리 재시작)가 읽지 않아, 엔진 함수 정의를 찾을 때마다 그 cpp를 다시 분석했습니다(파일 하나 약 10초). 기억한
/// 파일은 clangd를 시작할 때 근사 명령을 다시 줘서 저장된 색인을 읽게 합니다. clangd는 내용이 같으면 다시 색인하지 않습니다.
/// 최근에 쓴 순서로 <see cref="Capacity"/>개까지 둡니다.
/// </remarks>
public sealed class DefinitionSourceStore
{
    public const string FileName = "definition-sources.json";

    /// <summary>기억할 파일 수 상한입니다. 시작할 때마다 이만큼의 명령을 보내고 색인 파일을 읽습니다.</summary>
    public const int Capacity = 256;

    // 같은 캐시 폴더를 여러 탐색기(재시작 전후)가 함께 쓸 수 있어 폴더마다 잠급니다.
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string path;
    private readonly object gate;

    public DefinitionSourceStore(string directory)
    {
        path = Path.Combine(Path.GetFullPath(directory), FileName);
        gate = Locks.GetOrAdd(path, _ => new object());
    }

    /// <summary>기억한 파일입니다(최근 순). 지금 없는 파일은 뺍니다.</summary>
    public IReadOnlyList<string> Load()
    {
        lock (gate) return Read().Where(File.Exists).ToArray();
    }

    /// <summary>파일을 맨 앞에 기억합니다. 이미 맨 앞이면 쓰지 않습니다. 기록 파일을 쓰지 못하면 false입니다.</summary>
    public bool Record(string file)
    {
        var full = Path.GetFullPath(file);
        lock (gate)
        {
            var files = Read();
            if (files.Count > 0 && string.Equals(files[0], full, StringComparison.OrdinalIgnoreCase)) return true;
            files.RemoveAll(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
            files.Insert(0, full);
            if (files.Count > Capacity) files.RemoveRange(Capacity, files.Count - Capacity);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonValue.Object(("files", JsonValue.Array(files.Select(f => (JsonValue)f)))).ToJson());
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private List<string> Read()
    {
        try
        {
            if (!File.Exists(path)) return new List<string>();
            return JsonValue.Parse(File.ReadAllText(path))["files"].Items.Select(i => i.AsString()).OfType<string>().ToList();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is FormatException)
        {
            // 읽지 못한 기록은 버리고 새로 기억합니다. 잃는 것은 다음 시작의 미리 읽기뿐입니다.
            return new List<string>();
        }
    }
}
