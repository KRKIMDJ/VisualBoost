using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 색인하지 않은 엔진 cpp에서 정의를 확정할 때 열어 볼 후보 파일을 고릅니다.
/// </summary>
/// <remarks>
/// 후보 하나를 확인하는 데 대형 TU 분석(수 초)이 들므로, 이름 인덱스에서 같은 이름·소속의 함수가 있는 cpp를
/// 먼저 고르고 헤더와 이름이 같은 cpp를 보충합니다. 소속이 다른 같은 이름 함수는 제외합니다.
/// </remarks>
public static class DefinitionCandidates
{
    private static readonly HashSet<string> HeaderExtensions = new(StringComparer.OrdinalIgnoreCase) { ".h", ".hh", ".hpp", ".hxx", ".inl", ".ipp" };
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cpp", ".cc", ".cxx", ".c" };
    private static readonly Regex TypeOrMacroLine = new(
        @"^\s*(template\s*<.*>\s*)?((class|struct|union|enum|namespace|typedef|using)\b|#\s*define\b)",
        RegexOptions.CultureInvariant);

    public static bool IsHeader(string path) => HeaderExtensions.Contains(Path.GetExtension(path));

    public static bool IsSource(string path) => SourceExtensions.Contains(Path.GetExtension(path));

    /// <summary>위치의 줄이 타입·별칭·매크로 정의처럼 보이면 헤더가 곧 정의이므로 cpp를 찾지 않습니다.</summary>
    public static bool LooksLikeTypeOrMacro(string lineText) => TypeOrMacroLine.IsMatch(lineText);

    /// <param name="name">찾는 함수의 이름(소속 제외).</param>
    /// <param name="container">clangd가 알려 준 소속(<c>UPackage</c>, <c>UE::Foo::</c> 등). 없으면 빈 문자열.</param>
    /// <param name="headerPath">선언이 있는 헤더.</param>
    /// <param name="symbols">이름 인덱스에서 <paramref name="name"/>으로 찾은 위치.</param>
    /// <param name="sameStemFiles">헤더와 파일 이름이 같은 파일들.</param>
    public static IReadOnlyList<string> Select(string name, string container, string headerPath, IEnumerable<SourceSymbolLocation> symbols,
        IEnumerable<string> sameStemFiles, int max)
    {
        var owner = LastSegment(container);
        var headerDirectory = Path.GetDirectoryName(Path.GetFullPath(headerPath)) ?? string.Empty;
        var ranked = new List<(string Path, int Rank, int Shared)>();
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != SourceSymbolKind.Function || !IsSource(symbol.Path) || LastSegment(symbol.Name) != name)
            {
                continue;
            }

            var scope = LastSegment(symbol.Scope.Length > 0 ? symbol.Scope : Qualifier(symbol.Name));
            int rank;
            if (owner.Length == 0)
            {
                rank = scope.Length == 0 ? 0 : 2;
            }
            else if (string.Equals(scope, owner, StringComparison.Ordinal))
            {
                rank = 0;
            }
            else if (scope.Length == 0)
            {
                rank = 1;
            }
            else
            {
                // 다른 클래스의 같은 이름 함수는 확인 비용만 늘립니다.
                continue;
            }

            ranked.Add((symbol.Path, rank, SharedPrefix(headerDirectory, symbol.Path)));
        }

        var stem = Path.GetFileNameWithoutExtension(headerPath);
        foreach (var file in sameStemFiles)
        {
            if (IsSource(file) && string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase))
            {
                ranked.Add((file, 3, SharedPrefix(headerDirectory, file)));
            }
        }

        return ranked
            .OrderBy(r => r.Rank)
            .ThenByDescending(r => r.Shared)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(r => Path.GetFullPath(r.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToArray();
    }

    private static string LastSegment(string name)
    {
        var trimmed = name.TrimEnd(':');
        var index = trimmed.LastIndexOf("::", StringComparison.Ordinal);
        return index < 0 ? trimmed : trimmed.Substring(index + 2);
    }

    private static string Qualifier(string name)
    {
        var index = name.LastIndexOf("::", StringComparison.Ordinal);
        return index < 0 ? string.Empty : name.Substring(0, index);
    }

    /// <summary>같은 모듈 안의 후보를 앞세우기 위한 공통 경로 길이입니다.</summary>
    private static int SharedPrefix(string directory, string path)
    {
        var other = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var length = Math.Min(directory.Length, other.Length);
        var shared = 0;
        for (var i = 0; i < length && char.ToUpperInvariant(directory[i]) == char.ToUpperInvariant(other[i]); i++)
        {
            shared++;
        }

        return shared;
    }
}
