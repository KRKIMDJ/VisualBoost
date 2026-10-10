using System;
using System.Collections.Generic;
using System.Linq;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>편집기 문서 내용과 그 순서를 나타내는 리비전입니다.</summary>
/// <remarks>
/// 동기화는 여러 스레드에서 들어올 수 있어 늦게 처리된 오래된 내용이 최신 내용을 덮을 수 있습니다.
/// 리비전이 작은 내용은 버립니다. 편집기와 무관한 디스크 내용은 0이며 같은 리비전끼리는 내용만 비교합니다.
/// 내용은 필요할 때만 만들도록 지연 계산합니다(큰 문서의 불필요한 복사 방지).
/// </remarks>
public sealed class DocumentText
{
    private readonly Lazy<string> text;

    public DocumentText(string path, string text, long revision = 0)
        : this(path, () => text, revision)
    {
    }

    public DocumentText(string path, Func<string> text, long revision)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        this.text = new Lazy<string>(text ?? throw new ArgumentNullException(nameof(text)));
        Revision = revision;
    }

    public string Path { get; }

    public string Text => text.Value;

    public long Revision { get; }
}

/// <summary>
/// clangd에 열어 둔 문서와 보낸 내용을 추적합니다.
/// </summary>
/// <remarks>
/// 대형 TU는 문서마다 preamble·AST를 만들므로 편집기에 열린 문서를 모두 clangd에 열지 않고,
/// 최근에 조회한 문서만 <see cref="Capacity"/>개까지 유지합니다. 요청이 진행 중인 문서는 임대(lease)로
/// 표시해 다른 흐름이 닫지 못하게 합니다. 사용자가 보지 않는 일회성 문서(<see cref="AcquireTransient"/>)는 정원에 세지 않습니다.
/// 알림 순서가 서버 상태와 어긋나지 않도록 콜백은 잠금 안에서 호출하며, 콜백은 파이프 쓰기처럼 짧게 끝나야 합니다.
/// </remarks>
public sealed class ClangdDocumentSet
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, string, int> open;
    private readonly Action<string, string, int> change;
    private readonly Action<string> close;
    private long clock;

    public ClangdDocumentSet(int capacity, Action<string, string, int> open, Action<string, string, int> change, Action<string> close)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        this.open = open ?? throw new ArgumentNullException(nameof(open));
        this.change = change ?? throw new ArgumentNullException(nameof(change));
        this.close = close ?? throw new ArgumentNullException(nameof(close));
    }

    public int Capacity { get; }

    public IReadOnlyList<string> OpenPaths
    {
        get
        {
            lock (gate) return entries.Keys.ToArray();
        }
    }

    /// <summary>정원에 빈자리가 있습니다(<see cref="TryOpenSpare"/>가 열 수 있는 자리).</summary>
    public bool HasSpareSlot
    {
        get
        {
            lock (gate) return entries.Values.Count(e => !e.Transient) < Capacity;
        }
    }

    public bool Contains(string path)
    {
        lock (gate) return entries.ContainsKey(path);
    }

    /// <summary>
    /// 문서를 열거나 더 새 내용이면 갱신하고 임대합니다. 반환값은 서버에 보낸 최신 버전입니다.
    /// 호출자는 요청이 끝나면 <see cref="Release"/>를 호출해야 합니다.
    /// </summary>
    public int Acquire(DocumentText document)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(document.Path, out var entry))
            {
                var text = document.Text;
                open(document.Path, text, 1);
                entry = new Entry(text, 1, document.Revision);
                entries[document.Path] = entry;
            }
            else
            {
                Send(document, entry);
            }

            // 다시 쓰는 문서이므로 임대 중에 받아 둔 닫기 요청은 거두고, 일회성으로 열었더라도 정원에 넣어 유지합니다.
            entry.CloseRequested = false;
            entry.Transient = false;
            entry.Leases++;
            entry.LastUse = ++clock;
            return entry.Version;
        }
    }

    /// <summary>
    /// 사용자가 보지 않는 일회성 문서(정의 후보 cpp, 저장 반영, 다른 파일 위치 확인, 색인 시작 문서)를 열거나, 이미 열린 문서면 사용 순서를
    /// 바꾸지 않고 임대합니다. 새로 연 문서는 정원에 세지 않으며 마지막 임대가 끝나면(<see cref="Release"/>) 바로 닫습니다.
    /// </summary>
    /// <remarks>
    /// 예전에는 일회성 문서도 정원에 세어, 풀 때 정원을 넘었다며 미리 분석해 둔 사용자 문서 중 가장 오래된 것을 먼저 닫고 일회성 문서는 그
    /// 뒤에 따로 닫았습니다. 엔진 정의를 한 번 확인할 때마다 사용자 문서 하나의 분석(Unreal 2.5~4.8초, 2026-10-10 측정)을 잃었습니다.
    /// </remarks>
    public int AcquireTransient(DocumentText document)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(document.Path, out var entry))
            {
                var text = document.Text;
                open(document.Path, text, 1);
                entry = new Entry(text, 1, document.Revision) { Transient = true };
                entries[document.Path] = entry;
            }
            else
            {
                Send(document, entry);
            }

            entry.Leases++;
            return entry.Version;
        }
    }

    /// <summary>
    /// 정원에 빈자리가 있을 때만 문서를 임대 없이 열어 둡니다. 이미 열렸거나 빈자리가 없으면 거짓입니다. 연 문서는 가장 먼저 닫힐 순서에 두어,
    /// 미리 열어 두는 문서가 그 사이 사용자가 찾은 문서를 밀어내지 않게 합니다. 나중에 연 문서일수록 먼저 닫힙니다.
    /// </summary>
    public bool TryOpenSpare(DocumentText document)
    {
        lock (gate)
        {
            if (entries.ContainsKey(document.Path) || entries.Values.Count(e => !e.Transient) >= Capacity) return false;
            var oldest = entries.Values.Where(e => !e.Transient).Select(e => e.LastUse).DefaultIfEmpty(clock + 1).Min();
            var text = document.Text;
            open(document.Path, text, 1);
            entries[document.Path] = new Entry(text, 1, document.Revision) { LastUse = oldest - 1 };
            return true;
        }
    }

    /// <param name="touch">거짓이면 최근 사용 순서를 바꾸지 않습니다. 확인용 임대(<see cref="AcquireIfOpen(string, out int, bool)"/>)와 짝을 맞춥니다.</param>
    public void Release(string path, bool touch = true)
    {
        lock (gate)
        {
            if (entries.TryGetValue(path, out var entry) && entry.Leases > 0)
            {
                entry.Leases--;
                if (touch) entry.LastUse = ++clock;
                // 임대 중에 편집기에서 닫은 문서와 일회성으로 연 문서는 마지막 임대가 끝날 때 닫습니다(2026-10-09 검토 55).
                if (entry.Leases == 0 && (entry.CloseRequested || entry.Transient))
                {
                    entries.Remove(path);
                    close(path);
                }
            }

            Evict();
        }
    }

    /// <summary>이미 열린 문서만 임대하고 clangd에 보낸 내용을 돌려줍니다. 열려 있지 않으면 임대하지 않고 null입니다.</summary>
    public string? AcquireIfOpen(string path) => AcquireIfOpen(path, out _, touch: true);

    /// <summary>열린 문서에 clangd로 마지막에 보낸 내용입니다. 임대하지 않으며 사용 순서도 바꾸지 않습니다. 열려 있지 않으면 null입니다.</summary>
    public string? SentText(string path)
    {
        lock (gate) return entries.TryGetValue(path, out var entry) ? entry.Text : null;
    }

    /// <summary>
    /// 이미 열린 문서만 임대하고 clangd에 보낸 내용과 버전을 돌려줍니다. 열려 있지 않으면 임대하지 않고 null입니다.
    /// </summary>
    /// <param name="touch">
    /// 거짓이면 최근 사용 순서를 바꾸지 않습니다. 참조 결과를 확인하려고 열린 문서를 훑을 때 쓰며, 훑은 순서로 사용 순서가 덮여 사용자가 방금 보던
    /// 문서가 먼저 닫히지 않게 합니다(2026-10-09 검토 55).
    /// </param>
    public string? AcquireIfOpen(string path, out int version, bool touch)
    {
        lock (gate)
        {
            version = 0;
            if (!entries.TryGetValue(path, out var entry)) return null;
            entry.Leases++;
            if (touch) entry.LastUse = ++clock;
            version = entry.Version;
            return entry.Text;
        }
    }

    /// <summary>이미 열린 문서만 더 새 내용으로 갱신합니다. 열려 있으면 현재 버전을 돌려줍니다.</summary>
    public int? Update(DocumentText document)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(document.Path, out var entry))
            {
                return null;
            }

            Send(document, entry);
            return entry.Version;
        }
    }

    /// <summary>임대 중이 아니면 닫습니다. 편집기에서 문서를 닫았거나 일회성으로 연 문서를 정리할 때 씁니다.</summary>
    public bool TryClose(string path)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(path, out var entry) || entry.Leases > 0)
            {
                return false;
            }

            entries.Remove(path);
            close(path);
            return true;
        }
    }

    /// <summary>
    /// 열린 문서를 닫았다가 보낸 내용·버전 그대로 다시 엽니다. 임대는 유지합니다. 열려 있지 않으면 아무것도 하지 않고 거짓입니다.
    /// </summary>
    /// <remarks>
    /// 열린 문서의 명령만 바꾸면 clangd 22.1.3은 새 preamble을 만드는 동안 이전 preamble로 만든 분석으로 진단과 요청에 먼저 답합니다
    /// (2026-10-09 정확도 시험: 공유 PCH를 넣은 뒤에도 PCH 없는 분석의 오류로 결과가 비었음). 다시 열면 새 명령의 preamble이 준비된 뒤에만
    /// 답합니다.
    /// </remarks>
    public bool Reopen(string path)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(path, out var entry)) return false;
            close(path);
            open(path, entry.Text, entry.Version);
            return true;
        }
    }

    /// <summary>
    /// 편집기에서 닫은 문서를 닫습니다. 임대 중이면 표시해 두고 마지막 임대가 끝날 때 닫습니다. 임대 중에 닫기를 놓치면 열린 문서가 정원보다
    /// 적은 동안 그 문서의 분석(Unreal 문서 하나 0.5~1.7 GB)이 계속 남았습니다(2026-10-09 검토 55).
    /// </summary>
    public void CloseWhenReleased(string path)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(path, out var entry)) return;
            if (entry.Leases > 0)
            {
                entry.CloseRequested = true;
                return;
            }

            entries.Remove(path);
            close(path);
        }
    }

    /// <summary>서버를 다시 시작했을 때 알림 없이 상태만 비웁니다.</summary>
    public void Reset()
    {
        lock (gate) entries.Clear();
    }

    private void Send(DocumentText document, Entry entry)
    {
        if (document.Revision < entry.Revision)
        {
            return;
        }

        entry.Revision = document.Revision;
        var text = document.Text;
        if (!string.Equals(entry.Text, text, StringComparison.Ordinal))
        {
            entry.Version++;
            entry.Text = text;
            change(document.Path, text, entry.Version);
        }
    }

    private void Evict()
    {
        while (entries.Values.Count(e => !e.Transient) > Capacity)
        {
            var victim = entries.Where(e => e.Value.Leases == 0 && !e.Value.Transient).OrderBy(e => e.Value.LastUse).Select(e => e.Key).FirstOrDefault();
            if (victim is null)
            {
                return;
            }

            entries.Remove(victim);
            close(victim);
        }
    }

    private sealed class Entry
    {
        public Entry(string text, int version, long revision)
        {
            Text = text;
            Version = version;
            Revision = revision;
        }

        public string Text { get; set; }

        public int Version { get; set; }

        public long Revision { get; set; }

        public int Leases { get; set; }

        public long LastUse { get; set; }

        /// <summary>임대 중에 편집기에서 닫았습니다. 마지막 임대가 끝나면 닫습니다.</summary>
        public bool CloseRequested { get; set; }

        /// <summary>일회성으로 열었습니다(<see cref="AcquireTransient"/>). 정원에 세지 않고 마지막 임대가 끝나면 닫습니다.</summary>
        public bool Transient { get; set; }
    }
}
