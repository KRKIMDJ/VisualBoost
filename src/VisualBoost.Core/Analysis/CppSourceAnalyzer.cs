using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualBoost.Core.Analysis;

public static class CppSourceAnalyzer
{
    private static readonly Regex IncludePattern = new(
        @"^\s*#\s*include\s*(?<open>[<""])(?<value>[^>""]+)[>""]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MacroPattern = new(
        @"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NamespacePattern = new(
        @"\bnamespace\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TypePattern = new(
        @"\b(?:class|struct|union|enum(?:\s+class)?)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FunctionPattern = new(
        @"(?<name>[A-Za-z_~]\w*(?:::[A-Za-z_~]\w*)*)\s*\([^;{}]*\)\s*(?:const\s*)?(?:noexcept\s*)?(?:->[^;{]+)?\s*[;{]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VariablePattern = new(
        @"^\s*(?:(?:static|const|constexpr|inline|extern|mutable|thread_local)\s+)*(?:[A-Za-z_]\w*(?:::\w+)*(?:\s*[*&])?\s+)+(?<name>[A-Za-z_]\w*)\s*(?:[=;,\[])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "sizeof", "alignof", "decltype",
    };

    public static SourceFileAnalysis Analyze(string path, string source)
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
        using var reader = new StringReader(source);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var code = RemoveComments(line, ref inBlockComment);
            if (code.Length == 0)
            {
                continue;
            }

            var includeMatch = IncludePattern.Match(code);
            if (includeMatch.Success)
            {
                includes.Add(new SourceIncludeReference(
                    includeMatch.Groups["value"].Value.Trim(),
                    includeMatch.Groups["open"].Value == "<",
                    lineNumber));
                continue;
            }

            AddMatch(symbols, MacroPattern.Match(code), path, lineNumber, SourceSymbolKind.Macro);
            AddMatch(symbols, NamespacePattern.Match(code), path, lineNumber, SourceSymbolKind.Namespace);
            AddMatches(symbols, TypePattern.Matches(code), path, lineNumber, SourceSymbolKind.Type);

            var functionMatch = FunctionPattern.Match(code);
            if (functionMatch.Success)
            {
                var name = LastNameSegment(functionMatch.Groups["name"].Value);
                if (!ControlKeywords.Contains(name))
                {
                    AddMatch(symbols, functionMatch, path, lineNumber, SourceSymbolKind.Function);
                    continue;
                }
            }

            AddMatch(symbols, VariablePattern.Match(code), path, lineNumber, SourceSymbolKind.Variable);
        }

        return new SourceFileAnalysis(path, includes, symbols);
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
            nameGroup.Value,
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
