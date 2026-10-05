using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VisualBoost.Core.Coloring;

public readonly struct QuickColorSpan
{
    public QuickColorSpan(int start, int length, SemanticColorKind kind) { Start = start; Length = length; Kind = kind; }
    public int Start { get; }
    public int Length { get; }
    public SemanticColorKind Kind { get; }
}

/// <summary>단일 편집 문서의 색상 힌트만 만듭니다. 선언 동일성·참조·외부 파일 분석에 사용하지 않습니다.</summary>
public static class CppQuickColorScanner
{
    public const int MaximumLength = 2 * 1024 * 1024;
    private static readonly HashSet<string> Keywords = new(("alignas alignof asm auto bool break case catch char char8_t char16_t char32_t class const consteval constexpr constinit const_cast continue co_await co_return co_yield decltype default delete do double dynamic_cast else enum explicit export extern false float for friend goto if inline int interface __interface long mutable namespace new noexcept nullptr operator private protected public register reinterpret_cast requires return short signed sizeof static static_assert static_cast struct switch template this thread_local throw true try typedef typeid typename union unsigned using virtual void volatile wchar_t while concept final override").Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> Builtins = new("auto bool char char8_t char16_t char32_t double float int long short signed unsigned void wchar_t".Split(' '), StringComparer.Ordinal);

    public static IReadOnlyList<QuickColorSpan> Scan(string source, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (source.Length > MaximumLength) return Array.Empty<QuickColorSpan>();
        var tokens = Lex(source, token);
        if (tokens.Count > 250000) return Array.Empty<QuickColorSpan>();
        var direct = new SemanticColorKind?[tokens.Count];
        var names = new Dictionary<string, SemanticColorKind?>(StringComparer.Ordinal);
        // 입력 도중 닫히지 않은 enum이 반복되어도 중첩된 뒷부분을 무제한 재검사하지 않습니다.
        var enumBudget = tokens.Count * 2;
        string Text(int i) => i >= 0 && i < tokens.Count ? tokens[i].Text : "";
        bool Identifier(int i) => i >= 0 && i < tokens.Count && tokens[i].Identifier && !Keywords.Contains(Text(i));
        void Mark(int i, SemanticColorKind kind)
        {
            if (!Identifier(i)) return;
            direct[i] = kind;
            names[Text(i)] = names.TryGetValue(Text(i), out var previous) && previous != kind ? null : kind;
        }
        for (var i = 0; i < tokens.Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            if (tokens[i].Macro) { Mark(i, SemanticColorKind.Macro); continue; }
            if (Text(i) is "class" or "struct" or "union" or "interface" or "__interface" or "enum")
            {
                var n = i + 1;
                if (Text(i) == "enum" && Text(n) is "class" or "struct") n++;
                if (Text(n).EndsWith("_API", StringComparison.Ordinal)) n++;
                Mark(n, SemanticColorKind.Type);
                if (Text(i) == "enum")
                {
                    var open = n;
                    while (open < tokens.Count && open < n + 32 && Text(open) != "{" && Text(open) != ";") open++;
                    if (Text(open) == "{")
                    {
                        var depth = 0; var expectName = true;
                        for (var j = open + 1; j < tokens.Count && j < open + 4096 && enumBudget-- > 0; j++)
                        {
                            if ((j & 255) == 0) token.ThrowIfCancellationRequested();
                            var t = Text(j);
                            if (t == "}" && depth == 0) break;
                            if (depth == 0 && expectName && Identifier(j)) { Mark(j, SemanticColorKind.EnumMember); expectName = false; }
                            if (t is "(" or "[" or "{") depth++;
                            else if (t is ")" or "]" or "}") depth--;
                            else if (t == "," && depth == 0) expectName = true;
                        }
                    }
                }
            }
            if (Text(i) == "namespace")
            {
                var n = i + 1; Mark(n, SemanticColorKind.Namespace);
                while (Text(n + 1) == "::" && Identifier(n + 2)) { n += 2; Mark(n, SemanticColorKind.Namespace); }
            }
            if (Text(i) == "using" && Identifier(i + 1) && Text(i + 2) == "=") Mark(i + 1, SemanticColorKind.Type);
            if (Text(i) == "typedef")
            {
                var end = i + 1;
                while (end < tokens.Count && end < i + 256 && Text(end) != ";" && Text(end) != "(" && Text(end) != "{") end++;
                if (Text(end) == ";") Mark(end - 1, SemanticColorKind.Type);
            }
        }
        for (var i = 0; i < tokens.Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            if (!Identifier(i) || direct[i].HasValue || Text(i + 1) == "(" || Text(i + 1) == "::") continue;
            var previous = i - 1;
            while (Text(previous) is "*" or "&" or "&&" or "const" or "volatile") previous--;
            if ((Builtins.Contains(Text(previous)) || (names.TryGetValue(Text(previous), out var type) && type == SemanticColorKind.Type)) &&
                Text(i + 1) is ";" or "=" or "," or ")" or "[") Mark(i, SemanticColorKind.Variable);
        }
        var result = new List<QuickColorSpan>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            if (!Identifier(i)) continue;
            var kind = direct[i];
            if (!kind.HasValue) names.TryGetValue(Text(i), out kind);
            if (!kind.HasValue && Text(i + 1) == "(" && !Text(i).All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_'))
                kind = SemanticColorKind.Function;
            if (kind.HasValue) result.Add(new QuickColorSpan(tokens[i].Start, Text(i).Length, kind.Value));
        }
        return result.AsReadOnly();
    }

    private readonly struct Token
    {
        internal Token(string text, int start, bool identifier, bool macro = false) { Text = text; Start = start; Identifier = identifier; Macro = macro; }
        internal string Text { get; }
        internal int Start { get; }
        internal bool Identifier { get; }
        internal bool Macro { get; }
    }

    private static List<Token> Lex(string source, CancellationToken token)
    {
        var result = new List<Token>();
        var i = 0;
        bool Part(char c) => char.IsLetterOrDigit(c) || c == '_';
        int SkipLine(int at)
        {
            while (at < source.Length)
            {
                token.ThrowIfCancellationRequested();
                var end = source.IndexOf('\n', at);
                if (end < 0) return source.Length;
                var before = end - 1;
                if (before >= 0 && source[before] == '\r') before--;
                at = end + 1;
                if (before < 0 || source[before] != '\\') return at;
            }
            return at;
        }
        while (i < source.Length && result.Count <= 250000)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            var c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/') { i = SkipLine(i); continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            { var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? source.Length : end + 2; continue; }
            if (c == '#')
            {
                var end = SkipLine(i); var p = i + 1;
                while (p < end && source[p] is ' ' or '\t') p++;
                var word = p; while (p < end && char.IsLetter(source[p])) p++;
                if (source.Substring(word, p - word) == "define")
                {
                    while (p < end && source[p] is ' ' or '\t') p++;
                    var name = p; while (p < end && Part(source[p])) p++;
                    if (p > name) result.Add(new Token(source.Substring(name, p - name), name, true, true));
                }
                i = end; continue;
            }
            var quote = i;
            if (i + 1 < source.Length && c == 'u' && source[i + 1] == '8') quote += 2;
            else if (c is 'u' or 'U' or 'L') quote++;
            var raw = quote < source.Length && source[quote] == 'R';
            if (raw) quote++;
            if (quote < source.Length && (source[quote] == '"' || (!raw && source[quote] == '\'')))
            {
                if (raw)
                {
                    var open = source.IndexOf('(', quote + 1);
                    if (open < 0 || open - quote > 17) break;
                    var suffix = ")" + source.Substring(quote + 1, open - quote - 1) + "\"";
                    var end = source.IndexOf(suffix, open + 1, StringComparison.Ordinal);
                    if (end < 0) break;
                    i = end + suffix.Length;
                }
                else
                {
                    var delimiter = source[quote]; i = quote + 1;
                    while (i < source.Length && source[i] != delimiter)
                    { if ((i & 4095) == 0) token.ThrowIfCancellationRequested(); i += source[i] == '\\' && i + 1 < source.Length ? 2 : 1; }
                    if (i < source.Length) i++;
                }
                while (i < source.Length && Part(source[i])) i++;
                // 빈 리터럴 토큰으로 앞뒤 코드 식별자가 선언처럼 이어지는 것도 방지합니다.
                result.Add(new Token("<literal>", quote, false)); continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < source.Length && Part(source[i])) i++;
                result.Add(new Token(source.Substring(start, i - start), start, true)); continue;
            }
            if (char.IsDigit(c))
            {
                var start = i++;
                while (i < source.Length && (Part(source[i]) || source[i] is '.' or '\'')) i++;
                result.Add(new Token(source.Substring(start, i - start), start, false)); continue;
            }
            var width = i + 1 < source.Length && (source.Substring(i, 2) is "::" or "->" or "&&") ? 2 : 1;
            result.Add(new Token(source.Substring(i, width), i, false)); i += width;
        }
        return result;
    }
}
