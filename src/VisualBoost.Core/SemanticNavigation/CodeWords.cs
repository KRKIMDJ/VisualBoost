using System;
using System.Collections.Generic;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>C++ 소스에서 찾은 이름 하나의 위치입니다. 좌표는 0기반 줄과 UTF-16 문자 위치입니다.</summary>
public readonly struct CodeWord
{
    public CodeWord(int line, int character, bool directive)
    {
        Line = line;
        Character = character;
        Directive = directive;
    }

    public int Line { get; }

    public int Character { get; }

    /// <summary>전처리 줄(<c>#define</c> 본문 등, 줄 이음 포함) 안입니다.</summary>
    public bool Directive { get; }
}

/// <summary>
/// C++ 소스에서 주석·문자열·문자 상수 밖에 쓰인 이름을 줄 단위로 찾습니다. clangd 결과를 보완할 후보를 고를 때 씁니다.
/// </summary>
/// <remarks>
/// 블록 주석은 줄을 넘어 이어 보고, 원시 문자열(<c>R"x(…)x"</c>)은 같은 줄 안에서 끝나는 경우만 다룹니다(아니면 줄 끝까지 건너뜀).
/// 조건부 컴파일의 활성 여부는 알지 못하므로 호출자가 다른 근거로 확인해야 합니다.
/// </remarks>
public static class CodeWords
{
    public static IEnumerable<CodeWord> Find(string text, string word)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (string.IsNullOrEmpty(word)) yield break;
        var lines = text.Split('\n');
        var inBlock = false;
        var continued = false;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.EndsWith("\r", StringComparison.Ordinal)) line = line.Substring(0, line.Length - 1);
            var directive = continued || !inBlock && line.TrimStart().StartsWith("#", StringComparison.Ordinal);
            continued = directive && line.EndsWith("\\", StringComparison.Ordinal);
            if (line.IndexOf(word, StringComparison.Ordinal) < 0 && !inBlock && line.IndexOf("/*", StringComparison.Ordinal) < 0) continue;
            var i = 0;
            while (i < line.Length)
            {
                if (inBlock)
                {
                    var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        i = line.Length;
                        break;
                    }

                    inBlock = false;
                    i = end + 2;
                    continue;
                }

                var c = line[i];
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    inBlock = true;
                    i += 2;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    i = SkipLiteral(line, i);
                    continue;
                }

                if (char.IsDigit(c))
                {
                    // 숫자 상수(접미사·자릿수 구분자 포함)
                    i++;
                    while (i < line.Length && (IsWordChar(line[i]) || line[i] == '.' || line[i] == '\'' && i + 1 < line.Length && char.IsLetterOrDigit(line[i + 1]))) i++;
                    continue;
                }

                if (IsWordChar(c))
                {
                    var start = i;
                    while (i < line.Length && IsWordChar(line[i])) i++;
                    if (i - start == word.Length && string.CompareOrdinal(line, start, word, 0, word.Length) == 0) yield return new CodeWord(lineIndex, start, directive);
                    continue;
                }

                i++;
            }
        }
    }

    /// <summary>
    /// 조건부 컴파일 지시문(<c>#if</c>·<c>#ifdef</c>·<c>#ifndef</c>·<c>#elif</c>·<c>#else</c>·<c>#endif</c>) 줄 번호입니다. 두 지시문 사이의 줄은
    /// 활성 여부가 같습니다.
    /// </summary>
    public static IReadOnlyList<int> ConditionalLines(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var result = new List<int>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            var keyword = trimmed.Substring(1).TrimStart();
            var length = 0;
            while (length < keyword.Length && char.IsLetter(keyword[length])) length++;
            switch (keyword.Substring(0, length))
            {
                case "if":
                case "ifdef":
                case "ifndef":
                case "elif":
                case "elifdef":
                case "elifndef":
                case "else":
                case "endif":
                    result.Add(i);
                    break;
            }
        }

        return result;
    }

    private static int SkipLiteral(string line, int i)
    {
        var quote = line[i];
        if (quote == '"' && i > 0 && line[i - 1] == 'R')
        {
            var open = line.IndexOf('(', i);
            var delimiter = open > i ? line.Substring(i + 1, open - i - 1) : string.Empty;
            var close = open > i ? line.IndexOf(")" + delimiter + "\"", open, StringComparison.Ordinal) : -1;
            return close < 0 ? line.Length : close + delimiter.Length + 2;
        }

        i++;
        while (i < line.Length && line[i] != quote)
        {
            if (line[i] == '\\') i++;
            i++;
        }

        return i + 1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
