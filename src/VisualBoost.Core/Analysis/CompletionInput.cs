using System;

namespace VisualBoost.Core.Analysis;

public static class CompletionInput
{
    public static bool TryGetPrefix(string line, int caret, int minimumLength, out int start, out string prefix)
    {
        start = caret;
        prefix = string.Empty;
        if (caret < 0 || caret > line.Length || (caret < line.Length && IsPart(line[caret]))) return false;
        while (start > 0 && IsPart(line[start - 1])) start--;
        if (caret - start < Math.Max(3, minimumLength) || caret - start > 128 ||
            !(line[start] == '_' || char.IsLetter(line[start]))) return false;
        var before = start - 1;
        while (before >= 0 && char.IsWhiteSpace(line[before])) before--;
        if (before >= 0 && (line[before] is '.' or ':' or '>' or '#')) return false;
        var context = line.Substring(0, start);
        // 정밀 멤버 추론, 문자열 및 전처리 입력에 전역 이름을 끼워 넣지 않습니다.
        if (context.TrimStart().StartsWith("#", StringComparison.Ordinal) || context.Contains("//") ||
            context.Contains("\"") || context.Contains("'") || context.Contains("/*")) return false;
        prefix = line.Substring(start, caret - start);
        return true;
    }

    public static bool IsPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
