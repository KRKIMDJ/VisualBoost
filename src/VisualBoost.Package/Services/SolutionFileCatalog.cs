using System.Collections.Generic;
using System.Threading;
using VisualBoost.Core.Indexing;

namespace VisualBoost.Services;

internal static class SolutionFileCatalog
{
    public static IReadOnlyList<string> GetFiles(
        IEnumerable<string> searchRoots,
        CancellationToken cancellationToken) =>
        FileSystemPathCatalog.GetFiles(searchRoots, cancellationToken);

    public static bool IsExcludedPath(string path) =>
        FileSystemPathCatalog.IsExcludedPath(path);
}
