using System;
using System.Collections.Generic;
using System.Linq;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 문서를 clangd로 분석하기 전에 clangd 색인 파일의 참조 기록만으로 정의 위치를 정하는 규칙입니다. 스레드 안전합니다(상태 없음).
/// </summary>
/// <remarks>
/// <para>
/// 문서의 첫 정의 이동은 거의 전부 clangd가 그 문서의 preamble(앞쪽 include, Unreal은 엔진 헤더)을 만드는 시간입니다(테스트 전용 UE 샘플
/// 2.5~4.8초, 2026-10-10). clangd는 이 결과를 프로세스 사이에 저장하지 않아 VS를 다시 열면 문서마다 다시 듭니다. 색인 파일에는 파일마다
/// 그 파일 안의 참조(심볼 ID·종류·범위)가 있으므로, 색인 뒤 바뀌지 않은 문서라면 커서 위치의 심볼과 그 정의 기록을 찾을 수 있습니다.
/// </para>
/// <para>
/// clangd가 같은 위치에 돌려줄 답과 다를 수 있는 경우는 쓰지 않고 null을 돌려 clangd로 넘깁니다: 커서에 소스에 쓰인 참조가 없거나 둘 이상의
/// 심볼이 겹침(생성자 호출의 클래스·생성자, 템플릿 의존 이름은 기록 자체가 없음), 커서가 정의 자리(clangd는 선언으로 감), 정의 기록이
/// 여럿.
/// </para>
/// </remarks>
public static class IndexedDefinitions
{
    // clangd RefKind 비트입니다(IndexedReference.Kind).
    private const byte DeclarationKind = 1;
    private const byte DefinitionKind = 2;

    /// <summary>줄의 커서 위치(바로 뒤 포함)에 있는 식별자입니다. 없으면 null입니다.</summary>
    public static string? IdentifierAt(string line, int character) => IdentifierSpanAt(line, character)?.Name;

    /// <summary>줄의 커서 위치(바로 뒤 포함)에 있는 식별자와 그 시작 문자 위치입니다. 없으면 null입니다.</summary>
    public static (string Name, int Start)? IdentifierSpanAt(string line, int character)
    {
        if (character < 0 || character > line.Length) return null;
        var start = character;
        while (start > 0 && IsIdentifierChar(line[start - 1])) start--;
        var end = character;
        while (end < line.Length && IsIdentifierChar(line[end])) end++;
        if (end == start || char.IsDigit(line[start])) return null;
        return (line.Substring(start, end - start), start);
    }

    /// <summary>
    /// 커서의 식별자(0기반 줄, 시작 LSP 문자)와 범위가 정확히 같은, 소스에 그 이름으로 쓰인 참조의 심볼 ID입니다. 심볼이 하나가 아니거나 그
    /// 자리가 정의면 null입니다.
    /// </summary>
    /// <remarks>
    /// 커서를 덮기만 하면 받던 때는, 색인 뒤 내용이 바뀌었는데 수정 시각이 보존된 파일(압축 해제, 수정 시각을 지키는 동기화)에서 같은 길이의
    /// 다른 자리 참조를 받을 수 있었습니다. 시작 열까지 같아야 씁니다(2026-10-10 검토 80).
    /// </remarks>
    /// <param name="references">문서 파일 안의 참조입니다(<see cref="ClangdIndexShards.CurrentFile"/>).</param>
    /// <param name="start">커서 식별자의 시작 문자 위치입니다(<see cref="IdentifierSpanAt"/>).</param>
    public static string? SymbolAt(IReadOnlyList<IndexedReference> references, int line, int start, string name)
    {
        string? found = null;
        foreach (var reference in references)
        {
            // 매크로 펼침 안의 참조는 매크로 이름 자리에 기록되고 소스에 쓰인 이름이 아니므로 뺍니다.
            if (!reference.Spelled || reference.Line != line || reference.EndLine != line ||
                reference.Character != start || reference.EndCharacter != start + name.Length)
            {
                continue;
            }

            if ((reference.Kind & DefinitionKind) != 0) return null;
            if (found is not null && found != reference.SymbolId) return null;
            found = reference.SymbolId;
        }

        return found;
    }

    /// <summary>
    /// 후보 파일들의 기록으로 정의 하나, 또는 정의 기록이 없을 때 선언 하나를 고릅니다. 정의·선언이 여럿이거나, 고른 기록이 소스에 그 이름으로
    /// 쓰인 자리가 아니면(매크로가 만든 정의, 예: Unreal <c>GENERATED_BODY</c>의 <c>Super</c>는 clangd가 매크로 정의로 감) 둘 다 null입니다.
    /// </summary>
    public static (IndexedReference? Definition, IndexedReference? Declaration) Resolve(string symbolId, IEnumerable<IReadOnlyList<IndexedReference>> files)
    {
        var (definitions, declarations) = Occurrences(symbolId, files);
        if (definitions.Count == 1) return definitions[0].Spelled ? (definitions[0], null) : (null, null);
        return definitions.Count == 0 && declarations.Count == 1 && declarations[0].Spelled ? (null, declarations[0]) : (null, null);
    }

    /// <summary>후보 파일들의 참조에서 그 심볼의 정의·선언(정의가 아닌 것) 위치를 모읍니다. 같은 위치는 하나로 칩니다.</summary>
    public static (IReadOnlyList<IndexedReference> Definitions, IReadOnlyList<IndexedReference> Declarations) Occurrences(string symbolId,
        IEnumerable<IReadOnlyList<IndexedReference>> files)
    {
        var definitions = new List<IndexedReference>();
        var declarations = new List<IndexedReference>();
        foreach (var references in files)
        {
            foreach (var reference in references)
            {
                if (reference.SymbolId != symbolId) continue;
                if ((reference.Kind & DefinitionKind) != 0) AddDistinct(definitions, reference);
                else if ((reference.Kind & DeclarationKind) != 0) AddDistinct(declarations, reference);
            }
        }

        return (definitions, declarations);
    }

    /// <summary>커서의 식별자 바로 앞에 쓴 한정자(<c>X::</c>의 X)입니다. 없거나 Unreal의 <c>Super</c>·<c>ThisClass</c>처럼 소속을 알려 주지 않으면 null입니다.</summary>
    public static string? QualifierAt(string line, int character)
    {
        if (character < 0 || character > line.Length) return null;
        var start = character;
        while (start > 0 && IsIdentifierChar(line[start - 1])) start--;
        var match = Qualifier.Match(line.Substring(0, start));
        return match.Success && match.Groups[1].Value is var qualifier && qualifier != "Super" && qualifier != "ThisClass" ? qualifier : null;
    }

    /// <summary>
    /// 정의 기록을 찾아볼 파일입니다(최대 <paramref name="maximum"/>개). 흔한 이름은 이름 인덱스에 수백 곳이 있어(Unreal의 <c>BeginPlay</c>) 정의·선언이
    /// 있을 만한 곳을 앞에 둡니다: 문서 자신 → 이름 인덱스가 아는 파일 중 문서가 직접 include한 것 → 2단계로 include한 것 → 소속이 한정자와 같은 것 →
    /// 나머지 직접 include(이름 인덱스가 놓친 정의) → 엔진이 아닌 파일 → 엔진 파일. 같은 순위 안에서는 이름 인덱스 순서입니다.
    /// </summary>
    /// <param name="named">이름 인덱스에서 그 이름으로 찾은 위치입니다.</param>
    /// <param name="direct">문서가 직접 include한 파일입니다.</param>
    /// <param name="near">직접 include한 파일이 다시 include한 파일입니다.</param>
    public static IReadOnlyList<string> CandidateFiles(string document, IEnumerable<SourceSymbolLocation> named, string? qualifier,
        IEnumerable<string> direct, IEnumerable<string> near, Func<string, bool> isEngine, int maximum)
    {
        var directSet = new HashSet<string>(direct.Select(Key), StringComparer.OrdinalIgnoreCase);
        var nearSet = new HashSet<string>(near.Select(Key), StringComparer.OrdinalIgnoreCase);
        var ranks = new Dictionary<string, (int Rank, int Order, string Path)>(StringComparer.OrdinalIgnoreCase) { [Key(document)] = (0, 0, document) };
        void Offer(string path, int rank)
        {
            var key = Key(path);
            if (!ranks.TryGetValue(key, out var known) || rank < known.Rank) ranks[key] = (rank, known.Path is null ? ranks.Count : known.Order, path);
        }

        foreach (var symbol in named)
        {
            var key = Key(symbol.Path);
            var scoped = qualifier is not null && (symbol.Scope == qualifier || symbol.Scope.EndsWith("::" + qualifier, StringComparison.Ordinal));
            Offer(symbol.Path, directSet.Contains(key) ? 1 : nearSet.Contains(key) ? 2 : scoped ? 3 : isEngine(symbol.Path) ? 6 : 5);
        }

        foreach (var path in direct) Offer(path, 4);
        return ranks.Values.OrderBy(r => r.Rank).ThenBy(r => r.Order).Select(r => r.Path).Take(Math.Max(1, maximum)).ToArray();
    }

    private static readonly System.Text.RegularExpressions.Regex Qualifier = new(@"\b([A-Za-z_]\w*)\s*::\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string Key(string path) => path.Replace('/', '\\');

    private static void AddDistinct(List<IndexedReference> list, IndexedReference reference)
    {
        if (!list.Any(r => r.Line == reference.Line && r.Character == reference.Character &&
                           string.Equals(r.Path, reference.Path, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(reference);
        }
    }

    private static bool IsIdentifierChar(char c) => c == '_' || char.IsLetterOrDigit(c);
}
