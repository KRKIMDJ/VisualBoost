using System;
using System.Collections.Generic;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Indexing;

namespace VisualBoost.Services;

internal sealed class SolutionIndexDiscoveryResult
{
    public SolutionIndexDiscoveryResult(
        string solutionPath,
        IReadOnlyList<string> searchRoots,
        IReadOnlyList<string> explicitFiles,
        IReadOnlyList<SymbolSearchScope>? symbolScopes = null,
        SourceAnalysisPriority? analysisPriority = null)
    {
        SolutionPath = solutionPath ?? string.Empty;
        SearchRoots = searchRoots ?? throw new ArgumentNullException(nameof(searchRoots));
        ExplicitFiles = explicitFiles ?? throw new ArgumentNullException(nameof(explicitFiles));
        SymbolScopes = symbolScopes;
        AnalysisPriority = analysisPriority;
    }

    public string SolutionPath { get; }

    public IReadOnlyList<string> SearchRoots { get; }

    public IReadOnlyList<string> ExplicitFiles { get; }
    public IReadOnlyList<SymbolSearchScope>? SymbolScopes { get; }

    /// <summary>프로젝트 소속·엔진 위치로 정한 소스 분석 순서입니다. 없으면 수집한 순서대로 분석합니다.</summary>
    public SourceAnalysisPriority? AnalysisPriority { get; }
}
