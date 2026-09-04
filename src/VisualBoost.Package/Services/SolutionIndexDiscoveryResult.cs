using System;
using System.Collections.Generic;

namespace VisualBoost.Services;

internal sealed class SolutionIndexDiscoveryResult
{
    public SolutionIndexDiscoveryResult(
        string solutionPath,
        IReadOnlyList<string> searchRoots,
        IReadOnlyList<string> explicitFiles)
    {
        SolutionPath = solutionPath ?? string.Empty;
        SearchRoots = searchRoots ?? throw new ArgumentNullException(nameof(searchRoots));
        ExplicitFiles = explicitFiles ?? throw new ArgumentNullException(nameof(explicitFiles));
    }

    public string SolutionPath { get; }

    public IReadOnlyList<string> SearchRoots { get; }

    public IReadOnlyList<string> ExplicitFiles { get; }
}
