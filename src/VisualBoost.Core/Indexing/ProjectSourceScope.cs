using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisualBoost.Core.Searching;

namespace VisualBoost.Core.Indexing;

/// <summary>프로젝트 등록 파일을 기준으로 새 코드 탐색 범위를 제한합니다.</summary>
public static class ProjectSourceScope
{
    public static IReadOnlyList<string> GetRoots(string projectPath, IEnumerable<string> members)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectDirectory = string.IsNullOrEmpty(projectPath) ? null : Path.GetDirectoryName(projectPath);
        if (!string.IsNullOrEmpty(projectDirectory) && !FileSystemPathCatalog.IsExcludedPath(projectDirectory!))
            roots.Add(projectDirectory!);

        foreach (var member in members.Where(CodeFilePriority.IsCode))
        {
            if (FileSystemPathCatalog.IsExcludedPath(member)) continue;
            var directory = Path.GetDirectoryName(member);
            if (string.IsNullOrEmpty(directory)) continue;
            // 생성 프로젝트는 Intermediate에 있어 실제 소스와 떨어져 있습니다.
            // Public/Private/Classes를 공유하는 모듈을 우선하고, 나머지 연결 파일은 해당 폴더만 보충합니다.
            var ancestor = new DirectoryInfo(directory);
            string? moduleRoot = null;
            while (ancestor?.Parent is not null)
            {
                if (ancestor.Name.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                    ancestor.Name.Equals("Private", StringComparison.OrdinalIgnoreCase) ||
                    ancestor.Name.Equals("Classes", StringComparison.OrdinalIgnoreCase))
                {
                    moduleRoot = ancestor.Parent.FullName;
                    break;
                }
                if (ancestor.Parent.Name.Equals("Source", StringComparison.OrdinalIgnoreCase))
                {
                    moduleRoot = ancestor.FullName;
                    break;
                }
                ancestor = ancestor.Parent;
            }
            roots.Add(moduleRoot ?? directory!);
        }

        var result = new List<string>();
        foreach (var root in roots.OrderBy(path => path.Length))
            if (!result.Any(parent => IsInside(root, parent))) result.Add(root);
        return result;
    }

    public static bool IsSupplementalCode(string path) =>
        CodeFilePriority.IsCode(path) && !FileSystemPathCatalog.IsExcludedPath(path);

    public static bool IsInside(string path, string root) =>
        path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
