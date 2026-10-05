using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 의미 공급자가 돌려준 문서 범위입니다. 줄은 0부터, 열은 UTF-16 코드 단위입니다.
/// VS 텍스트 스냅샷의 위치 단위와 같으므로 편집기 좌표로 그대로 옮길 수 있습니다.
/// </summary>
public sealed class NavigationLocation : IEquatable<NavigationLocation>
{
    public NavigationLocation(string path, int line, int character, int endLine, int endCharacter)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Line = line;
        Character = character;
        EndLine = endLine;
        EndCharacter = endCharacter;
    }

    public string Path { get; }

    public int Line { get; }

    public int Character { get; }

    public int EndLine { get; }

    public int EndCharacter { get; }

    public bool Equals(NavigationLocation? other) =>
        other is not null &&
        string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
        Line == other.Line && Character == other.Character;

    public override bool Equals(object? obj) => Equals(obj as NavigationLocation);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Path) ^ (Line * 397) ^ Character;

    public override string ToString() => $"{Path}:{Line + 1}:{Character + 1}";

    /// <summary>LSP Location, Location[], LocationLink[] 응답을 같은 모델로 바꿉니다.</summary>
    public static IReadOnlyList<NavigationLocation> FromLsp(JsonValue result)
    {
        if (result.IsNull)
        {
            return Array.Empty<NavigationLocation>();
        }

        var items = result.Kind == JsonKind.Array ? result.Items : new[] { result };
        var locations = new List<NavigationLocation>(items.Count);
        foreach (var item in items)
        {
            var uri = item["uri"].AsString() ?? item["targetUri"].AsString();
            var range = item["range"].IsNull ? item["targetSelectionRange"] : item["range"];
            if (uri is null || range.IsNull || DocumentUri.ToPath(uri) is not string path)
            {
                continue;
            }

            locations.Add(new NavigationLocation(path,
                range["start"]["line"].AsInt32() ?? 0, range["start"]["character"].AsInt32() ?? 0,
                range["end"]["line"].AsInt32() ?? 0, range["end"]["character"].AsInt32() ?? 0));
        }

        return locations;
    }

    /// <summary>같은 위치를 하나로 합치고 경로·줄·열 순서로 정렬합니다.</summary>
    public static IReadOnlyList<NavigationLocation> Normalize(IEnumerable<NavigationLocation> locations) =>
        locations.Distinct()
            .OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Line)
            .ThenBy(l => l.Character)
            .ToArray();
}

/// <summary>Windows 파일 경로와 LSP <c>file:</c> URI를 변환합니다.</summary>
public static class DocumentUri
{
    /// <summary>
    /// 드라이브 문자와 경로 대소문자를 보존해 URI를 만듭니다. 대소문자를 바꾸면 clangd가 열린 문서와
    /// background index의 같은 파일을 서로 다른 URI로 다뤄 결과가 중복된 사례가 있었습니다.
    /// </summary>
    public static string FromPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path).Replace('\\', '/');
        var builder = new StringBuilder("file:///");
        var segments = full.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0) builder.Append('/');
            var segment = segments[i];
            if (i == 0 && segment.Length == 2 && segment[1] == ':')
            {
                builder.Append(segment);
                continue;
            }

            builder.Append(Uri.EscapeDataString(segment));
        }

        return builder.ToString();
    }

    /// <summary><c>file:</c>가 아닌 URI는 null입니다. clangd가 소문자로 보낸 드라이브 문자는 대문자로 맞춥니다.</summary>
    public static string? ToPath(string uri)
    {
        if (!uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = Uri.UnescapeDataString(uri.Substring("file:///".Length)).Replace('/', System.IO.Path.DirectorySeparatorChar);
        if (path.Length >= 2 && path[1] == ':')
        {
            path = char.ToUpperInvariant(path[0]) + path.Substring(1);
        }

        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            return null;
        }
    }
}
