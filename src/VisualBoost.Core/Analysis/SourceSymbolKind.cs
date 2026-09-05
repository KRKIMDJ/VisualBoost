namespace VisualBoost.Core.Analysis;

public enum SourceSymbolKind
{
    Unknown,
    Namespace,
    Type,
    Function,
    Variable,
    Macro,
    // 기존 캐시의 숫자 값은 유지하고 상세 타입은 뒤에 추가합니다.
    Class,
    Struct,
    Union,
    Enum,
}
