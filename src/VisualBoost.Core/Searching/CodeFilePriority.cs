using System;
using System.Collections.Generic;
using System.IO;

namespace VisualBoost.Core.Searching;

public static class CodeFilePriority
{
    private static readonly HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".inl", ".ixx", ".cppm",
      ".cs", ".fs", ".fsx", ".vb", ".py", ".js", ".jsx", ".ts", ".tsx", ".java", ".kt", ".rs", ".go",
      ".usf", ".ush", ".hlsl", ".hlsli", ".glsl", ".shader" };
    public static bool IsCode(string path) => extensions.Contains(Path.GetExtension(path));
}
