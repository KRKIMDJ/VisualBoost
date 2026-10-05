using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.DocumentNavigation;

public sealed class CommentSymbolReference
{
    public CommentSymbolReference(int start, int length, string name) { Start = start; Length = length; Name = name; }
    public int Start { get; }
    public int Length { get; }
    public string Name { get; }
    public string ShortName => Name.Substring(Name.LastIndexOf("::", StringComparison.Ordinal) + (Name.Contains("::") ? 2 : 1));
}

/// <summary>주석 링크만 해석합니다. 소속/참조 그래프나 새 탐색 인덱스를 만들지 않습니다.</summary>
public static class CommentSymbolReferences
{
    private static readonly Regex Names = new(@"(?<![\w:/\\.@-])(?<name>[A-Za-z_]\w*(?:::[A-Za-z_~]\w*)*)(?![\w:/\\@-]|\.\w)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(40));
    private static readonly Regex Namespace = new(@"\bnamespace\s+(?<name>[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)\s*\{", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(40));
    private static readonly Regex TypeBody = new(@"\G\s*(?:final\s*)?(?::[^;{}()=]+)?\s*\{", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(40));
    public static bool IsTypeOrNamespace(SourceSymbolKind kind) => kind is SourceSymbolKind.Class or SourceSymbolKind.Struct or SourceSymbolKind.Union or SourceSymbolKind.Type or SourceSymbolKind.Namespace;

    public static IReadOnlyList<CommentSymbolReference> Parse(string comment, Func<string, bool> knownTypeOrNamespace)
    {
        if (comment.Length > 16384) return Array.Empty<CommentSymbolReference>();
        var files = CommentFileReferences.Parse(comment);
        var result = new List<CommentSymbolReference>();
        var inspected = 0;
        foreach (Match match in Names.Matches(comment))
        {
            if (++inspected > 128) break;
            if (files.Any(f => match.Index < f.Start + f.Length && match.Index + match.Length > f.Start)) continue;
            var name = match.Value;
            if (!name.Contains("::") && !knownTypeOrNamespace(name)) continue;
            result.Add(new CommentSymbolReference(match.Index, match.Length, name));
        }
        return result;
    }

    public static bool Matches(string reference, string qualified) => string.Equals(reference, qualified, StringComparison.Ordinal) || qualified.EndsWith("::" + reference, StringComparison.Ordinal);

    // 후보 파일은 호출자가 기존 인덱스의 이름 조회로 제한합니다. 파일 I/O 정책도 호출자가 소유합니다.
    public static IReadOnlyList<SourceSymbolLocation> Resolve(CommentSymbolReference reference, IEnumerable<SourceSymbolLocation> candidates,
        Func<string, string?> readSource, CancellationToken token = default)
    {
        var result = new List<SourceSymbolLocation>();
        foreach (var path in candidates.Where(s => s.Kind == SourceSymbolKind.Function || IsTypeOrNamespace(s.Kind))
            .Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var source = readSource(path);
            if (source is null) continue;
            var symbols = CppSourceAnalyzer.Analyze(path, source, token).Symbols;
            var masked = CppSymbolDetails.Mask(source, token);
            foreach (var symbol in symbols.Where(s => IsTypeOrNamespace(s.Kind) && s.Kind != SourceSymbolKind.Namespace))
            {
                if (!Matches(reference.Name, Qualify(symbol.Scope, symbol.Name))) continue;
                var afterName = LineOffset(source, symbol.Line) + symbol.Column - 1 + symbol.Name.Length;
                // class Foo* 같은 elaborated type 사용부와 전방 선언은 정의로 연결하지 않습니다.
                if (afterName < masked.Length && TypeBody.Match(masked, afterName, Math.Min(4096, masked.Length - afterName)).Success) result.Add(symbol);
            }
            foreach (var symbol in symbols.Where(s => s.Kind == SourceSymbolKind.Namespace))
            {
                // 축약 namespace A::B 문법도 현재 문서에서 확인하고 기존 인덱스는 변경하지 않습니다.
                var offset = LineOffset(source, symbol.Line);
                var end = source.IndexOf('\n', offset); if (end < 0) end = source.Length;
                var match = Namespace.Match(masked, offset, Math.Min(masked.Length - offset, Math.Max(end - offset + 256, 256)));
                if (!match.Success || match.Index > end) continue;
                var name = match.Groups["name"].Value;
                var full = name.Contains("::") ? name : Qualify(symbol.Scope, name);
                if (Matches(reference.Name, full) || full.StartsWith(reference.Name + "::", StringComparison.Ordinal))
                    result.Add(new SourceSymbolLocation(full, path, symbol.Line, match.Groups["name"].Index - offset + 1, SourceSymbolKind.Namespace));
            }
            if (!reference.Name.Contains("::")) continue;
            var members = new CppDocumentMemberProvider().Analyze(source, token).Members;
            foreach (var member in members)
            {
                token.ThrowIfCancellationRequested();
                if (member.Name != reference.ShortName) continue;
                if (member.End <= 0 || member.End > masked.Length || masked[member.End - 1] != '}') continue;
                var parents = new Stack<string>();
                for (var scope = member.Scope; scope is not null; scope = scope.Parent) parents.Push(scope.Name);
                var owner = string.Join("::", parents);
                if (member.QualifiedOwner.Length > 0)
                    owner = owner.Length == 0 || member.QualifiedOwner == owner || member.QualifiedOwner.StartsWith(owner + "::", StringComparison.Ordinal)
                        ? member.QualifiedOwner : owner + "::" + member.QualifiedOwner;
                if (!Matches(reference.Name, Qualify(owner, member.Name))) continue;
                result.Add(new SourceSymbolLocation(member.Name, path, member.Line, member.NameOffset - LineOffset(source, member.Line) + 1,
                    SourceSymbolKind.Function, owner, source.Substring(member.NameOffset + member.Name.Length, Math.Min(member.End - member.NameOffset - member.Name.Length, 160)).Split('{')[0].Trim()));
            }
        }
        return result.GroupBy(s => (s.Path.ToUpperInvariant(), s.Line, s.Column)).Select(g => g.First())
            .OrderBy(s => MatchesExactly(reference.Name, s) ? 0 : 1).ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Line).ToArray();
    }
    private static bool MatchesExactly(string name, SourceSymbolLocation s) => name == Qualify(s.Scope, s.Name);
    private static string Qualify(string scope, string name) => scope.Length == 0 ? name : scope + "::" + name;
    private static int LineOffset(string source, int line)
    {
        var offset = 0;
        for (var i = 1; i < line; i++) { var end = source.IndexOf('\n', offset); if (end < 0) return source.Length; offset = end + 1; }
        return offset;
    }
}
