using VisualBoost.Core.Analysis;

namespace VisualBoost.Analysis;

internal sealed class CachedSourceAnalysis
{
    public CachedSourceAnalysis(long length, long lastWriteUtcTicks, SourceFileAnalysis analysis)
    {
        Length = length;
        LastWriteUtcTicks = lastWriteUtcTicks;
        Analysis = analysis;
    }

    public long Length { get; }

    public long LastWriteUtcTicks { get; }

    public SourceFileAnalysis Analysis { get; }
}
