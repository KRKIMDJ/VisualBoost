using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisualBoost.Core.DocumentNavigation;

public sealed class CommentFileReference
{
    public CommentFileReference(int start, int length, string path, int line) { Start = start; Length = length; Path = path; Line = line; }
    public int Start { get; }
    public int Length { get; }
    public string Path { get; }
    public int Line { get; }
}

// 호출자는 언어 서비스에서 comment로 분류된 구간만 전달합니다. URL·실행 파일을 파일 링크로 만들지 않습니다.
public static class CommentFileReferences
{
    private const string Extensions = @"(?:h|hpp|hh|hxx|inl|c|cpp|cc|cxx|cs|fs|py|js|jsx|ts|tsx|java|kt|rs|go|md|txt|json|xml|xaml|yaml|yml|ini|usf|ush)";
    private static readonly Regex Pattern = new(@"(?<![\w:/\\.-])(?:""(?<path>[^""\r\n]+\." + Extensions + @")""|(?<path>(?:[\w.-]+[/\\])*[\w.-]+\." + Extensions + @"))(?::(?<line>[0-9]+))?(?![\w:/\\]|\.[\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    public static IReadOnlyList<CommentFileReference> Parse(string comment)
    {
        if (comment.Length > 16384) return Array.Empty<CommentFileReference>();
        var result = new List<CommentFileReference>();
        foreach (Match match in Pattern.Matches(comment))
        {
            var path = match.Groups["path"].Value;
            if (path.Contains(":") || path.StartsWith("\\", StringComparison.Ordinal) || path.StartsWith("/", StringComparison.Ordinal)) continue;
            var line = 1;
            if (match.Groups["line"].Success && (!int.TryParse(match.Groups["line"].Value, out line) || line < 1)) continue;
            result.Add(new CommentFileReference(match.Index, match.Length, path, line));
        }
        return result;
    }
    public static IReadOnlyList<string> Resolve(string sourcePath, string reference, IEnumerable<string> candidates)
    {
        var normalized = reference.Replace('/', '\\');
        var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, normalized));
        var matches = candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => string.Equals(p, local, StringComparison.OrdinalIgnoreCase) ||
                (normalized.Contains("\\") ? p.EndsWith("\\" + normalized, StringComparison.OrdinalIgnoreCase) : string.Equals(Path.GetFileName(p), normalized, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var exact = matches.FirstOrDefault(p => string.Equals(p, local, StringComparison.OrdinalIgnoreCase));
        return exact is null ? matches : new[] { exact };
    }
}
