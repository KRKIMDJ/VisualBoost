using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Searching;

namespace VisualBoost.Services;

internal sealed class IndexedSymbolUsageProvider : ISymbolUsageProvider
{
    private const long MaximumSourceLength = 8 * 1024 * 1024;
    private static readonly HashSet<string> CppExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".inl", ".ixx", ".cppm",
    };

    public IReadOnlyList<SourceUsageLocation> FindUsages(
        string symbol,
        IReadOnlyList<string> paths,
        string? sourceProjectFile,
        SymbolUsageScope scope,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumResults <= 0 || string.IsNullOrWhiteSpace(symbol)) return Array.Empty<SourceUsageLocation>();
        var projectPaths = scope == SymbolUsageScope.CurrentProject
            ? new HashSet<string>(SourceProjectFiles.Read(
                sourceProjectFile ?? throw new InvalidOperationException("실행 문서의 소속 프로젝트를 확인할 수 없습니다."),
                paths, cancellationToken), StringComparer.OrdinalIgnoreCase)
            : null;
        var results = new ConcurrentBag<SourceUsageLocation>();
        var resultCount = 0;
        var candidates = paths.Select(path => SearchPath.TryNormalize(path, out var normalized) ? normalized : null)
            .Where(path => path is not null).Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(path =>
            CppExtensions.Contains(Path.GetExtension(path)) &&
            (scope == SymbolUsageScope.EntireSolution ||
             projectPaths!.Contains(path)));
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            // 대규모 검색 중에도 편집기와 디스크 작업에 여유를 남깁니다.
            MaxDegreeOfParallelism = Math.Min(2, Math.Max(1, Environment.ProcessorCount - 1)),
        };

        Parallel.ForEach(candidates, options, (path, loopState) =>
        {
            if (Volatile.Read(ref resultCount) >= maximumResults)
            {
                loopState.Stop();
                return;
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaximumSourceLength)
                {
                    return;
                }

                var source = File.ReadAllText(path);
                cancellationToken.ThrowIfCancellationRequested();
                // 파일 전체에 이름이 없으면 줄 단위 스캔과 표시 메타데이터 생성을 생략합니다.
                if (source.IndexOf(symbol, StringComparison.Ordinal) < 0) return;
                var matches = CppIdentifierUsageScanner.Find(
                    symbol,
                    path,
                    source,
                    maximumResults,
                    cancellationToken);
                foreach (var match in matches)
                {
                    var position = Interlocked.Increment(ref resultCount);
                    if (position <= maximumResults)
                    {
                        results.Add(match);
                    }
                    else
                    {
                        loopState.Stop();
                        break;
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                // 검색 도중 잠기거나 사라진 파일만 건너뛰고 나머지 결과는 유지합니다.
            }
        });

        return results
            .OrderBy(result => result.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Line)
            .ThenBy(result => result.Column)
            .ToArray();
    }
}
