using System;

namespace VisualBoost.Services;

internal sealed class SolutionFileIndexConfiguration
{
    public static readonly SolutionFileIndexConfiguration Default = new(
        usePersistentFileCache: true,
        enableSourceAnalysis: true,
        sourceAnalysisDelay: TimeSpan.FromSeconds(2));

    public SolutionFileIndexConfiguration(
        bool usePersistentFileCache,
        bool enableSourceAnalysis,
        TimeSpan sourceAnalysisDelay)
    {
        UsePersistentFileCache = usePersistentFileCache;
        EnableSourceAnalysis = enableSourceAnalysis;
        SourceAnalysisDelay = sourceAnalysisDelay < TimeSpan.Zero
            ? TimeSpan.Zero
            : sourceAnalysisDelay;
    }

    public bool UsePersistentFileCache { get; }

    public bool EnableSourceAnalysis { get; }

    public TimeSpan SourceAnalysisDelay { get; }
}
