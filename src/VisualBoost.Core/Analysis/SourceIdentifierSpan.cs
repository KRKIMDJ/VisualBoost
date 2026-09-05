namespace VisualBoost.Core.Analysis;

// LineText 기준의 0부터 시작하는 식별자 범위입니다. 주석과 문자열은 포함하지 않습니다.
public sealed class SourceIdentifierSpan
{
    public SourceIdentifierSpan(int start, int length) { Start = start; Length = length; }
    public int Start { get; }
    public int Length { get; }
}
