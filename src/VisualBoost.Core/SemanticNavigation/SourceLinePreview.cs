using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>결과 목록에 보여 줄 위치별 코드 한 줄을 읽습니다.</summary>
public static class SourceLinePreview
{
    private const long MaxFileBytes = 32L * 1024 * 1024;

    /// <param name="currentText">편집기에 열린 문서의 현재 내용. 없으면 null을 돌려주어 디스크에서 읽게 합니다.</param>
    /// <returns>입력 순서와 같은 줄 텍스트. 읽을 수 없으면 빈 문자열입니다.</returns>
    public static IReadOnlyList<string> Load(IReadOnlyList<NavigationLocation> locations, Func<string, string?>? currentText,
        CancellationToken cancellationToken = default, int maxLength = 240) =>
        LoadLines(locations, currentText, cancellationToken).Select(line => Shorten(line.Trim(), maxLength)).ToArray();

    /// <summary>위치의 원래 줄(들여쓰기 포함)을 읽습니다. 문자 위치로 일치 구간을 강조할 때 씁니다.</summary>
    public static IReadOnlyList<string> LoadLines(IReadOnlyList<NavigationLocation> locations, Func<string, string?>? currentText,
        CancellationToken cancellationToken = default)
    {
        var result = new string[locations.Count];
        foreach (var group in Enumerable.Range(0, locations.Count).GroupBy(i => locations[i].Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = currentText?.Invoke(group.Key) ?? ReadText(group.Key);
            var lines = text is null ? Array.Empty<string>() : SplitLines(text);
            foreach (var index in group)
            {
                var line = locations[index].Line;
                result[index] = line >= 0 && line < lines.Length ? lines[line] : string.Empty;
            }
        }

        return result;
    }

    /// <summary>BOM을 따르고 없으면 UTF-8로 읽습니다. 너무 크거나 읽을 수 없으면 null입니다.</summary>
    public static string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }

            using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 편집기 글이 디스크 내용과 같은지(저장하지 않은 편집이 없는지) 봅니다. 디스크는 편집기처럼 BOM을 따르고, 없으면 UTF-8로 읽되 UTF-8이
    /// 아니면 <paramref name="fallback"/>(기본은 시스템 ANSI 코드 페이지)으로 읽습니다. <see cref="ReadText"/>만으로 비교하면 BOM 없는 CP949
    /// 문서는 디스크 글이 U+FFFD로 바뀌어 늘 달라, 그 문서의 보충·PCH 기억과 색인 파일 지름길이 꺼졌습니다(2026-10-10 검토 84).
    /// </summary>
    public static bool SameAsDisk(string text, string path, Encoding? fallback = null)
    {
        var disk = ReadText(path);
        if (disk is null) return false;
        if (string.Equals(disk, text, StringComparison.Ordinal)) return true;
        // UTF-8로 읽지 못한 바이트가 없었으면 다른 해석은 없습니다. 흔한 경우는 파일을 다시 읽지 않습니다.
        if (disk.IndexOf('\uFFFD') < 0) return false;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (HasByteOrderMark(bytes)) return false;
            return string.Equals((fallback ?? Encoding.Default).GetString(bytes), text, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasByteOrderMark(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ||
        bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF);

    public static string LineAt(string text, int line)
    {
        var lines = SplitLines(text);
        return line >= 0 && line < lines.Length ? lines[line] : string.Empty;
    }

    private static string[] SplitLines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static string Shorten(string text, int maxLength) =>
        text.Length <= maxLength ? text.Replace('\t', ' ') : text.Substring(0, maxLength).Replace('\t', ' ') + "…";
}
