using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VisualBoost.Core.Analysis;

public static class CppIdentifierUsageScanner
{
    public static IReadOnlyList<SourceUsageLocation> Find(
        string symbol,
        string path,
        string source,
        int maximumResults = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol) || !IsIdentifier(symbol) || maximumResults <= 0)
        {
            return Array.Empty<SourceUsageLocation>();
        }

        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var results = new List<SourceUsageLocation>();
        var inBlockComment = false;
        using var reader = new StringReader(source);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            ScanLine(symbol, path, line, lineNumber, results, ref inBlockComment, maximumResults);
            if (results.Count >= maximumResults)
            {
                break;
            }
        }

        return results;
    }

    private static void ScanLine(
        string symbol,
        string path,
        string line,
        int lineNumber,
        ICollection<SourceUsageLocation> results,
        ref bool inBlockComment,
        int maximumResults)
    {
        var stringDelimiter = '\0';
        var escaped = false;
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

            if (stringDelimiter != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (line[index] == '\\')
                {
                    escaped = true;
                }
                else if (line[index] == stringDelimiter)
                {
                    stringDelimiter = '\0';
                }

                continue;
            }

            if (index + 1 < line.Length && line[index] == '/' && line[index + 1] == '/')
            {
                break;
            }

            if (index + 1 < line.Length && line[index] == '/' && line[index + 1] == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }

            if (line[index] == '"' || line[index] == '\'')
            {
                stringDelimiter = line[index];
                continue;
            }

            if (!IsIdentifierStart(line[index]))
            {
                continue;
            }

            var start = index;
            while (index + 1 < line.Length && IsIdentifierPart(line[index + 1]))
            {
                index++;
            }

            var length = index - start + 1;
            if (length == symbol.Length &&
                string.Compare(line, start, symbol, 0, length, StringComparison.Ordinal) == 0)
            {
                results.Add(new SourceUsageLocation(
                    symbol,
                    path,
                    lineNumber,
                    start + 1,
                    line.Trim()));
                if (results.Count >= maximumResults)
                {
                    return;
                }
            }
        }
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsIdentifierPart(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);

    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);
}
