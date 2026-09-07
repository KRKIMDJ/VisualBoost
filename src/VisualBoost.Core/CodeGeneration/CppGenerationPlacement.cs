using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using VisualBoost.Core.DocumentNavigation;

namespace VisualBoost.Core.CodeGeneration;

public sealed partial class CppGenerationProvider
{
    private static (int Offset, string Indent)? FindNeighbor(string source, string sourceCode, GenerationFunction current,
        string target, string targetCode, int start, int end, Func<int, bool>? allowedAccess, CancellationToken token)
    {
        // 탐색 구문은 배치 후보만 공급합니다. 생성 가능 여부는 기존 언어 모델·원문 검증을 따릅니다.
        // 조건부 선언의 안팎은 추측하지 않고 기존의 클래스 끝 삽입으로 돌아갑니다.
        if (Regex.IsMatch(targetCode.Substring(start, end - start), @"(?m)^\s*#\s*(if|ifdef|ifndef|else|elif|endif)\b")) return null;
        var sourceMembers = new CppDocumentMemberProvider().Analyze(source, token).Members;
        var targets = new CppDocumentMemberProvider().Analyze(target, token).Members
            .Where(m => m.Start >= start && m.End <= end && OwnerOf(m) == current.Owner)
            .GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.ToArray());
        var siblings = sourceMembers.Where(m => m.NameOffset != current.NameStart && OwnerOf(m) == current.Owner).ToArray();
        // 직전 함수가 없으면 더 위를 찾고, 위쪽에 대응 함수가 하나도 없을 때 아래쪽을 찾습니다.
        foreach (var sibling in siblings.Where(m => m.End <= current.Start).OrderByDescending(m => m.Start)
            .Concat(siblings.Where(m => m.Start >= current.End).OrderBy(m => m.Start)))
        {
            token.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(sibling.Name, out var candidates)) continue;
            Header original;
            try { original = ReadHeader(source, sourceCode, FromMember(sibling, current.Owner, sourceCode)); }
            catch (GenerationNotSupportedException) { continue; }
            var matches = new List<DocumentMember>();
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if ((targetCode[candidate.End - 1] == '}') == current.IsDefinition) continue;
                try
                {
                    var parsed = ReadHeader(target, targetCode, FromMember(candidate, current.Owner, targetCode));
                    if (original.Types.SequenceEqual(parsed.Types) && original.Qualifiers == parsed.Qualifiers) matches.Add(candidate);
                }
                catch (GenerationNotSupportedException) { }
            }
            // 같은 이름·인수의 후보가 중복되면 잘못된 기준을 고르는 대신 다음 이웃을 확인합니다.
            if (matches.Count != 1) continue;
            var match = matches[0];
            var after = sibling.End <= current.Start;
            var offset = after ? AfterFunction(target, match.End) : BeforeFunction(target, targetCode, match.Start, start);
            if (offset < start || offset > end || allowedAccess is not null && !allowedAccess(offset)) continue;
            var lineStart = target.LastIndexOf('\n', Math.Max(0, match.Start - 1)) + 1;
            var indent = target.Substring(lineStart, match.Start - lineStart);
            if (!string.IsNullOrWhiteSpace(indent)) indent = "    ";
            return (offset, indent);
        }
        return null;
    }
    private static GenerationFunction FromMember(DocumentMember m, string owner, string code) =>
        new(m.Name, owner, m.Start, m.NameOffset, m.End, code[m.End - 1] == '}');
    private static string OwnerOf(DocumentMember member)
    {
        var names = new Stack<string>();
        for (var scope = member.Scope; scope is not null; scope = scope.Parent) names.Push(scope.Name);
        var lexical = string.Join("::", names);
        if (member.QualifiedOwner.Length == 0) return lexical;
        if (lexical.Length == 0 || member.QualifiedOwner == lexical || member.QualifiedOwner.StartsWith(lexical + "::", StringComparison.Ordinal)) return member.QualifiedOwner;
        return lexical + "::" + member.QualifiedOwner;
    }
    private static int AfterFunction(string source, int offset)
    {
        // 함수 끝의 같은 줄 주석은 함수에 남기고 다음 함수의 설명을 넘지 않습니다.
        var position = offset;
        while (position < source.Length)
        {
            if (source[position] == '\n') return position + 1;
            if (source[position] == '\r') return position + (position + 1 < source.Length && source[position + 1] == '\n' ? 2 : 1);
            if (char.IsWhiteSpace(source[position])) { position++; continue; }
            if (position + 1 < source.Length && source[position] == '/' && source[position + 1] == '/')
            {
                var newline = source.IndexOfAny(new[] { '\r', '\n' }, position);
                position = newline < 0 ? source.Length : newline;
                continue;
            }
            if (position + 1 < source.Length && source[position] == '/' && source[position + 1] == '*')
            {
                var close = source.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (close < 0) return offset;
                position = close + 2;
                continue;
            }
            return offset;
        }
        return position;
    }
    private static int BeforeFunction(string source, string code, int offset, int minimum)
    {
        var begin = source.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        if (begin < minimum || !string.IsNullOrWhiteSpace(code.Substring(begin, offset - begin))) return offset;
        var fallback = begin;
        // 앞쪽 설명 주석과 빈 줄은 아래 함수와 함께 남깁니다.
        while (begin > minimum)
        {
            var previous = source.LastIndexOf('\n', Math.Max(0, begin - 2)) + 1;
            if (previous < minimum || previous == begin) break;
            if (!string.IsNullOrWhiteSpace(code.Substring(previous, begin - previous))) break;
            begin = previous;
        }
        // 앞선 코드 줄에서 시작된 후행 주석 중간까지 올라갔다면 원래 함수 줄을 사용합니다.
        if (begin > 0 && source.LastIndexOf("/*", begin - 1, StringComparison.Ordinal) > source.LastIndexOf("*/", begin - 1, StringComparison.Ordinal)) return fallback;
        return begin;
    }
    private static string AccessAt(string code, int open, int position, string access)
    {
        var depth = 0;
        for (var i = open + 1; i < position; i++)
        {
            if (code[i] == '{') { depth++; continue; }
            if (code[i] == '}') { depth--; continue; }
            if (depth != 0 || !char.IsLetter(code[i])) continue;
            var first = i;
            while (i < position && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
            var word = code.Substring(first, i - first);
            var next = i; while (next < position && char.IsWhiteSpace(code[next])) next++;
            if (next < position && code[next] == ':' && word is "public" or "protected" or "private") access = word;
            i--;
        }
        return access;
    }
}
