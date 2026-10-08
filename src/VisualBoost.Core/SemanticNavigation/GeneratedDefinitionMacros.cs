using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// Unreal 빌드가 모듈마다 만드는 정의 헤더(<c>Definitions.&lt;모듈&gt;.h</c>, 모든 TU에 강제 include)의 매크로를 다룹니다.
/// 모듈 API 매크로(<c>GAME_API</c> 등)는 그 모듈과 의존하는 모듈의 정의 헤더마다 다시 정의되고, clang은 매크로를 정의 위치
/// (파일 이름과 위치)로 구분하므로 같은 이름이 정의 헤더 수만큼 다른 심볼이 됩니다. 어느 모듈의 명령으로 색인했느냐에 따라 같은 헤더의
/// 사용처가 다른 심볼로 기록되어, 한 정의로 찾으면 다른 모듈 명령으로 색인한 파일의 사용처가 빠집니다(2026-10-09 정확도 시험).
/// 참조 탐색은 같은 이름의 정의 헤더 매크로를 한 매크로로 봅니다.
/// </summary>
public static class GeneratedDefinitionMacros
{
    private static readonly Regex Usr = new(@"^c:Definitions\.[^@/\\]+\.h@\d+@macro@(?<name>[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant);

    /// <summary>clang 매크로 USR이 정의 헤더에 정의된 매크로이면 그 이름, 아니면 null입니다.</summary>
    public static string? NameOf(string? usr)
    {
        if (usr is null) return null;
        var match = Usr.Match(usr);
        return match.Success ? match.Groups["name"].Value : null;
    }

    /// <summary>두 USR이 같은 이름의 정의 헤더 매크로이거나 서로 같습니다.</summary>
    public static bool Same(string? left, string? right) =>
        left is { Length: > 0 } && (string.Equals(left, right, StringComparison.Ordinal) || NameOf(left) is { } name && name == NameOf(right));

    /// <summary>정의 헤더 파일 이름(<c>Definitions.*.h</c>)입니다.</summary>
    public static bool IsDefinitionsFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("Definitions.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".h", StringComparison.OrdinalIgnoreCase) &&
               name.Length > "Definitions..h".Length;
    }

    /// <summary>명령들이 강제 include하는 정의 헤더 경로입니다(중복 없이, 처음 나온 순서).</summary>
    public static IReadOnlyList<string> FilesIn(IEnumerable<CompileCommand> commands)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var command in commands)
        {
            foreach (var argument in command.Arguments)
            {
                if (!IsDefinitionsFile(argument)) continue;
                string full;
                try
                {
                    full = Path.GetFullPath(Path.IsPathRooted(argument) ? argument : Path.Combine(command.Directory, argument));
                }
                catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
                {
                    continue;
                }

                if (seen.Add(full)) files.Add(full);
            }
        }

        return files;
    }

    /// <summary><c>#define 이름</c> 줄에서 이름의 위치(0기반 줄·UTF-16 문자)입니다. 없으면 null입니다.</summary>
    public static (int Line, int Character)? DefineOf(string text, string name)
    {
        var pattern = new Regex(@"^[ \t]*#[ \t]*define[ \t]+(?<name>" + Regex.Escape(name) + @")\b", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var match = pattern.Match(text);
        if (!match.Success) return null;
        var index = match.Groups["name"].Index;
        var line = 0;
        var start = 0;
        for (var i = 0; i < index; i++)
        {
            if (text[i] != '\n') continue;
            line++;
            start = i + 1;
        }

        return (line, index - start);
    }
}
