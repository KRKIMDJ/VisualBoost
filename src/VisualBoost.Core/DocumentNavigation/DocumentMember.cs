using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Searching;

namespace VisualBoost.Core.DocumentNavigation;

public sealed class DocumentMember
{
    public DocumentMember(string name, string kind, int nameOffset, int start, int end, int line)
    { Name = name; Kind = kind; NameOffset = nameOffset; Start = start; End = end; Line = line; }
    public string Name { get; }
    public string Kind { get; }
    public int NameOffset { get; }
    public int Start { get; }
    public int End { get; }
    public int Line { get; }
}

public interface IDocumentMemberProvider
{
    DocumentMemberSnapshot Analyze(string source, CancellationToken cancellationToken = default);
}

public sealed class DocumentMemberSnapshot
{
    private readonly DocumentMember[] members;
    private readonly DocumentMember[] alphabetical;
    private readonly SourceSymbolLocation[] symbols;
    private readonly Dictionary<SourceSymbolLocation, DocumentMember> byLocation;
    public DocumentMemberSnapshot(IEnumerable<DocumentMember> members)
    {
        this.members = members.OrderBy(m => m.Start).ToArray();
        alphabetical = this.members.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Start).ToArray();
        // 검색 결과에서 원래 버퍼 위치를 복원하며 디스크 위치로 변환하지 않습니다.
        byLocation = this.members.ToDictionary(m => new SourceSymbolLocation(m.Name, string.Empty, m.Line, 1, SourceSymbolKind.Function), m => m);
        symbols = byLocation.Keys.ToArray();
    }
    public IReadOnlyList<DocumentMember> Members => Array.AsReadOnly(members);
    public DocumentMember? FindContaining(int caretOffset)
    {
        var low = 0; var high = members.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (members[mid].Start <= caretOffset) low = mid + 1; else high = mid;
        }
        return low > 0 && caretOffset < members[low - 1].End ? members[low - 1] : null;
    }
    public IReadOnlyList<DocumentMember> Search(string query, bool nameOrder, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query)) return Array.AsReadOnly(nameOrder ? alphabetical : members);
        return FuzzySymbolSearch.Search(query, symbols, 200, token).Select(m => byLocation[m.Location]).ToArray();
    }
}
