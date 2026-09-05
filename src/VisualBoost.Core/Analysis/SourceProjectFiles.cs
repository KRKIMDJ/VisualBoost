using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace VisualBoost.Core.Analysis;

public static class SourceProjectFiles
{
    public static IReadOnlyCollection<string> Read(string projectFile, IReadOnlyList<string> indexedPaths,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadProject(Path.GetFullPath(projectFile), Path.GetDirectoryName(Path.GetFullPath(projectFile))!,
            indexedPaths, files, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellationToken);
        return files;
    }

    private static void ReadProject(string projectFile, string projectDirectory, IReadOnlyList<string> indexedPaths,
        HashSet<string> files, HashSet<string> visited, CancellationToken token)
    {
        if (!visited.Add(projectFile)) return;
        var directory = Path.GetDirectoryName(projectFile)!;
        using var reader = XmlReader.Create(projectFile, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            var item = reader.LocalName;
            if (item == "Import")
            {
                var import = reader.GetAttribute("Project");
                if (import is not null && import.EndsWith(".vcxitems", StringComparison.OrdinalIgnoreCase))
                    ReadProject(Resolve(import, directory, projectDirectory), projectDirectory, indexedPaths, files, visited, token);
                continue;
            }
            if (item != "ClCompile" && item != "ClInclude" && item != "Compile" && item != "None" && item != "Content") continue;
            var include = reader.GetAttribute("Include");
            if (string.IsNullOrWhiteSpace(include)) continue;
            foreach (var value in include!.Split(';'))
            {
                var expanded = Expand(value, directory, projectDirectory);
                if (expanded.Length == 0) continue;
                if (expanded.IndexOfAny(new[] { '*', '?' }) >= 0)
                {
                    var fullPattern = Path.IsPathRooted(expanded) ? expanded : Path.Combine(directory, expanded);
                    // 와일드카드 앞의 상대 디렉터리를 정규화해야 외부 연결 파일도 일치합니다.
                    var wildcard = fullPattern.IndexOfAny(new[] { '*', '?' });
                    var separator = fullPattern.LastIndexOfAny(new[] { '/', '\\' }, wildcard);
                    fullPattern = Path.Combine(Path.GetFullPath(fullPattern.Substring(0, separator + 1)),
                        fullPattern.Substring(separator + 1));
                    var regex = new Regex("^" + Regex.Escape(fullPattern.Replace('\\', '/'))
                        .Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*")
                        .Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    foreach (var path in indexedPaths)
                    {
                        token.ThrowIfCancellationRequested();
                        if (regex.IsMatch(path.Replace('\\', '/'))) files.Add(path);
                    }
                }
                else files.Add(Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(directory, expanded)));
            }
        }
    }

    private static string Resolve(string value, string directory, string projectDirectory)
    {
        var expanded = Expand(value, directory, projectDirectory);
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(directory, expanded));
    }

    private static string Expand(string value, string directory, string projectDirectory)
    {
        var expanded = value.Replace("$(MSBuildThisFileDirectory)", directory + Path.DirectorySeparatorChar)
            .Replace("$(MSBuildProjectDirectory)", projectDirectory)
            .Replace("$(ProjectDir)", projectDirectory + Path.DirectorySeparatorChar).Trim();
        if (expanded.Contains("$("))
            throw new NotSupportedException("프로젝트 파일의 사용자 속성을 해석할 수 없습니다: " + value);
        return expanded;
    }
}
