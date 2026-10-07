using System;
using System.Collections.Generic;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>참조 위치가 심볼의 정의인지 선언인지입니다. 근거가 없으면 <see cref="None"/>입니다.</summary>
public enum NavigationRole
{
    None,
    Declaration,
    Definition,
}

/// <summary>
/// clangd 응답만으로 참조 위치의 역할(정의·선언)을 정합니다. 결과 목록의 표시 보조이므로 근거가 확실한 위치만 표시하고 추측하지 않습니다.
/// </summary>
/// <remarks>
/// LSP 참조 응답에는 역할이 없어 다음 근거를 조합합니다(clangd 22 확인).
/// <list type="bullet">
/// <item><c>includeDeclaration</c> true 결과에서 false 결과를 뺀 위치는 clangd가 선언 또는 정의로 표시한 위치입니다.</item>
/// <item><c>textDocument/symbolInfo</c>의 <c>definitionRange</c>는 요청 파일 AST가 아는 정의이고, <c>declarationRange</c>는 그 정의가 아닌 한 정의가 아닌 선언입니다(번역 단위의 정의는 하나).</item>
/// <item>요청 위치의 <c>textDocument/definition</c>은 커서가 정의·대표 선언 위에 있으면 반대쪽으로 바뀝니다(토글). 이때는 정의·선언 요청이 같은 위치를
/// 돌려주므로, 두 응답이 각각 하나이고 서로 다를 때만 정의 응답을 정의로, 선언 응답을 선언으로 씁니다.</item>
/// </list>
/// 선언·정의 묶음의 나머지 위치는 정의를 하나라도 알 때만 선언으로 둡니다. 정의를 모르면 그 안의 정의를 가려낼 수 없기 때문입니다.
/// 같은 심볼을 구성마다 다르게 정의한 경우(플랫폼별 구현 등) 색인이 고른 정의 밖의 정의는 선언으로 보일 수 있습니다.
/// </remarks>
public static class ReferenceRoles
{
    private static readonly IReadOnlyDictionary<NavigationLocation, NavigationRole> Empty = new Dictionary<NavigationLocation, NavigationRole>();

    /// <param name="all">선언 포함 참조 결과(<c>includeDeclaration</c> true)입니다. 역할은 이 위치들에만 붙습니다.</param>
    /// <param name="uses">선언 제외 참조 결과입니다. 받지 못했거나 결과 수 제한에 걸려 두 결과를 비교할 수 없으면 null입니다.</param>
    /// <param name="astDefinition">symbolInfo의 정의 위치입니다. 없으면 null입니다.</param>
    /// <param name="astDeclaration">symbolInfo의 선언 위치입니다. 없으면 null입니다.</param>
    /// <param name="definition">요청 위치의 정의 이동 응답입니다. 받지 못했으면 null입니다.</param>
    /// <param name="declaration">요청 위치의 선언 이동 응답입니다. 받지 못했으면 null입니다.</param>
    public static IReadOnlyDictionary<NavigationLocation, NavigationRole> Classify(IReadOnlyList<NavigationLocation> all, IReadOnlyCollection<NavigationLocation>? uses,
        NavigationLocation? astDefinition, NavigationLocation? astDeclaration,
        IReadOnlyList<NavigationLocation>? definition, IReadOnlyList<NavigationLocation>? declaration)
    {
        if (all is null) throw new ArgumentNullException(nameof(all));
        var definitions = new HashSet<NavigationLocation>();
        var declarations = new HashSet<NavigationLocation>();
        if (astDefinition is not null) definitions.Add(astDefinition);
        if (definition is { Count: 1 } && declaration is { Count: 1 } && !definition[0].Equals(declaration[0]))
        {
            definitions.Add(definition[0]);
            declarations.Add(declaration[0]);
        }

        if (astDeclaration is not null) declarations.Add(astDeclaration);
        declarations.ExceptWith(definitions);

        // clangd가 일반 사용으로 돌려준 위치와 어긋나는 근거는 버립니다. 그런 정의는 "정의를 안다"는 근거로도 쓰지 않습니다.
        var plain = uses is null ? null : new HashSet<NavigationLocation>(uses);
        if (plain is not null)
        {
            definitions.ExceptWith(plain);
            declarations.ExceptWith(plain);
            if (definitions.Count > 0)
            {
                foreach (var location in all)
                {
                    if (!plain.Contains(location) && !definitions.Contains(location)) declarations.Add(location);
                }
            }
        }

        if (definitions.Count == 0 && declarations.Count == 0) return Empty;
        var result = new Dictionary<NavigationLocation, NavigationRole>();
        foreach (var location in all)
        {
            if (definitions.Contains(location)) result[location] = NavigationRole.Definition;
            else if (declarations.Contains(location)) result[location] = NavigationRole.Declaration;
        }

        return result;
    }

    /// <summary>역할 표식 글자입니다. 역할이 없으면 빈 문자열입니다.</summary>
    public static string Text(NavigationRole role) => role switch
    {
        NavigationRole.Definition => "정의",
        NavigationRole.Declaration => "선언",
        _ => string.Empty,
    };

    /// <summary>필터 글자 전체가 역할 표식 글자와 같으면 그 역할입니다. 아니면 <see cref="NavigationRole.None"/>입니다.</summary>
    public static NavigationRole FromText(string? text) => text?.Trim() switch
    {
        "정의" => NavigationRole.Definition,
        "선언" => NavigationRole.Declaration,
        _ => NavigationRole.None,
    };
}
