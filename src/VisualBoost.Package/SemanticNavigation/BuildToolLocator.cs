using VisualBoost.Core.SemanticNavigation;

namespace VisualBoost.SemanticNavigation;

/// <summary>
/// 컴파일 명령을 얻을 빌드 도구를 현재 VS 설치에서 찾습니다. 프로젝트를 연 VS와 같은 C++ 도구 집합을 쓰기 위해
/// 다른 설치로 대체하지 않습니다.
/// </summary>
internal static class BuildToolLocator
{
    public static string? FindMsBuild()
    {
        var install = ClangdLocator.CurrentInstallDirectory();
        return install is null ? null : MsBuildCompileCommands.FindMsBuild(install);
    }

    public static string? FindNinja()
    {
        var install = ClangdLocator.CurrentInstallDirectory();
        return install is null ? null : NinjaCompileCommands.FindNinja(install);
    }
}
