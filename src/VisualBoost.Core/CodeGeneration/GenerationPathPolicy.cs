using System;
using System.IO;
using System.Linq;

namespace VisualBoost.Core.CodeGeneration;

public static class GenerationPathPolicy
{
    public static void Validate(string solutionRoot, string path)
    {
        var root = Path.GetFullPath(solutionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new GenerationNotSupportedException("솔루션 폴더 밖의 외부 소스는 생성 대상에서 제외합니다.");
        var relative = full.Substring(root.Length);
        var blocked = new[] { "Engine", "ThirdParty", "External", "Vendor", "Generated", "Intermediate", ".vs", "bin", "obj" };
        if (relative.Split(new[] { '/', '\\' }).Any(p => blocked.Contains(p, StringComparer.OrdinalIgnoreCase)) ||
            full.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".gen.cpp", StringComparison.OrdinalIgnoreCase))
            throw new GenerationNotSupportedException("엔진·외부·생성 코드 폴더는 수정하지 않습니다.");
        var extension = Path.GetExtension(full);
        if (!new[] { ".h", ".hpp", ".hh", ".hxx", ".cpp", ".cc", ".cxx" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new GenerationNotSupportedException("일반 C++ 헤더·구현 파일만 지원합니다.");
    }
}
