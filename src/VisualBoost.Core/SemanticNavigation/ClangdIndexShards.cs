using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>clangd 색인 파일에 기록된 참조 하나입니다. 좌표는 0기반 줄과 LSP 문자 위치입니다.</summary>
public readonly struct IndexedReference
{
    public IndexedReference(string symbolId, byte kind, string path, int line, int character, int endLine, int endCharacter)
    {
        SymbolId = symbolId;
        Kind = kind;
        Path = path;
        Line = line;
        Character = character;
        EndLine = endLine;
        EndCharacter = endCharacter;
    }

    /// <summary>심볼 ID(8바이트, 대문자 16진수 16자리)입니다. clangd <c>symbolInfo</c>의 <c>id</c>와 같은 표기입니다.</summary>
    public string SymbolId { get; }

    /// <summary>clangd RefKind 비트(선언 1, 정의 2, 참조 4, 소스에 쓰인 이름 8, 호출 16)입니다.</summary>
    public byte Kind { get; }

    public string Path { get; }

    public int Line { get; }

    public int Character { get; }

    public int EndLine { get; }

    public int EndCharacter { get; }

    public bool Spelled => (Kind & 8) != 0;
}

/// <summary>
/// clangd background index가 파일마다 쓰는 색인 파일(<c>.cache/clangd/index/이름.해시.idx</c>)에서 참조만 읽습니다. 스레드 안전합니다.
/// </summary>
/// <remarks>
/// 참조 탐색 결과에서 찾는 심볼이 아닌 위치(가상 함수의 기반 함수 참조·재정의 함수 선언)를 가려내는 데 씁니다. LSP에는 위치별 심볼을 알려
/// 주는 요청이 열린 문서에만 있어, 열지 않은 파일은 clangd가 쓴 색인 파일을 읽습니다. 형식은 clangd 공개 소스(index/Serialization.cpp)의
/// RIFF 컨테이너(<c>CdIx</c>)이며 형식 버전이 <see cref="FormatVersion"/>과 다르면 읽지 않습니다(null). 파일마다 그 파일에 있는 참조만
/// 담습니다. 이 파일을 쓴 뒤 원본이 바뀌었으면 위치가 어긋날 수 있어 읽지 않습니다.
/// </remarks>
public sealed class ClangdIndexShards
{
    /// <summary>읽을 수 있는 색인 형식 버전입니다(clangd 22.1 확인).</summary>
    public const uint FormatVersion = 20;

    private const int MaxCachedShards = 256;
    private readonly object gate = new();
    private readonly string directory;
    private readonly Dictionary<string, (DateTime Written, IReadOnlyList<IndexedReference>? References)> cache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>>? shardsByName;
    private DateTime listedUtc;

    public ClangdIndexShards(string indexDirectory)
    {
        directory = indexDirectory;
    }

    /// <summary>
    /// 그 파일의 색인 파일에 있는 참조입니다. 색인 파일이 없거나, 원본보다 오래되었거나, 형식을 읽지 못하면 null입니다.
    /// </summary>
    /// <param name="path">clangd가 쓰는 실제 경로입니다.</param>
    public IReadOnlyList<IndexedReference>? ReferencesIn(string path)
    {
        var sourceWritten = SafeWriteTime(path);
        if (sourceWritten == DateTime.MinValue) return null;
        var full = Normalize(path);
        List<IndexedReference>? found = null;
        foreach (var shard in ShardsNamed(Path.GetFileName(path)))
        {
            var written = SafeWriteTime(shard);
            if (written < sourceWritten) continue;
            var references = Read(shard, written);
            if (references is null) continue;
            foreach (var reference in references)
            {
                if (!string.Equals(Normalize(reference.Path), full, StringComparison.OrdinalIgnoreCase)) continue;
                found ??= new List<IndexedReference>();
                found.Add(reference);
            }
        }

        return found;
    }

    /// <summary>같은 이름 파일의 색인 파일들입니다. 목록에 없으면 새로 생긴 색인 파일일 수 있어 목록을 다시 만듭니다(5초에 한 번까지).</summary>
    private IReadOnlyList<string> ShardsNamed(string fileName)
    {
        lock (gate)
        {
            if (shardsByName is null || !shardsByName.ContainsKey(fileName) && DateTime.UtcNow - listedUtc > TimeSpan.FromSeconds(5))
            {
                shardsByName = List();
                listedUtc = DateTime.UtcNow;
            }

            return shardsByName.TryGetValue(fileName, out var shards) ? shards.ToArray() : Array.Empty<string>();
        }
    }

    private Dictionary<string, List<string>> List()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(directory)) return map;
            foreach (var shard in Directory.EnumerateFiles(directory, "*.idx"))
            {
                // 이름.해시16자리.idx
                var name = Path.GetFileNameWithoutExtension(shard);
                var dot = name.LastIndexOf('.');
                if (dot <= 0) continue;
                var source = name.Substring(0, dot);
                if (!map.TryGetValue(source, out var list)) map[source] = list = new List<string>();
                list.Add(shard);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
        }

        return map;
    }

    private IReadOnlyList<IndexedReference>? Read(string shard, DateTime written)
    {
        lock (gate)
        {
            if (cache.TryGetValue(shard, out var cached) && cached.Written == written) return cached.References;
        }

        IReadOnlyList<IndexedReference>? references;
        try
        {
            references = Parse(File.ReadAllBytes(shard));
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is InvalidDataException)
        {
            references = null;
        }

        lock (gate)
        {
            if (cache.Count >= MaxCachedShards) cache.Clear();
            cache[shard] = (written, references);
        }

        return references;
    }

    /// <summary>색인 파일 내용에서 참조를 읽습니다. 형식 버전이 다르면 null입니다. 깨진 내용이면 <see cref="InvalidDataException"/>입니다.</summary>
    public static IReadOnlyList<IndexedReference>? Parse(byte[] data)
    {
        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "RIFF" || Encoding.ASCII.GetString(data, 8, 4) != "CdIx")
        {
            throw new InvalidDataException("clangd 색인 파일 형식이 아닙니다.");
        }

        var chunks = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
        var position = 12;
        while (position + 8 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, position, 4);
            var length = checked((int)BitConverter.ToUInt32(data, position + 4));
            if (position + 8 + length > data.Length) throw new InvalidDataException("clangd 색인 파일이 잘렸습니다.");
            chunks[id] = (position + 8, length);
            position += 8 + length + (length & 1);
        }

        if (!chunks.TryGetValue("meta", out var meta) || meta.Length < 4 || BitConverter.ToUInt32(data, meta.Offset) != FormatVersion) return null;
        if (!chunks.TryGetValue("stri", out var stri)) throw new InvalidDataException("문자열 표가 없습니다.");
        var strings = Strings(data, stri.Offset, stri.Length);
        var references = new List<IndexedReference>();
        if (!chunks.TryGetValue("refs", out var refs)) return references;
        var paths = new Dictionary<int, string>();
        var reader = new Reader(data, refs.Offset, refs.Offset + refs.Length);
        while (!reader.AtEnd)
        {
            var id = reader.Id();
            var count = reader.Var();
            for (var i = 0; i < count; i++)
            {
                var kind = reader.Byte();
                var file = (int)reader.Var();
                var line = (int)reader.Var();
                var character = (int)reader.Var();
                var endLine = (int)reader.Var();
                var endCharacter = (int)reader.Var();
                reader.Id();
                if (!paths.TryGetValue(file, out var path))
                {
                    if (file >= strings.Count) throw new InvalidDataException("문자열 번호가 범위를 벗어났습니다.");
                    path = DocumentUri.ToPath(strings[file]) ?? string.Empty;
                    paths[file] = path;
                }

                references.Add(new IndexedReference(id, kind, path, line, character, endLine, endCharacter));
            }
        }

        return references;
    }

    /// <summary>문자열 표: 원래 크기(0이면 압축 안 함) 뒤에 zlib으로 압축한 NUL 구분 문자열들입니다.</summary>
    private static List<string> Strings(byte[] data, int offset, int length)
    {
        if (length < 4) throw new InvalidDataException("문자열 표가 짧습니다.");
        var size = BitConverter.ToUInt32(data, offset);
        byte[] raw;
        if (size == 0)
        {
            raw = new byte[length - 4];
            Buffer.BlockCopy(data, offset + 4, raw, 0, raw.Length);
        }
        else
        {
            // zlib 머리(2바이트)를 건너뛰고 deflate로 풉니다. 끝의 adler32는 읽지 않습니다.
            if (length < 6) throw new InvalidDataException("압축된 문자열 표가 짧습니다.");
            raw = new byte[size];
            using var input = new MemoryStream(data, offset + 6, length - 6);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            var read = 0;
            while (read < raw.Length)
            {
                var n = inflate.Read(raw, read, raw.Length - read);
                if (n == 0) throw new InvalidDataException("문자열 표 압축을 풀지 못했습니다.");
                read += n;
            }
        }

        var strings = new List<string>();
        var start = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != 0) continue;
            strings.Add(Encoding.UTF8.GetString(raw, start, i - start));
            start = i + 1;
        }

        return strings;
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            return path.Replace('\\', '/');
        }
    }

    private static DateTime SafeWriteTime(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private sealed class Reader
    {
        private readonly byte[] data;
        private readonly int end;
        private int position;

        public Reader(byte[] data, int start, int end)
        {
            this.data = data;
            position = start;
            this.end = end;
        }

        public bool AtEnd => position >= end;

        public byte Byte()
        {
            if (position >= end) throw new InvalidDataException("참조 표가 잘렸습니다.");
            return data[position++];
        }

        public string Id()
        {
            if (position + 8 > end) throw new InvalidDataException("참조 표가 잘렸습니다.");
            var text = new StringBuilder(16);
            for (var i = 0; i < 8; i++) text.Append(data[position + i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            position += 8;
            return text.ToString();
        }

        /// <summary>7비트씩 낮은 자리부터, 높은 비트가 이어짐 표시인 가변 길이 정수입니다.</summary>
        public uint Var()
        {
            uint value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = Byte();
                value |= (uint)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return value;
            }

            throw new InvalidDataException("가변 길이 정수가 너무 깁니다.");
        }
    }
}
