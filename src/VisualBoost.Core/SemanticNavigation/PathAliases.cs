using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// junction·심볼릭 링크·subst 드라이브를 거쳐 연 작업 영역에서 clangd와 주고받는 경로를 실제 경로로 맞춥니다.
/// </summary>
/// <remarks>
/// clangd는 TU 본 파일의 색인 조각을 compilation database의 경로로 기록하지만, 그 안의 참조 위치는 링크를 푼 실제 경로로 기록합니다.
/// 둘이 다르면 본 파일의 참조가 background index에서 빠지고, 열린 문서의 위치는 받은 경로·색인 위치는 실제 경로로 와서 중복됩니다.
/// 그래서 clangd에는 실제 경로만 보내고(<see cref="ToReal(string)"/>), 받은 위치는 작업 영역을 연 경로로 되돌립니다(<see cref="ToGiven"/>).
/// 대응은 세션을 시작할 때 작업 영역·엔진 루트에서만 정하고 이후 바꾸지 않습니다. 열린 문서의 URI가 세션 중에 바뀌면 clangd 문서 상태가 어긋나기 때문입니다.
/// 루트 아래에 다른 링크가 또 있으면 그 하위 경로는 맞추지 않습니다.
/// </remarks>
public sealed class PathAliases
{
    private readonly (string Real, string Given)[] aliases;

    private PathAliases((string Real, string Given)[] aliases)
    {
        this.aliases = aliases;
    }

    /// <summary>링크가 없는 작업 영역입니다. 경로를 바꾸지 않습니다.</summary>
    public static PathAliases None { get; } = new(Array.Empty<(string, string)>());

    /// <summary>대응 수입니다. 링크를 거치지 않은 작업 영역이면 0입니다.</summary>
    public int Count => aliases.Length;

    /// <summary>각 루트 폴더의 실제 경로가 연 경로와 다르면 대응으로 둡니다.</summary>
    /// <param name="resolve">폴더의 실제 경로 조회입니다. 테스트에서 바꿀 수 있고, 기본은 Windows 파일 시스템 조회입니다.</param>
    public static PathAliases ForRoots(IEnumerable<string?> roots, Func<string, string?>? resolve = null)
    {
        resolve ??= RealPath;
        var found = new List<(string Real, string Given)>();
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            string given;
            try
            {
                given = Trim(Path.GetFullPath(root));
            }
            catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
            {
                continue;
            }

            // 드라이브 루트는 'P:'만으로는 그 드라이브의 현재 폴더를 뜻하므로 구분자를 붙여 조회합니다.
            var real = resolve(given.Length == 2 && given[1] == ':' ? given + Path.DirectorySeparatorChar : given) is { } resolved ? Trim(resolved) : null;
            if (real is null || string.Equals(real, given, StringComparison.OrdinalIgnoreCase)) continue;
            if (found.Any(alias => string.Equals(alias.Given, given, StringComparison.OrdinalIgnoreCase))) continue;
            found.Add((real, given));
        }

        return found.Count == 0 ? None : new PathAliases(found.ToArray());
    }

    /// <summary>clangd에 보낼 경로입니다. 연 경로 아래면 실제 경로로 바꿉니다.</summary>
    public string ToReal(string path) => Replace(path, toReal: true);

    /// <summary>clangd가 돌려준 경로입니다. 실제 경로 아래면 연 경로로 바꿉니다.</summary>
    public string ToGiven(string path) => Replace(path, toReal: false);

    /// <summary>compilation database에 쓸 명령입니다. 파일과 작업 폴더만 바꾸고 인자는 그대로 둡니다.</summary>
    public IReadOnlyList<CompileCommand> ToReal(IReadOnlyList<CompileCommand> commands) =>
        aliases.Length == 0 ? commands : commands.Select(c => new CompileCommand(ToReal(c.Directory), ToReal(c.File), c.Arguments)).ToArray();

    /// <summary>위치 경로를 연 경로로 바꾸고, 그 결과 같아진 위치는 처음 것 하나만 남깁니다. 순서는 유지합니다.</summary>
    public IReadOnlyList<NavigationLocation> ToGiven(IReadOnlyList<NavigationLocation> locations)
    {
        if (aliases.Length == 0) return locations;
        return locations.Select(location =>
        {
            var path = ToGiven(location.Path);
            return ReferenceEquals(path, location.Path)
                ? location
                : new NavigationLocation(path, location.Line, location.Character, location.EndLine, location.EndCharacter, location.Container);
        }).Distinct().ToArray();
    }

    private string Replace(string path, bool toReal)
    {
        if (aliases.Length == 0) return path;
        // compilation database는 '/' 구분자를 쓰기도 하므로 비교만 '\'로 맞추고, 바꾼 앞부분은 원래 구분자 모양을 따릅니다.
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        // 중첩된 루트가 있으면 가장 긴 접두사를 씁니다.
        var best = -1;
        var bestLength = -1;
        for (var i = 0; i < aliases.Length; i++)
        {
            var from = toReal ? aliases[i].Given : aliases[i].Real;
            if (from.Length > bestLength && IsUnder(normalized, from))
            {
                best = i;
                bestLength = from.Length;
            }
        }

        if (best < 0) return path;
        var to = toReal ? aliases[best].Real : aliases[best].Given;
        if (path.IndexOf(Path.AltDirectorySeparatorChar) >= 0 && path.IndexOf(Path.DirectorySeparatorChar) < 0)
        {
            to = to.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return to + path.Substring(bestLength);
    }

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
        (path.Length == root.Length || path[root.Length] == Path.DirectorySeparatorChar);

    /// <summary>끝 구분자를 모두 뗍니다. subst 드라이브 루트(<c>P:\</c>)도 <c>P:</c>로 두어 접두사 뒤가 항상 구분자이게 합니다.</summary>
    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 폴더 핸들의 최종 경로입니다. junction·심볼릭 링크와 subst 드라이브를 모두 풉니다. 열 수 없으면 null입니다.
    /// </summary>
    private static string? RealPath(string directory)
    {
        const uint FileFlagBackupSemantics = 0x02000000;
        const uint ShareAll = 0x7;
        const uint OpenExisting = 3;
        using var handle = CreateFile(directory, 0, ShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.Capacity = (int)length + 1;
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }

        if (length == 0 || length >= buffer.Capacity) return null;
        var path = buffer.ToString();
        // 결과는 \\?\C:\... 또는 \\?\UNC\server\share\... 형태입니다.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
