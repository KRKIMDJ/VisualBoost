using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.DocumentNavigation;
using VisualBoost.Services;

namespace VisualBoost.CommentLinks;

internal sealed class CommentSymbolLinkResult
{
    internal IReadOnlyList<SourceSymbolLocation> Targets { get; set; } = Array.Empty<SourceSymbolLocation>();
    internal bool Limited { get; set; }
    internal Dictionary<string, DateTime> Stamps { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class CommentSymbolLinkResolver
{
    // 클릭 시에만 기존 이름 인덱스로 좁힌 파일을 읽습니다. 솔루션 전체 탐색이나 캐시는 만들지 않습니다.
    internal static CommentSymbolLinkResult Resolve(CommentSymbolReference reference, SolutionFileIndexService index, CancellationToken token)
        => Resolve(reference, index.FindSymbol, token);

    internal static CommentSymbolLinkResult Resolve(CommentSymbolReference reference, Func<string, IReadOnlyList<SourceSymbolLocation>> lookup, CancellationToken token)
    {
        var result = new CommentSymbolLinkResult();
        var first = reference.Name.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries)[0];
        var named = lookup(reference.ShortName);
        var direct = named.Where(s => s.Name == reference.ShortName && s.Kind != SourceSymbolKind.Namespace &&
            (CommentSymbolReferences.IsTypeOrNamespace(s.Kind) || s.Kind == SourceSymbolKind.Function && reference.Name.Contains("::")) &&
            CommentSymbolReferences.Matches(reference.Name, s.Scope.Length == 0 ? s.Name : s.Scope + "::" + s.Name)).ToArray();
        // 함수/타입 파일을 이미 찾았으면 같은 최상위 namespace의 수천 파일을 뒤지지 않습니다.
        var candidates = (direct.Length > 0 ? direct : named.Concat(first == reference.ShortName ? Array.Empty<SourceSymbolLocation>() : lookup(first))
            .Where(s => s.Kind == SourceSymbolKind.Namespace && (s.Name == reference.ShortName || s.Name == first)))
            .OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var files = 0; long bytes = 0;
        string? Read(string path)
        {
            token.ThrowIfCancellationRequested();
            if (++files > 32) { result.Limited = true; return null; }
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { result.Limited = true; return null; }
                if (info.Length > 8 * 1024 * 1024 || bytes + info.Length > 32 * 1024 * 1024) { result.Limited = true; return null; }
                bytes += info.Length;
                var stamp = info.LastWriteTimeUtc;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, true);
                var source = new System.Text.StringBuilder();
                var block = new char[8192]; int count;
                while ((count = reader.Read(block, 0, block.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (source.Length + count > 4 * 1024 * 1024) { result.Limited = true; return null; }
                    source.Append(block, 0, count);
                }
                if (File.GetLastWriteTimeUtc(path) != stamp) { result.Limited = true; return null; }
                result.Stamps[path] = stamp;
                return source.ToString();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
            { result.Limited = true; return null; }
        }
        result.Targets = CommentSymbolReferences.Resolve(reference, candidates, Read, token);
        return result;
    }
}
