using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VisualBoost.Core.Analysis;

public static class CppSourceAnalyzer
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex IncludePattern = new(
        @"^\s*#\s*include\s*(?<open>[<""])(?<value>[^>""]+)[>""]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex MacroPattern = new(
        @"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex NamespacePattern = new(
        @"\bnamespace\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex TypePattern = new(
        @"\b(?:(?<kind>class|struct|union)\s+(?:[A-Za-z_]\w*_API\s+)?|(?<kind>enum)(?:\s+(?:class|struct))?\s+)(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex FunctionPattern = new(
        // 공백과 식별자를 되감아 분할하지 않습니다. 마스킹된 긴 주석에서도 탐색 비용을 제한합니다.
        @"(?<![\w:~])(?<name>[A-Za-z_~](?>\w*)(?:::[A-Za-z_~](?>\w*))*)(?>\s*)\([^;{}]*\)(?>\s*)(?:const\b(?>\s*))?(?:noexcept\b(?>\s*))?(?:(?:override|final)\b(?>\s*))?(?:->[^;{]+)?(?<terminator>[;{]?)(?>\s*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex VariablePattern = new(
        @"^(?>\s*)(?>(?:(?:static|const|constexpr|inline|extern|mutable|thread_local)\s+)*)(?:[A-Za-z_](?>\w*)(?:::(?>\w+))*(?:\s*[*&])?(?>\s+))+(?<name>[A-Za-z_](?>\w*))(?>\s*)(?:[=;,\[])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "sizeof", "alignof", "decltype",
    };

    public static SourceFileAnalysis Analyze(string path, string source, CancellationToken cancellationToken = default)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var includes = new List<SourceIncludeReference>();
        var symbols = new List<SourceSymbolLocation>();
        var inBlockComment = false;
        cancellationToken.ThrowIfCancellationRequested();
        var maskedSource = CppSymbolDetails.Mask(source, cancellationToken);
        using var maskedReader = new StringReader(maskedSource);
        using var reader = new StringReader(source);
        string? line;
        var lineNumber = 0;
        var sourceOffset = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var code = maskedReader.ReadLine() ?? string.Empty;
            var lineOffset = sourceOffset;
            var nextLine = maskedSource.IndexOf('\n', sourceOffset);
            sourceOffset = nextLine < 0 ? maskedSource.Length : nextLine + 1;
            var includeCode = RemoveComments(line, ref inBlockComment);
            if (code.Length == 0)
            {
                continue;
            }

            var includeMatch = code.TrimStart().StartsWith("#", StringComparison.Ordinal) ? IncludePattern.Match(includeCode) : Match.Empty;
            if (includeMatch.Success)
            {
                includes.Add(new SourceIncludeReference(
                    includeMatch.Groups["value"].Value.Trim(),
                    includeMatch.Groups["open"].Value == "<",
                    lineNumber));
                continue;
            }

            var macro = MacroPattern.Match(code);
            if (macro.Success) { AddMatch(symbols, macro, path, lineNumber, SourceSymbolKind.Macro); continue; }
            AddMatch(symbols, NamespacePattern.Match(code), path, lineNumber, SourceSymbolKind.Namespace);
            var typeMatches = TypePattern.Matches(code);
            foreach (Match typeMatch in typeMatches)
            {
                if (IsForwardDeclaration(code, typeMatch))
                {
                    continue;
                }

                var kind = typeMatch.Groups["kind"].Value switch
                {
                    "class" => SourceSymbolKind.Class,
                    "struct" => SourceSymbolKind.Struct,
                    "union" => SourceSymbolKind.Union,
                    "enum" => SourceSymbolKind.Enum,
                    _ => SourceSymbolKind.Type,
                };
                AddMatch(symbols, typeMatch, path, lineNumber, kind);
            }

            var functionMatch = FunctionPattern.Match(code);
            if (!functionMatch.Success && code.IndexOf('(') >= 0 && code.IndexOfAny(new[] { ';', '{', '}' }) < 0)
            {
                // 여러 줄 인수 목록만 제한적으로 이어 읽습니다. 파일 전체 정규식 검색은 하지 않습니다.
                var end = sourceOffset;
                for (var count = 0; count < 16 && end < maskedSource.Length && end - lineOffset < 4096; count++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var newline = maskedSource.IndexOf('\n', end);
                    end = newline < 0 ? maskedSource.Length : newline + 1;
                    if (end - lineOffset > 4096) break;
                    var candidate = maskedSource.Substring(lineOffset, end - lineOffset);
                    var match = FunctionPattern.Match(candidate);
                    if (match.Success && match.Groups["name"].Index < code.Length) { code = candidate; functionMatch = match; break; }
                    if (candidate.IndexOfAny(new[] { ';', '{', '}' }) >= 0) break;
                }
            }
            if (functionMatch.Success && IsFunctionDeclarationOrDefinition(code, functionMatch))
            {
                var name = LastNameSegment(functionMatch.Groups["name"].Value);
                if (!ControlKeywords.Contains(name))
                {
                    AddMatch(symbols, functionMatch, path, lineNumber, SourceSymbolKind.Function);
                    continue;
                }
            }

            var variableMatch = VariablePattern.Match(code);
            // 전방 선언의 타입 이름을 변수로 다시 등록하면 선언 헤더 후보가 오염됩니다.
            if (variableMatch.Success)
            {
                foreach (Match typeMatch in typeMatches)
                {
                    if (typeMatch.Groups["name"].Index == variableMatch.Groups["name"].Index)
                    {
                        variableMatch = Match.Empty;
                        break;
                    }
                }
            }
            AddMatch(symbols, variableMatch, path, lineNumber, SourceSymbolKind.Variable);
        }

        return new SourceFileAnalysis(path, includes, CppSymbolDetails.Enrich(source, symbols, maskedSource, cancellationToken));
    }

    private static bool IsForwardDeclaration(string code, Match match)
    {
        var suffix = code.Substring(match.Index + match.Length).TrimStart();
        return suffix.StartsWith(";", StringComparison.Ordinal);
    }

    private static bool IsFunctionDeclarationOrDefinition(
        string code,
        Match match)
    {
        var nameGroup = match.Groups["name"];
        var qualifiedName = nameGroup.Value;
        var simpleName = LastNameSegment(qualifiedName).TrimStart('~');
        var prefix = code.Substring(0, nameGroup.Index).Trim();
        if (prefix.Length == 0)
        {
            var qualifierOffset = qualifiedName.LastIndexOf("::", StringComparison.Ordinal);
            if (qualifierOffset < 0)
            {
                return false;
            }

            var qualifier = qualifiedName.Substring(0, qualifierOffset);
            var containingType = LastNameSegment(qualifier);
            return string.Equals(containingType, simpleName, StringComparison.Ordinal);
        }

        if (prefix.IndexOf('=') >= 0 ||
            prefix.EndsWith(".", StringComparison.Ordinal) ||
            prefix.EndsWith("->", StringComparison.Ordinal) ||
            prefix.EndsWith("(", StringComparison.Ordinal) ||
            prefix.EndsWith("[", StringComparison.Ordinal) ||
            prefix.EndsWith("!", StringComparison.Ordinal))
        {
            return false;
        }

        var firstWordEnd = prefix.IndexOfAny(new[] { ' ', '\t', '(' });
        var firstWord = firstWordEnd < 0 ? prefix : prefix.Substring(0, firstWordEnd);
        return !ControlKeywords.Contains(firstWord) &&
               !string.Equals(firstWord, "return", StringComparison.Ordinal) &&
               !string.Equals(firstWord, "co_return", StringComparison.Ordinal) &&
               !string.Equals(firstWord, "throw", StringComparison.Ordinal) &&
               !string.Equals(firstWord, "new", StringComparison.Ordinal) &&
               !string.Equals(firstWord, "delete", StringComparison.Ordinal);
    }

    private static void AddMatches(
        ICollection<SourceSymbolLocation> symbols,
        MatchCollection matches,
        string path,
        int line,
        SourceSymbolKind kind)
    {
        foreach (Match match in matches)
        {
            AddMatch(symbols, match, path, line, kind);
        }
    }

    private static void AddMatch(
        ICollection<SourceSymbolLocation> symbols,
        Match match,
        string path,
        int line,
        SourceSymbolKind kind)
    {
        if (!match.Success)
        {
            return;
        }

        var nameGroup = match.Groups["name"];
        symbols.Add(new SourceSymbolLocation(
            kind == SourceSymbolKind.Function ? LastNameSegment(nameGroup.Value) : nameGroup.Value,
            path,
            line,
            nameGroup.Index + 1,
            kind));
    }

    private static string RemoveComments(string line, ref bool inBlockComment)
    {
        var result = new StringBuilder(line.Length);
        for (var index = 0; index < line.Length; index++)
        {
            if (inBlockComment)
            {
                if (index + 1 < line.Length && line[index] == '*' && line[index + 1] == '/')
                {
                    inBlockComment = false;
                    index++;
                }

                continue;
            }

            if (index + 1 < line.Length && line[index] == '/' && line[index + 1] == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }

            if (index + 1 < line.Length && line[index] == '/' && line[index + 1] == '/')
            {
                break;
            }

            result.Append(line[index]);
        }

        return result.ToString();
    }

    private static string LastNameSegment(string name)
    {
        var separator = name.LastIndexOf("::", StringComparison.Ordinal);
        return separator < 0 ? name : name.Substring(separator + 2);
    }
}
