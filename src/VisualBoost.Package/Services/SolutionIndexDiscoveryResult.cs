using System;
using System.Collections.Generic;
using VisualBoost.Core.Indexing;

namespace VisualBoost.Services;

internal sealed class SolutionIndexDiscoveryResult
{
    public SolutionIndexDiscoveryResult(
        string solutionPath,
        IReadOnlyList<string> searchRoots,
        IReadOnlyList<string> explicitFiles,
        IReadOnlyList<SymbolSearchScope>? symbolScopes = null)
    {
        SolutionPath = solutionPath ?? string.Empty;
        SearchRoots = searchRoots ?? throw new ArgumentNullException(nameof(searchRoots));
        ExplicitFiles = explicitFiles ?? throw new ArgumentNullException(nameof(explicitFiles));
        SymbolScopes = symbolScopes;
    }

    public string SolutionPath { get; }

    public IReadOnlyList<string> SearchRoots { get; }

    public IReadOnlyList<string> ExplicitFiles { get; }
    public IReadOnlyList<SymbolSearchScope>? SymbolScopes { get; }
}
