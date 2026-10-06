using System;
using System.Collections.Generic;
using System.Linq;

namespace VisualBoost.Core.Indexing;

/// <summary>탐색 목록의 파일 범위입니다. 선언 동일성이나 참조 분석 자료를 변경하지 않습니다.</summary>
public sealed class SymbolSearchScope
{
    private readonly Func<string, bool> includes;
    /// <summary>프로젝트 범위 ID의 앞부분입니다. 뒤에 프로젝트 파일 경로가 붙습니다.</summary>
    public const string ProjectIdPrefix = "project:";
    /// <summary>Solution에 등록된 엔진 밖 코드(프로젝트 코드) 범위의 ID입니다.</summary>
    public const string RegisteredId = "registered";
    public static SymbolSearchScope All { get; } = new("all", "전체", _ => true);
    private SymbolSearchScope(string id, string name, Func<string, bool> includes)
    { Id = id; Name = name; this.includes = includes; }
    public string Id { get; }
    public string Name { get; }
    public override string ToString() => Name;
    public bool Includes(string path) => includes(path);

    public static SymbolSearchScope Project(string projectPath, string name, IEnumerable<string> files)
    {
        var members = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return new SymbolSearchScope(ProjectIdPrefix + projectPath, "프로젝트: " + name, members.Contains);
    }

    public static IReadOnlyList<SymbolSearchScope> CreateCatalog(IEnumerable<SymbolSearchScope> projects,
        IEnumerable<string> registeredFiles, IEnumerable<string> engineRoots)
    {
        var registered = new HashSet<string>(registeredFiles, StringComparer.OrdinalIgnoreCase);
        var roots = engineRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool IsEngine(string path) => roots.Any(root => ProjectSourceScope.IsInside(path, root));
        var result = new List<SymbolSearchScope> { All,
            new(RegisteredId, "프로젝트 코드", path => registered.Contains(path) && !IsEngine(path)) };
        if (roots.Length > 0) result.Add(new SymbolSearchScope("engine", "엔진", IsEngine));
        // 미등록 파일을 외부 라이브러리로 단정하지 않습니다. 새 파일이나 수집 누락일 수도 있습니다.
        result.Add(new SymbolSearchScope("unassigned", "소속 미확인", path => !registered.Contains(path) && !IsEngine(path)));
        result.AddRange(projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
        return result.AsReadOnly();
    }
}
