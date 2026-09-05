using System;
using System.IO;

namespace VisualBoost.Core.Analysis;

public static class SearchPath
{
    public static bool TryNormalize(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value!.Trim();
        // 탐색기의 '경로 복사'와 호스트가 반환한 한 쌍의 따옴표만 제거합니다.
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[candidate.Length - 1] == '"')
            candidate = candidate.Substring(1, candidate.Length - 2).Trim();
        if (candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
            candidate = uri.LocalPath;
        }
        candidate = candidate.Replace('/', '\\');
        var drive = candidate.Length >= 3 && char.IsLetter(candidate[0]) && candidate[1] == ':' && candidate[2] == '\\';
        var unc = candidate.StartsWith("\\\\", StringComparison.Ordinal);
        if (!drive && !unc) return false;
        for (var i = 0; i < candidate.Length; i++)
        {
            var c = candidate[i];
            if (c < 32 || c is '"' or '<' or '>' or '|' or '*' or '?' || (c == ':' && !(drive && i == 1))) return false;
        }
        if (unc)
        {
            var parts = candidate.Substring(2).Split('\\');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]) ||
                parts[0] is "." or ".." || parts[1] is "." or "..") return false;
        }
        try { path = Path.GetFullPath(candidate); return true; }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        { return false; }
    }

    public static string? DirectoryOf(string? value) => TryNormalize(value, out var path) ? Path.GetDirectoryName(path) : null;
}
