namespace VisualBoost.Core.Analysis;

public enum SymbolNavigationDirection
{
    Definition,
    Declaration,
}

public static class FunctionNavigationPolicy
{
    public static SymbolNavigationDirection SelectDirection(
        string? currentPath, int line, int startColumn, int endColumn,
        FunctionNameRange? declaration, FunctionNameRange? definition)
    {
        // 파일 형식이나 함수 본문 전체가 아니라 이름 범위만 판단합니다.
        // 인라인 함수처럼 두 위치가 겹치거나 Code Model 정보가 불완전하면
        // 언어 서비스의 기본 정의 탐색에 맡겨 잘못된 역방향 이동을 피합니다.
        if (string.IsNullOrWhiteSpace(currentPath) || declaration?.IsValid != true ||
            definition?.IsValid != true ||
            !definition.Contains(currentPath!, line, startColumn, endColumn) ||
            declaration.Contains(currentPath!, line, startColumn, endColumn))
        {
            return SymbolNavigationDirection.Definition;
        }

        return SymbolNavigationDirection.Declaration;
    }
}
