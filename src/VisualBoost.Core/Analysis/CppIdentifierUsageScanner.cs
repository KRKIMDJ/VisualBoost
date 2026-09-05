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
        var stringDelimiter = '\0';
        var escaped = false;
        string? rawStringEnd = null;
        using var reader = new StringReader(source);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            ScanLine(symbol, path, line, lineNumber, results, ref inBlockComment,
                ref stringDelimiter, ref escaped, ref rawStringEnd, maximumResults, cancellationToken);
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
        ref char stringDelimiter,
        ref bool escaped,
        ref string? rawStringEnd,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        // 검색어가 없는 줄에는 표시용 메타데이터를 할당하지 않습니다.
        var identifiers = line.IndexOf(symbol, StringComparison.Ordinal) >= 0
            ? new List<SourceIdentifierSpan>() : null;
        List<int>? columns = null;
        for (var index = 0; index < line.Length; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (rawStringEnd is not null)
            {
                var closing = line.IndexOf(rawStringEnd, index, StringComparison.Ordinal);
                if (closing < 0) break;
                index = closing + rawStringEnd.Length - 1;
                rawStringEnd = null;
                continue;
            }
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
                if (line[index] == '"' && index > 0 && line[index - 1] == 'R')
                {
                    var opening = line.IndexOf('(', index + 1);
                    if (opening >= 0 && opening - index - 1 <= 16)
                    {
                        rawStringEnd = ")" + line.Substring(index + 1, opening - index - 1) + "\"";
                        index = opening;
                        continue;
                    }
                }
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
            // 인코딩 및 raw 문자열 접두어는 코드 식별자 색상을 붙이지 않습니다.
            if (index + 1 < line.Length && (line[index + 1] == '"' || line[index + 1] == '\'')) continue;
            identifiers?.Add(new SourceIdentifierSpan(start, length));
            if (length == symbol.Length &&
                string.Compare(line, start, symbol, 0, length, StringComparison.Ordinal) == 0)
            {
                columns ??= new List<int>();
                if (results.Count + columns.Count < maximumResults) columns.Add(start + 1);
            }
        }

        // 역슬래시로 이어진 일반 문자열만 다음 물리 줄까지 유지합니다.
        if (!escaped) stringDelimiter = '\0';
        escaped = false;
        if (columns is null || columns.Count == 0) return;
        var leading = line.Length - line.TrimStart().Length;
        var spans = identifiers!.ConvertAll(span => new SourceIdentifierSpan(span.Start - leading, span.Length)).AsReadOnly();
        var text = line.Trim();
        foreach (var column in columns)
            results.Add(new SourceUsageLocation(symbol, path, lineNumber, column, text, spans));
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
