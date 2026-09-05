using System;

namespace VisualBoost.Core.Analysis;

public sealed class FunctionNameRange
{
    public FunctionNameRange(string path, int line, int startColumn, int endColumn)
    {
        Path = path;
        Line = line;
        StartColumn = startColumn;
        EndColumn = endColumn;
    }

    public string Path { get; }
    public int Line { get; }
    public int StartColumn { get; }
    public int EndColumn { get; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Path) && Line > 0 &&
        StartColumn > 0 && EndColumn > StartColumn;

    public bool Contains(string path, int line, int startColumn, int endColumn) =>
        IsValid && StringComparer.OrdinalIgnoreCase.Equals(Path.Replace('/', '\\'), path.Replace('/', '\\')) &&
        line == Line && startColumn >= StartColumn && startColumn <= endColumn && endColumn <= EndColumn;
}
