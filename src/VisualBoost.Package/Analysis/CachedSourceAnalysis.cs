using VisualBoost.Core.Analysis;

namespace VisualBoost.Analysis;

internal sealed class CachedSourceAnalysis
{
    // 2: 함수 본문 안의 변수·함수 위치를 빼고 한 줄 함수 정의를 등록합니다(2026-10-10). 이전 분석은 다시 분석합니다.
    internal const int CurrentRevision = 2;
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
