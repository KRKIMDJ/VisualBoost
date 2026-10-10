using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

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

/// <summary>색인 파일이 기록한 TU 하나의 색인 결과입니다(<see cref="ClangdIndexShards.TranslationUnitOf"/>).</summary>
public readonly struct IndexedTranslationUnit
{
    public IndexedTranslationUnit(string path, bool hadErrors)
    {
        Path = path;
        HadErrors = hadErrors;
    }

    /// <summary>clangd가 쓰는 실제 경로입니다.</summary>
    public string Path { get; }

    /// <summary>색인할 때 컴파일할 수 없는 오류가 있었는지입니다. 참이면 그 TU의 색인이 덜 되었을 수 있습니다.</summary>
    public bool HadErrors { get; }
}

/// <summary>색인 파일에서 읽은 파일 하나의 참조와 분석 오류 표시입니다(<see cref="ClangdIndexShards.CurrentFile"/>).</summary>
public sealed class IndexedFile
{
    public IndexedFile(IReadOnlyList<IndexedReference> references, bool? hadErrors, IReadOnlyList<string>? includes = null, DateTime indexedAt = default)
    {
        References = references;
        HadErrors = hadErrors;
        Includes = includes ?? Array.Empty<string>();
        IndexedAt = indexedAt;
    }

    /// <summary>그 파일 안에 있는 참조입니다.</summary>
    public IReadOnlyList<IndexedReference> References { get; }

    /// <summary>그 파일을 색인할 때 컴파일할 수 없는 오류가 있었는지입니다. 파일 목록에서 그 파일을 찾지 못했으면 null입니다.</summary>
    public bool? HadErrors { get; }

    /// <summary>그 파일이 직접 include한 파일의 실제 경로입니다(색인 파일 파일 목록 기록, 없으면 빈 목록).</summary>
    public IReadOnlyList<string> Includes { get; }

    /// <summary>
    /// 읽은 색인 파일 중 가장 먼저 쓴 시각(UTC)입니다. 그 뒤에 바뀐 include 헤더는 이 기록의 심볼 해석에 반영되지 않았습니다.
    /// </summary>
    public DateTime IndexedAt { get; }
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
    private readonly Dictionary<string, ShardContent> cache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>>? shardsByName;
    private DateTime listedUtc;
    private int versionMismatch;

    public ClangdIndexShards(string indexDirectory)
    {
        directory = indexDirectory;
    }

    /// <summary>
    /// 형식 버전이 <see cref="FormatVersion"/>과 다른 색인 파일을 읽었습니다. 그동안 색인 파일로 정의를 바로 찾거나 참조를 거르지 못하고
    /// clangd에 묻습니다. clangd가 바뀌었을 수 있어 호출자가 진단 기록으로 알립니다(2026-10-10 검토 85).
    /// </summary>
    public bool FormatVersionMismatch => Volatile.Read(ref versionMismatch) != 0;

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
            var references = Read(shard, written)?.References;
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

    /// <summary>
    /// 그 파일의 색인 파일에 있는 참조(없으면 빈 목록)와, 그 파일을 색인할 때 분석 오류가 있었는지입니다. 원본보다 늦게 쓴 색인 파일을
    /// 형식대로 읽지 못했으면 null이고, 파일 목록에서 그 파일의 분석 오류 표시를 찾지 못했으면 <c>HadErrors</c>가 null입니다.
    /// </summary>
    /// <param name="path">clangd가 쓰는 실제 경로입니다.</param>
    public IndexedFile? CurrentFile(string path)
    {
        var sourceWritten = SafeWriteTime(path);
        if (sourceWritten == DateTime.MinValue) return null;
        var full = Normalize(path);
        List<IndexedReference>? found = null;
        bool? hadErrors = null;
        var includes = new List<string>();
        var indexedAt = DateTime.MaxValue;
        foreach (var shard in ShardsNamed(Path.GetFileName(path)))
        {
            var written = SafeWriteTime(shard);
            if (written < sourceWritten) continue;
            if (Read(shard, written) is not { References: { } references } content) continue;
            found ??= new List<IndexedReference>();
            if (written < indexedAt) indexedAt = written;
            foreach (var reference in references)
            {
                if (string.Equals(Normalize(reference.Path), full, StringComparison.OrdinalIgnoreCase)) found.Add(reference);
            }

            foreach (var node in content.Nodes ?? Array.Empty<SourceNode>())
            {
                if (!string.Equals(Normalize(node.Path), full, StringComparison.OrdinalIgnoreCase)) continue;
                hadErrors = (hadErrors ?? false) || (node.Flags & HadErrorsFlag) != 0;
                includes.AddRange(node.Includes);
            }
        }

        return found is null ? null : new IndexedFile(found, hadErrors, includes, indexedAt);
    }

    /// <summary>
    /// 그 파일 이름의 색인 파일 중 원본보다 늦게 쓴 것이 있는지 봅니다. 내용은 읽지 않으므로 다른 폴더의 같은 이름 파일 색인으로 참이 될 수
    /// 있습니다. clangd에 명령을 다시 줘도 다시 색인하지 않을 파일을 고를 때 씁니다.
    /// </summary>
    public bool HasCurrentShard(string path)
    {
        var sourceWritten = SafeWriteTime(path);
        return sourceWritten != DateTime.MinValue && ShardsNamed(Path.GetFileName(path)).Any(s => SafeWriteTime(s) >= sourceWritten);
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

    private ShardContent? Read(string shard, DateTime written)
    {
        lock (gate)
        {
            if (cache.TryGetValue(shard, out var cached) && cached.Written == written) return cached;
        }

        ShardContent content;
        try
        {
            var data = ReadShared(shard);
            IReadOnlyList<IndexedReference>? references;
            try
            {
                references = Parse(data);
                if (references is null) Volatile.Write(ref versionMismatch, 1);
            }
            catch (InvalidDataException)
            {
                // 깨진 내용은 clangd가 다시 쓸 때(수정 시각이 바뀜)까지 같은 결과이므로 담아 둡니다.
                references = null;
            }

            IReadOnlyList<SourceNode>? nodes;
            try
            {
                nodes = SourceNodes(data);
            }
            catch (InvalidDataException)
            {
                nodes = null;
            }

            content = new ShardContent(written, references, nodes);
        }
        catch (InvalidDataException)
        {
            // 너무 크거나 읽는 중에 줄어든 파일입니다.
            content = new ShardContent(written, null, null);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // clangd가 쓰는 중인 공유 위반처럼 일시적일 수 있어 담지 않고 다음 요청에서 다시 읽습니다(2026-10-09 검토 41).
            return null;
        }

        lock (gate)
        {
            if (cache.Count >= MaxCachedShards) cache.Clear();
            cache[shard] = content;
        }

        return content;
    }

    /// <summary>clangd가 같은 파일을 다시 쓰거나 지우는 중에도 열 수 있게 공유 모드를 넓혀 읽습니다.</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxShardBytes) throw new InvalidDataException("clangd 색인 파일이 너무 큽니다.");
        var data = new byte[stream.Length];
        var read = 0;
        while (read < data.Length)
        {
            var n = stream.Read(data, read, data.Length - read);
            if (n == 0) throw new InvalidDataException("clangd 색인 파일이 읽는 중에 줄었습니다.");
            read += n;
        }

        return data;
    }

    /// <summary>색인 파일 하나와 풀어 낸 문자열 표의 크기 상한입니다. 깨진 크기 값으로 큰 메모리를 잡지 않게 합니다.</summary>
    private const int MaxShardBytes = 256 * 1024 * 1024;

    /// <summary>
    /// 색인 파일 내용에서 참조를 읽습니다. 형식 버전이 다르면 null입니다. 깨진 내용이면 어느 경우든 <see cref="InvalidDataException"/>입니다
    /// (길이·번호는 범위를 넘치지 않게 검사합니다, 2026-10-09 검토 41).
    /// </summary>
    public static IReadOnlyList<IndexedReference>? Parse(byte[] data)
    {
        var chunks = Chunks(data);
        if (!chunks.TryGetValue("meta", out var meta) || meta.Length < 4 || BitConverter.ToUInt32(data, meta.Offset) != FormatVersion) return null;
        if (!chunks.TryGetValue("stri", out var stri)) throw new InvalidDataException("문자열 표가 없습니다.");
        var strings = Strings(data, stri.Offset, stri.Length);
        var references = new List<IndexedReference>();
        if (!chunks.TryGetValue("refs", out var refs)) return references;
        var paths = new Dictionary<uint, string>();
        var reader = new Reader(data, refs.Offset, refs.Offset + refs.Length);
        while (!reader.AtEnd)
        {
            var id = reader.Id();
            var count = reader.Var();
            for (var i = 0; i < count; i++)
            {
                var kind = reader.Byte();
                var file = reader.Var();
                var line = reader.Int();
                var character = reader.Int();
                var endLine = reader.Int();
                var endCharacter = reader.Int();
                reader.Id();
                if (!paths.TryGetValue(file, out var path))
                {
                    if (file >= (uint)strings.Count) throw new InvalidDataException("문자열 번호가 범위를 벗어났습니다.");
                    path = DocumentUri.ToPath(strings[(int)file]) ?? string.Empty;
                    paths[file] = path;
                }

                references.Add(new IndexedReference(id, kind, path, line, character, endLine, endCharacter));
            }
        }

        return references;
    }

    // srcs 목록의 파일별 플래그입니다(clangd IncludeGraphNode::SourceFlag).
    private const byte IsTranslationUnitFlag = 1;
    private const byte HadErrorsFlag = 2;

    /// <summary>
    /// 색인 파일의 파일 목록(<c>srcs</c>)에서 이 색인 파일의 소스가 TU로 색인된 기록을 읽습니다. 그 소스가 TU가 아니면(헤더, unity 묶음에
    /// 포함된 구성원 cpp) null입니다. clangd는 TU에 컴파일할 수 없는 오류가 있으면 그 색인에서 나온 파일마다 오류 플래그를 남깁니다.
    /// </summary>
    /// <remarks>
    /// 공유 PCH·헤더 보충 자동 판단이 clangd 로그 문구에 기대지 않게 하는 신호입니다. 로그 문구는 버전 표시 없이 바뀔 수 있지만 이 목록은
    /// 색인 파일에 저장되는 자료라 형식이 바뀌면 구조가 어긋납니다. 그래서 형식 버전 번호(<see cref="FormatVersion"/>)는 보지 않고, 목록을
    /// 끝까지 구조대로(플래그 1바이트, 파일 URI 문자열 번호, digest 8바이트, 직접 include URI 번호들) 읽고 URI가 모두 <c>file:</c>일 때만
    /// 씁니다. 맞지 않으면 <see cref="InvalidDataException"/>입니다. 구조는 clangd 공개 소스(index/Serialization.cpp)를 따릅니다.
    /// </remarks>
    /// <param name="sourceFileName">색인 파일 이름 앞부분(<c>이름.해시.idx</c>의 이름)입니다.</param>
    public static IndexedTranslationUnit? TranslationUnitOf(byte[] data, string sourceFileName)
    {
        IndexedTranslationUnit? found = null;
        foreach (var node in SourceNodes(data))
        {
            if ((node.Flags & IsTranslationUnitFlag) == 0) continue;
            if (string.Equals(Path.GetFileName(node.Path), sourceFileName, StringComparison.OrdinalIgnoreCase))
            {
                found = new IndexedTranslationUnit(node.Path, (node.Flags & HadErrorsFlag) != 0);
            }
        }

        return found;
    }

    /// <summary>
    /// 색인 파일의 파일 목록(<c>srcs</c>) 전체입니다. 형식 버전 번호는 보지 않고 구조대로 끝까지 읽으며(<see cref="TranslationUnitOf"/> 참고),
    /// 맞지 않으면 <see cref="InvalidDataException"/>입니다.
    /// </summary>
    private static IReadOnlyList<SourceNode> SourceNodes(byte[] data)
    {
        var chunks = Chunks(data);
        if (!chunks.TryGetValue("stri", out var stri)) throw new InvalidDataException("문자열 표가 없습니다.");
        if (!chunks.TryGetValue("srcs", out var srcs)) throw new InvalidDataException("파일 목록이 없습니다.");
        var strings = Strings(data, stri.Offset, stri.Length);
        string Uri(uint index)
        {
            if (index >= (uint)strings.Count) throw new InvalidDataException("문자열 번호가 범위를 벗어났습니다.");
            var uri = strings[(int)index];
            if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("파일 목록의 URI가 파일 URI가 아닙니다.");
            return uri;
        }

        var nodes = new List<SourceNode>();
        var reader = new Reader(data, srcs.Offset, srcs.Offset + srcs.Length);
        while (!reader.AtEnd)
        {
            var flags = reader.Byte();
            var uri = Uri(reader.Var());
            reader.Skip(8);
            var count = reader.Var();
            var includes = new List<string>();
            for (var i = 0; i < count; i++)
            {
                if (DocumentUri.ToPath(Uri(reader.Var())) is { } include) includes.Add(include);
            }

            if (DocumentUri.ToPath(uri) is { } path) nodes.Add(new SourceNode(path, flags, includes));
        }

        return nodes;
    }

    /// <summary>
    /// 색인 파일 이름(<c>이름.해시16자리.idx</c>)에서 소스 파일 이름을 꺼냅니다. 형식이 다르면 null입니다.
    /// </summary>
    public static string? SourceNameOfShard(string shardFileName)
    {
        if (!shardFileName.EndsWith(".idx", StringComparison.OrdinalIgnoreCase)) return null;
        var name = shardFileName.Substring(0, shardFileName.Length - 4);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name.Substring(0, dot) : null;
    }

    /// <summary>색인 파일을 다른 프로세스가 쓰는 중에도 읽습니다. 너무 크거나 읽는 중에 줄면 <see cref="InvalidDataException"/>입니다.</summary>
    public static byte[] ReadShard(string path) => ReadShared(path);

    /// <summary>RIFF 컨테이너(<c>CdIx</c>)의 청크 표입니다. 형식이 아니거나 잘렸으면 <see cref="InvalidDataException"/>입니다.</summary>
    private static Dictionary<string, (int Offset, int Length)> Chunks(byte[] data)
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
            var declared = BitConverter.ToUInt32(data, position + 4);
            if (declared > (uint)(data.Length - position - 8)) throw new InvalidDataException("clangd 색인 파일이 잘렸습니다.");
            var length = (int)declared;
            chunks[id] = (position + 8, length);
            position += 8 + length + (length & 1);
        }

        return chunks;
    }

    /// <summary>문자열 표: 원래 크기(0이면 압축 안 함) 뒤에 zlib으로 압축한 NUL 구분 문자열들입니다.</summary>
    private static List<string> Strings(byte[] data, int offset, int length)
    {
        if (length < 4) throw new InvalidDataException("문자열 표가 짧습니다.");
        var size = BitConverter.ToUInt32(data, offset);
        if (size > MaxShardBytes) throw new InvalidDataException("문자열 표 크기가 너무 큽니다.");
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

    private readonly struct SourceNode
    {
        public SourceNode(string path, byte flags, IReadOnlyList<string> includes)
        {
            Path = path;
            Flags = flags;
            Includes = includes;
        }

        public string Path { get; }

        public byte Flags { get; }

        public IReadOnlyList<string> Includes { get; }
    }

    /// <summary>색인 파일 하나에서 읽은 참조(형식 버전이 다르면 null)와 파일 목록(구조가 맞지 않으면 null)입니다.</summary>
    private sealed class ShardContent
    {
        public ShardContent(DateTime written, IReadOnlyList<IndexedReference>? references, IReadOnlyList<SourceNode>? nodes)
        {
            Written = written;
            References = references;
            Nodes = nodes;
        }

        public DateTime Written { get; }

        public IReadOnlyList<IndexedReference>? References { get; }

        public IReadOnlyList<SourceNode>? Nodes { get; }
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
            if (position >= end) throw new InvalidDataException("색인 파일의 표가 잘렸습니다.");
            return data[position++];
        }

        public void Skip(int count)
        {
            if (position + count > end) throw new InvalidDataException("색인 파일의 표가 잘렸습니다.");
            position += count;
        }

        public string Id()
        {
            if (position + 8 > end) throw new InvalidDataException("색인 파일의 표가 잘렸습니다.");
            var text = new StringBuilder(16);
            for (var i = 0; i < 8; i++) text.Append(data[position + i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            position += 8;
            return text.ToString();
        }

        /// <summary><see cref="Var"/>를 줄·열 같은 음이 아닌 <see cref="int"/>로 읽습니다.</summary>
        public int Int()
        {
            var value = Var();
            if (value > int.MaxValue) throw new InvalidDataException("위치 값이 범위를 벗어났습니다.");
            return (int)value;
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
