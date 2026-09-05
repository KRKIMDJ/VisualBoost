using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.DocumentNavigation;

// 읽기 전용 문서 탐색용 구문 추출기입니다. 생성/리팩터링의 의미 판정에 사용하지 않습니다.
public sealed class CppDocumentMemberProvider : IDocumentMemberProvider
{
    public DocumentMemberSnapshot Analyze(string source, CancellationToken cancellationToken = default)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > 8 * 1024 * 1024) throw new ArgumentException("문서 함수 탐색은 8,388,608 UTF-16 문자 이하 문서를 지원합니다.", nameof(source));
        var parser = new Parser(CppSymbolDetails.Mask(source, cancellationToken), cancellationToken);
        return new DocumentMemberSnapshot(parser.Run());
    }

    private sealed class Parser
    {
        private readonly struct Token
        {
            public Token(string text, int start, int line) { Text = text; Start = start; Line = line; }
            public string Text { get; }
            public int Start { get; }
            public int Line { get; }
        }
        private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
        { "if", "for", "while", "switch", "catch", "sizeof", "alignof", "decltype", "noexcept", "requires", "static_assert", "return", "throw", "new", "delete" };
        private readonly List<Token> tokens = new();
        private readonly List<DocumentMember> result = new();
        private readonly CancellationToken token;
        private readonly int length;
        private int[] pairs = Array.Empty<int>();
        private string At(int i) => i >= 0 && i < tokens.Count ? tokens[i].Text : string.Empty;

        public Parser(string code, CancellationToken token)
        {
            this.token = token; length = code.Length;
            var line = 1; var lineStart = true;
            for (var i = 0; i < code.Length;)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                if (code[i] == '\n') { line++; lineStart = true; i++; continue; }
                if (char.IsWhiteSpace(code[i])) { i++; continue; }
                if (lineStart && code[i] == '#')
                {
                    // 전처리 지시문 및 이어진 매크로 본문의 중괄호는 문서 범위에 포함하지 않습니다.
                    bool continuation;
                    do
                    {
                        token.ThrowIfCancellationRequested();
                        var end = code.IndexOf('\n', i);
                        if (end < 0) { i = code.Length; break; }
                        var previous = end - 1;
                        if (previous >= i && code[previous] == '\r') previous--;
                        continuation = previous >= i && code[previous] == '\\';
                        i = end + 1; line++;
                    } while (continuation);
                    continue;
                }
                lineStart = false;
                var start = i++;
                if (char.IsLetter(code[start]) || code[start] == '_')
                    while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
                    { if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); i++; }
                else if (i < code.Length && (code[start] == ':' && code[i] == ':' || code[start] == '-' && code[i] == '>')) i++;
                tokens.Add(new Token(code.Substring(start, i - start), start, line));
            }
            pairs = Enumerable.Repeat(-1, tokens.Count).ToArray();
            var stack = new Stack<int>();
            for (var i = 0; i < tokens.Count; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                if (At(i) is "(" or "[" or "{") stack.Push(i);
                else if (At(i) is ")" or "]" or "}")
                {
                    if (stack.Count == 0) continue;
                    var open = stack.Peek();
                    if (At(open) + At(i) is "()" or "[]" or "{}") { stack.Pop(); pairs[open] = i; pairs[i] = open; }
                }
            }
        }
        public IReadOnlyList<DocumentMember> Run() { Walk(0, tokens.Count, string.Empty, 0); return result; }

        private void Walk(int begin, int end, string owner, int depth)
        {
            if (depth > 128) return;
            var statement = begin;
            for (var i = begin; i < end; i++)
            {
                token.ThrowIfCancellationRequested();
                if (At(i) == ";") { statement = i + 1; continue; }
                if (At(i) == ":" && At(i - 1) is "public" or "private" or "protected") { statement = i + 1; continue; }
                if (At(i) == "[" && pairs[i] >= 0) { i = pairs[i]; continue; }
                if (At(i) == "(" && pairs[i] >= 0)
                {
                    var close = pairs[i];
                    if (TryName(statement, i, owner, out var name, out var nameStart, out var kind))
                    {
                        var terminal = Terminal(close + 1, end);
                        if (terminal >= 0)
                        {
                            var body = At(terminal) == "{";
                            var finish = body ? pairs[terminal] : terminal;
                            var endOffset = finish >= 0 ? tokens[finish].Start + At(finish).Length : length;
                            result.Add(new DocumentMember(name, kind, tokens[nameStart].Start, tokens[statement].Start, endOffset, tokens[nameStart].Line));
                            i = finish >= 0 ? finish : end;
                            statement = i + 1; continue;
                        }
                    }
                    // 주석처럼 사용하는 단독 매크로 호출은 다음 선언의 접두부로 남기지 않습니다.
                    if (i == statement + 1 && At(statement).All(c => !char.IsLetter(c) || char.IsUpper(c)) && !Excluded.Contains(At(statement)))
                        statement = close + 1;
                    i = close; continue;
                }
                if (At(i) == "{")
                {
                    var close = pairs[i] < 0 ? end : pairs[i];
                    var type = string.Empty; var isScope = false;
                    for (var j = statement; j < i; j++)
                    {
                        if ((j & 1023) == 0) token.ThrowIfCancellationRequested();
                        if (At(j) == "namespace" || At(j) == "extern") { isScope = true; type = string.Empty; }
                        if (At(j) is "class" or "struct" or "union")
                        {
                            if (At(j - 1) is "enum" or "<" or ",") continue;
                            var candidate = j + 1;
                            if (At(candidate).EndsWith("_API", StringComparison.Ordinal)) candidate++;
                            if (Identifier(At(candidate))) { type = At(candidate); isScope = true; }
                        }
                    }
                    if (isScope) Walk(i + 1, close, type, depth + 1);
                    i = close; statement = i + 1;
                }
            }
        }

        private bool TryName(int statement, int open, string owner, out string name, out int nameStart, out string kind)
        {
            name = string.Empty; nameStart = open - 1; kind = "function";
            if (nameStart < statement) return false;
            // 연산자 이름은 매개변수 괄호와 이름 자체의 괄호를 분리합니다.
            var op = -1;
            for (var j = Math.Max(statement, open - 5); j < open; j++) if (At(j) == "operator") op = j;
            if (op >= 0)
            {
                if (At(open - 1) == "operator") return false;
                nameStart = op;
                name = "operator" + (Identifier(At(op + 1)) ? " " : string.Empty) + string.Concat(tokens.Skip(op + 1).Take(open - op - 1).Select(t => t.Text));
            }
            else
            {
                if (!Identifier(At(nameStart)) || Excluded.Contains(At(nameStart))) return false;
                name = At(nameStart);
                if (At(nameStart - 1) == "~") { nameStart--; name = "~" + name; }
            }
            var qualifiedStart = nameStart;
            while (qualifiedStart >= statement + 2 && At(qualifiedStart - 1) == "::" && Identifier(At(qualifiedStart - 2))) qualifiedStart -= 2;
            var containingType = qualifiedStart < nameStart ? At(nameStart - 2) : owner;
            var constructor = name.TrimStart('~') == containingType && containingType.Length > 0;
            if (constructor) kind = name.StartsWith("~", StringComparison.Ordinal) ? "destructor" : "constructor";
            if (!constructor && op < 0 && qualifiedStart == statement) return false;
            if (op >= 0 && qualifiedStart == statement && owner.Length == 0) return false;
            for (var j = statement; j < qualifiedStart; j++)
            {
                if ((j & 1023) == 0) token.ThrowIfCancellationRequested();
                if (At(j) is "=" or "." or "->" || At(j) is "return" or "throw" or "new" or "delete") return false;
                if (At(j) is "(" or "[" && pairs[j] >= 0) j = pairs[j];
            }
            return true;
        }

        private int Terminal(int start, int end)
        {
            var initializer = false;
            // 불완전한 선언에서 무관한 후속 함수까지 훑지 않도록 탐색 거리를 제한합니다.
            var maxOffset = start < end ? tokens[start].Start + 8192 : length;
            for (var j = start; j < end && tokens[j].Start < maxOffset; j++)
            {
                token.ThrowIfCancellationRequested();
                if (At(j) == ";") return j;
                if (At(j) == ":") { initializer = true; continue; }
                if (At(j) == "}") return -1;
                if (At(j) == "{")
                {
                    if (initializer && Identifier(At(j - 1)) && pairs[j] >= 0) { j = pairs[j]; continue; }
                    return j;
                }
                if (At(j) is "(" or "[")
                {
                    if (pairs[j] < 0) return -1;
                    if (!initializer && At(j - 1) != "noexcept" && At(j) != "[") return -1;
                    j = pairs[j];
                }
                else if (!initializer && Identifier(At(j)) && At(j) is not ("const" or "volatile" or "noexcept" or "override" or "final" or "default" or "delete"))
                    return -1;
            }
            return -1;
        }
        private static bool Identifier(string value) => value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_');
    }
}
