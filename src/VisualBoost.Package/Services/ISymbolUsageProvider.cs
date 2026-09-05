using System.Collections.Generic;
using System.Threading;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Services;

internal interface ISymbolUsageProvider
{
    IReadOnlyList<SourceUsageLocation> FindUsages(
        string symbol,
        IReadOnlyList<string> paths,
        string? sourceProjectFile,
        SymbolUsageScope scope,
        int maximumResults,
        CancellationToken cancellationToken);
}
