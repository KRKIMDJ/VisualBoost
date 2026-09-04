using System;

namespace VisualBoost.Core.Analysis;

public sealed class SourceSymbolLocation
{
    public SourceSymbolLocation(
        string name,
        string path,
        int line,
        int column,
        SourceSymbolKind kind)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Line = line;
        Column = column;
        Kind = kind;
    }

    public string Name { get; }

    public string Path { get; }

    public int Line { get; }

    public int Column { get; }

    public SourceSymbolKind Kind { get; }
}
