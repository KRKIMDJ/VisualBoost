using VisualBoost.Core.Analysis;

namespace VisualBoost.Analysis;

internal sealed class CachedSourceAnalysis
{
    internal const int CurrentRevision = 1;
    public CachedSourceAnalysis(long length, long lastWriteUtcTicks, SourceFileAnalysis analysis, int revision = CurrentRevision)
    {
        Length = length;
        LastWriteUtcTicks = lastWriteUtcTicks;
        Analysis = analysis;
        Revision = revision;
    }

    public long Length { get; }

    public long LastWriteUtcTicks { get; }

    public SourceFileAnalysis Analysis { get; }
    public int Revision { get; }
}
