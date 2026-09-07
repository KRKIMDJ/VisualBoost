using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VisualBoost.Core.DocumentNavigation;

public sealed class DocumentTreeNode
{
    internal DocumentTreeNode(string name, string kind, DocumentMember? member, DocumentTreeNode? parent)
    { Name = name; Kind = kind; Member = member; Parent = parent; }
    public string Name { get; }
    public string Kind { get; }
    public DocumentMember? Member { get; }
    public DocumentTreeNode? Parent { get; }
    public IReadOnlyList<DocumentTreeNode> Children => children;
    internal readonly List<DocumentTreeNode> children = new();
}

public static class DocumentMemberTree
{
    public static IReadOnlyList<DocumentTreeNode> Build(IReadOnlyList<DocumentMember> members, CancellationToken token = default, bool nameOrder = false)
    {
        var roots = new List<DocumentTreeNode>();
        var lexical = new Dictionary<DocumentScope, DocumentTreeNode>();
        var qualified = new Dictionary<(DocumentTreeNode?, string), DocumentTreeNode>();
        // 실제 소스에서 확인한 범위를 먼저 등록해 같은 파일의 클래스 밖 정의도 가능한 경우 같은 그룹에 묶습니다.
        foreach (var member in members) { token.ThrowIfCancellationRequested(); if (member.Scope is not null) Scope(member.Scope); }
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested();
            var parent = member.Scope is null ? null : Scope(member.Scope);
            if (member.QualifiedOwner.Length > 0)
            {
                var segments = member.QualifiedOwner.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
                // 원문에 완전 한정 경로가 있으면 같은 어휘 범위를 두 번 붙이지 않습니다.
                var ancestry = new Stack<string>();
                for (var p = parent; p is not null; p = p.Parent) ancestry.Push(p.Name);
                var prefix = ancestry.SelectMany(n => n.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries)).ToArray();
                var skip = segments.Length >= prefix.Length && segments.Take(prefix.Length).SequenceEqual(prefix) ? prefix.Length : 0;
                foreach (var segment in segments.Skip(skip).Take(128))
                {
                    if (!qualified.TryGetValue((parent, segment), out var node))
                    {
                        // 클래스인지 네임스페이스인지 확정할 수 없는 외부 한정 이름은 일반 scope로 표시합니다.
                        node = new DocumentTreeNode(segment, "scope", null, parent);
                        qualified[(parent, segment)] = node; Add(node);
                    }
                    parent = node;
                }
            }
            Add(new DocumentTreeNode(member.Name, member.Kind, member, parent));
        }
        // 형제의 순서는 입력 검색 결과의 최초 함수 순서를 기준으로 합니다. 이름 정렬도 계층을 깨지 않습니다.
        var rank = members.Select((m, i) => (m, i)).ToDictionary(v => v.m, v => v.i);
        Sort(roots);
        return roots;

        DocumentTreeNode Scope(DocumentScope scope)
        {
            if (lexical.TryGetValue(scope, out var node)) return node;
            var parent = scope.Parent is null ? null : Scope(scope.Parent);
            node = new DocumentTreeNode(scope.Name, scope.Kind, null, parent);
            lexical[scope] = node;
            // 이름이 같은 재개방 범위는 각각 원문의 블록으로 남기며 한정 정의의 연결만 최초 범위를 사용합니다.
            if (!qualified.ContainsKey((parent, scope.Name))) qualified[(parent, scope.Name)] = node;
            Add(node); return node;
        }
        void Add(DocumentTreeNode node) => (node.Parent?.children ?? roots).Add(node);
        int Sort(List<DocumentTreeNode> nodes)
        {
            var ranked = new List<(DocumentTreeNode node, int rank)>();
            foreach (var node in nodes)
            { token.ThrowIfCancellationRequested(); ranked.Add((node, node.Member is not null ? rank[node.Member] : Sort(node.children))); }
            nodes.Clear(); nodes.AddRange((nameOrder ? ranked.OrderBy(v => v.node.Name, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.rank) : ranked.OrderBy(v => v.rank)).Select(v => v.node));
            return ranked.Count == 0 ? int.MaxValue : ranked.Min(v => v.rank);
        }
    }
}
