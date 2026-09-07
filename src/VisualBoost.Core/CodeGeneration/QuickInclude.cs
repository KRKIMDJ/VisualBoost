using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.CodeGeneration;

public static class QuickInclude
{
    private static readonly Regex Include = new(@"^\s*#\s*include\s*[<""]([^>""\r\n]+)[>""]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static bool IsHeader(string path) => new[] { ".h", ".hpp", ".hh", ".hxx" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        && !path.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".gen.h", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> RankHeaders(string source, string? projectRoot, IEnumerable<string> candidates)
    {
        var sourceDirectory = Path.GetDirectoryName(source)!;
        int Distance(string path)
        {
            var left = sourceDirectory.Split('\\'); var right = Path.GetDirectoryName(path)!.Split('\\'); var common = 0;
            while (common < Math.Min(left.Length, right.Length) && string.Equals(left[common], right[common], StringComparison.OrdinalIgnoreCase)) common++;
            return left.Length + right.Length - common * 2;
        }
        return candidates.Where(IsHeader).Where(p => !string.Equals(p, source, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => string.Equals(Path.GetDirectoryName(p), sourceDirectory, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => projectRoot is not null && p.StartsWith(projectRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            .ThenBy(Distance).ThenBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string IncludePath(string source, string header, bool unrealPublicHeader = false)
    {
        if (!SearchPath.TryNormalize(source, out source) || !SearchPath.TryNormalize(header, out header) || !IsHeader(header))
            throw new GenerationNotSupportedException("유효한 소스와 일반 헤더 경로가 필요합니다.");
        if (string.Equals(source, header, StringComparison.OrdinalIgnoreCase)) throw new GenerationNotSupportedException("자기 자신은 include하지 않습니다.");
        // Unreal 모듈의 공개 헤더는 공개 include 루트 기준 경로를 사용합니다. 모듈 의존성 자체는 변경하지 않습니다.
        var portable = header.Replace('\\', '/');
        var sourceRoot = portable.IndexOf("/Source/", StringComparison.OrdinalIgnoreCase);
        if (unrealPublicHeader && sourceRoot >= 0)
        {
            foreach (var marker in new[] { "/Public/", "/Classes/" })
            {
                var start = portable.IndexOf(marker, sourceRoot + 8, StringComparison.OrdinalIgnoreCase);
                if (start >= 0) return portable.Substring(start + marker.Length);
            }
        }
        if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(header), StringComparison.OrdinalIgnoreCase))
            throw new GenerationNotSupportedException("다른 드라이브의 헤더는 상대 경로를 만들 수 없습니다. 프로젝트 include 경로를 먼저 설정하세요.");
        var folder = new Uri(Path.GetDirectoryName(source)!.TrimEnd('\\') + "\\");
        return Uri.UnescapeDataString(folder.MakeRelativeUri(new Uri(header)).ToString());
    }

    public static GenerationPlan Create(string source, string sourcePath, string headerPath, CancellationToken token = default, bool unrealPublicHeader = false)
    {
        token.ThrowIfCancellationRequested();
        if (source.Length > 8 * 1024 * 1024) throw new GenerationNotSupportedException("8MB를 넘는 편집 버퍼는 자동 include 대상에서 제외합니다.");
        var path = IncludePath(sourcePath, headerPath, unrealPublicHeader);
        if (path.IndexOfAny(new[] { '\r', '\n', '"', '<', '>' }) >= 0) throw new GenerationNotSupportedException("include에 사용할 수 없는 경로입니다.");
        var newline = source.Contains("\r\n") ? "\r\n" : "\n";
        var masked = CppSymbolDetails.Mask(source, token);
        var lines = Regex.Matches(source, @"[^\r\n]*(?:\r\n|\n|\r|$)").Cast<Match>().Where(m => m.Length > 0).ToArray();
        var code = lines.Select(l => masked.Substring(l.Index, l.Length).Trim()).ToArray();
        var significant = code.Select((s, i) => (s, i)).Where(v => v.s.Length > 0).ToArray();
        var guard = "";
        if (significant.Length >= 3)
        {
            var first = Regex.Match(significant[0].s, @"^#\s*ifndef\s+(\w+)\s*$");
            if (first.Success && Regex.IsMatch(significant[1].s, @"^#\s*define\s+" + Regex.Escape(first.Groups[1].Value) + @"\s*$")
                && Regex.IsMatch(significant[significant.Length - 1].s, @"^#\s*endif\s*$")) guard = first.Groups[1].Value;
        }
        var depth = 0; var offset = LeadingTriviaEnd(source); var preamble = true; int? generatedOffset = null;
        var generatedCount = 0; var guardEntered = false;
        var conditionalIncludes = new Stack<bool>();
        for (var i = 0; i < lines.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var line = lines[i]; var text = code[i];
            if (preamble && text.StartsWith("#", StringComparison.Ordinal) && line.Value.TrimEnd().EndsWith("\\", StringComparison.Ordinal))
                throw new GenerationNotSupportedException("여러 줄 전처리 지시문이 있는 머리말은 자동 편집하지 않습니다.");
            if (Regex.IsMatch(text, @"^(?:export\s+)?module(?:\s|;)") || Regex.IsMatch(text, @"^import\s"))
                throw new GenerationNotSupportedException("C++ module 문서는 include 위치를 자동 결정하지 않습니다.");
            var include = Regex.IsMatch(text, @"^#\s*include\b") ? Include.Match(line.Value) : Match.Empty;
            if (Regex.IsMatch(text, @"^#\s*include\b") && !include.Success)
                throw new GenerationNotSupportedException("매크로 또는 불완전한 include가 있어 중복과 순서를 확인할 수 없습니다.");
            if (include.Success)
            {
                if (preamble && conditionalIncludes.Count > 0)
                { conditionalIncludes.Pop(); conditionalIncludes.Push(true); }
                var existing = include.Groups[1].Value.Replace('\\', '/');
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, existing));
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase) || string.Equals(resolved, headerPath, StringComparison.OrdinalIgnoreCase))
                    throw new GenerationNotSupportedException("이 헤더는 이미 include되어 있습니다. 조건부 include나 기존 경로를 확인하세요.");
                if (existing.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase))
                {
                    generatedCount++;
                    if (!preamble || depth != (guardEntered ? 1 : 0)) throw new GenerationNotSupportedException("조건부 또는 본문 내부의 generated 헤더는 자동 편집하지 않습니다.");
                    generatedOffset = line.Index;
                }
                else if (generatedOffset is not null) throw new GenerationNotSupportedException("generated 헤더 뒤에 include가 이미 있습니다. 기존 순서를 먼저 정리하세요.");
                if (preamble && depth == (guardEntered ? 1 : 0)) offset = DirectiveEnd(source, line.Index + include.Length);
            }
            else if (Regex.IsMatch(text, @"^#\s*pragma\s+once\b") && preamble && depth == 0)
                offset = DirectiveEnd(source, line.Index + Regex.Match(line.Value, @"^\s*#\s*pragma\s+once\b").Length);
            else if (guard.Length > 0 && Regex.IsMatch(text, @"^#\s*define\s+" + Regex.Escape(guard) + @"\s*$") && depth == 1 && preamble)
                offset = DirectiveEnd(source, line.Index + Regex.Match(line.Value, @"^\s*#\s*define\s+" + Regex.Escape(guard) + @"\b").Length);
            else if (text.Length > 0 && !text.StartsWith("#", StringComparison.Ordinal)) preamble = false;
            if (Regex.IsMatch(text, @"^#\s*(if|ifdef|ifndef)\b"))
            {
                if (i == significant.FirstOrDefault().i && guard.Length > 0) guardEntered = true;
                depth++;
                conditionalIncludes.Push(false);
            }
            else if (Regex.IsMatch(text, @"^#\s*endif\b"))
            {
                depth--;
                var containsInclude = conditionalIncludes.Count > 0 && conditionalIncludes.Pop();
                if (containsInclude && conditionalIncludes.Count > 0)
                { conditionalIncludes.Pop(); conditionalIncludes.Push(true); }
                // 조건부 헤더 묶음 뒤에서는 조건 밖에 삽입해 플랫폼 조건을 물려받지 않습니다.
                if (preamble && containsInclude && depth == (guardEntered ? 1 : 0))
                    offset = DirectiveEnd(source, line.Index + Regex.Match(line.Value, @"^\s*#\s*endif\b").Length);
            }
            if (guardEntered && depth == 1 && Regex.IsMatch(text, @"^#\s*(else|elif)\b"))
                throw new GenerationNotSupportedException("분기가 있는 헤더 가드는 자동 편집하지 않습니다.");
            if (guardEntered && depth == 0 && i != significant[significant.Length - 1].i)
                throw new GenerationNotSupportedException("헤더 가드 바깥 코드가 있어 자동 편집하지 않습니다.");
            if (depth < 0) throw new GenerationNotSupportedException("전처리 조건문이 불균형하여 include 위치를 결정할 수 없습니다.");
        }
        if (depth != 0 || generatedCount > 1) throw new GenerationNotSupportedException("전처리 조건문 또는 generated 헤더가 불명확합니다.");
        offset = generatedOffset ?? offset;
        var prefix = offset > 0 && source[offset - 1] != '\n' && source[offset - 1] != '\r' ? newline : "";
        return new GenerationPlan(offset, prefix + "#include \"" + path + "\"" + newline, path,
            "자동 저장하지 않음 · Unreal 모듈 의존성은 별도로 확인");
    }
    private static int DirectiveEnd(string text, int offset)
    {
        // 지시문에 붙은 주석만 보존하고 다음 빈 줄·본문 설명 주석은 넘지 않습니다.
        while (offset < text.Length)
        {
            if (text[offset] == '\r') return offset + (offset + 1 < text.Length && text[offset + 1] == '\n' ? 2 : 1);
            if (text[offset] == '\n') return offset + 1;
            if (char.IsWhiteSpace(text[offset])) { offset++; continue; }
            if (offset + 1 < text.Length && text.Substring(offset, 2) == "//")
            { var end = text.IndexOfAny(new[] { '\r', '\n' }, offset); offset = end < 0 ? text.Length : end; continue; }
            if (offset + 1 < text.Length && text.Substring(offset, 2) == "/*")
            {
                var end = text.IndexOf("*/", offset + 2, StringComparison.Ordinal);
                if (end < 0) throw new GenerationNotSupportedException("머리말 주석이 닫히지 않았습니다.");
                offset = end + 2; continue;
            }
            throw new GenerationNotSupportedException("전처리 지시문 뒤에 추가 코드가 있어 include 위치를 안전하게 결정할 수 없습니다.");
        }
        return offset;
    }
    private static int LeadingTriviaEnd(string text, int offset = 0)
    {
        while (offset < text.Length)
        {
            if (char.IsWhiteSpace(text[offset]) || text[offset] == '\uFEFF') { offset++; continue; }
            if (offset + 1 < text.Length && text.Substring(offset, 2) == "//")
            { var end = text.IndexOf('\n', offset); offset = end < 0 ? text.Length : end + 1; continue; }
            if (offset + 1 < text.Length && text.Substring(offset, 2) == "/*")
            {
                var end = text.IndexOf("*/", offset + 2, StringComparison.Ordinal);
                if (end < 0) throw new GenerationNotSupportedException("머리말 주석이 닫히지 않았습니다.");
                offset = end + 2; continue;
            }
            break;
        }
        return offset;
    }
}
