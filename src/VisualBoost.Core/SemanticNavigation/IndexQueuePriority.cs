using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 색인 중에 연 문서와 관련된 TU를 clangd background index 대기열 앞으로 올릴 때 쓸 대기열 표시를 정합니다.
/// </summary>
/// <remarks>
/// clangd는 대기열을 무작위로 섞고, 문서를 처음 열 때 그 파일이 헤더면 파일 이름(확장자 제외)이 같은 TU의 색인 작업을 앞으로 올립니다
/// (clangd 22 <c>BackgroundIndex::boostRelated</c>, 작업 표시는 TU 경로의 파일 이름에서 마지막 확장자를 뺀 것). 그래서 표시와 같은 이름의 빈
/// 헤더를 잠시 열면 그 TU를 앞으로 올릴 수 있습니다. 올리는 대상은 연 문서 자신(clangd는 명령을 받은 문서를 TU로 색인하며 include한 헤더
/// 전체가 함께 색인됨)과, 연 문서가 include한 헤더와 이름이 같은 cpp의 TU(그 헤더에 선언한 함수의 정의)입니다. 순서만 바꾸므로 clangd 동작이
/// 바뀌어 맞지 않게 되어도 색인 결과에는 영향이 없습니다.
/// </remarks>
public static class IndexQueuePriority
{
    /// <summary>문서 하나를 열 때 올릴 표시 수 상한입니다. include가 많은 문서가 대기열 대부분을 올려 순서가 의미를 잃지 않게 합니다.</summary>
    public const int MaxTagsPerDocument = 24;

    private static readonly Regex Include = new(@"^[ \t]*#[ \t]*include[ \t]*[""<]([^"">\r\n]+)["">]", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// 문서가 include한 파일의 이름(확장자 제외)입니다. 처음 나온 순서이며 Unreal 생성 헤더(<c>.generated.h</c>)와 파일 이름으로 쓸 수 없는 문자가
    /// 든 이름은 뺍니다. 편집 중인 문서의 깨진 include 줄에서 .NET Framework의 <see cref="Path"/> 함수가 예외를 내지 않게 경로 함수에 넘기기 전에
    /// 거릅니다(2026-10-09 검토 64).
    /// </summary>
    public static IReadOnlyList<string> IncludedStems(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var stems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Include.Matches(text))
        {
            var spelled = match.Groups[1].Value.Trim().Replace('\\', '/');
            var name = spelled.Substring(spelled.LastIndexOf('/') + 1);
            if (name.IndexOfAny(InvalidNameChars) >= 0 || name.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length > 0 && seen.Add(stem)) stems.Add(stem);
        }

        return stems;
    }

    /// <summary>TU 경로의 clangd 대기열 표시입니다(파일 이름에서 마지막 확장자를 뺀 것). clangd는 대소문자를 구분해 비교합니다.</summary>
    public static string TagOf(string translationUnit) => Path.GetFileNameWithoutExtension(translationUnit);

    /// <summary>
    /// 연 문서에 대해 올릴 표시입니다. 연 문서 자신의 표시를 먼저, 그다음 이름(헤더면 자기 이름 포함, 그 뒤 include한 파일 이름 순)마다
    /// <paramref name="relatedTags"/>가 돌려준 TU 표시를 상한까지 담습니다.
    /// </summary>
    /// <param name="relatedTags">파일 이름(확장자 제외)에 해당하는 cpp를 색인하는 TU의 표시입니다.</param>
    public static IReadOnlyList<string> TagsFor(string path, string text, Func<string, IEnumerable<string>> relatedTags)
    {
        var tags = new List<string>();
        void Add(string tag)
        {
            if (tags.Count < MaxTagsPerDocument && tag.Length > 0 && !tags.Contains(tag, StringComparer.Ordinal)) tags.Add(tag);
        }

        Add(TagOf(path));
        var stems = new List<string>();
        if (DefinitionCandidates.IsHeader(path)) stems.Add(TagOf(path));
        stems.AddRange(IncludedStems(text));
        foreach (var stem in stems)
        {
            if (tags.Count >= MaxTagsPerDocument) break;
            foreach (var tag in relatedTags(stem)) Add(tag);
        }

        return tags;
    }

    /// <summary>database의 TU가 실제 파일인 문맥(Unreal 외)의 이름별 표시입니다. 대소문자를 가리지 않고 찾아 database에 적힌 표기를 돌려줍니다.</summary>
    public static ILookup<string, string> CommandTags(IEnumerable<CompileCommand> commands) =>
        commands.Select(c => TagOf(c.File)).Distinct(StringComparer.Ordinal).ToLookup(t => t, StringComparer.OrdinalIgnoreCase);
}
