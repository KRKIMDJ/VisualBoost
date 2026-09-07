using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.CodeGeneration;

public static class QuickIncludeHeaderLookup
{
    public static bool Matches(SourceSymbolLocation location, string symbol, string owner) =>
        location.Name == symbol && (owner.Length == 0 || location.Scope == owner || location.Scope.EndsWith("::" + owner, StringComparison.Ordinal));

    public static IReadOnlyList<string> Find(string sourcePath, string? projectRoot, string symbol, string owner,
        IEnumerable<SourceSymbolLocation> indexedSymbols, IEnumerable<string> namedFiles,
        Func<string, string?> readHeader, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var indexed = QuickInclude.RankHeaders(sourcePath, projectRoot, indexedSymbols.Where(s => Matches(s, symbol, owner)).Select(s => s.Path));
        if (indexed.Count > 0) return indexed;

        // 생성/저장 직후 심볼 인덱스에 없더라도 동명 헤더의 실제 선언만 제한적으로 확인합니다.
        // 파일명만으로 include하거나 솔루션 전체를 재분석하지 않습니다.
        var nearby = new[] { ".h", ".hpp", ".hh", ".hxx" }.Select(ext => Path.Combine(Path.GetDirectoryName(sourcePath)!, symbol + ext));
        var candidates = QuickInclude.RankHeaders(sourcePath, projectRoot, namedFiles.Concat(nearby));
        var remaining = 4 * 1024 * 1024;
        foreach (var path in candidates.Take(16))
        {
            token.ThrowIfCancellationRequested();
            var source = readHeader(path);
            if (source is null || source.Length > 1024 * 1024 || source.Length > remaining) continue;
            remaining -= source.Length;
            var analysis = CppSourceAnalyzer.Analyze(path, source, token);
            if (analysis.Symbols.Any(s => Matches(s, symbol, owner))) return new[] { path };
        }
        return Array.Empty<string>();
    }
}
