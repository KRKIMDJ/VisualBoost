using System;

namespace VisualBoost.Core.Analysis;

public sealed class SourceSymbolLocation
{
    public SourceSymbolLocation(
        string name,
        string path,
        int line,
        int column,
        SourceSymbolKind kind,
        string scope = "",
        string signature = "")
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Line = line;
        Column = column;
        Kind = kind;
        Scope = scope ?? string.Empty;
        Signature = signature ?? string.Empty;
    }

    public string Name { get; }

    public string Path { get; }

    public int Line { get; }

    public int Column { get; }

    public SourceSymbolKind Kind { get; }
    public string Scope { get; }
    public string Signature { get; }
    public string Detail => Signature + (Scope.Length == 0 ? string.Empty : " · " + Scope);
    public string Description => (Scope.Length == 0 ? Name : Scope + "::" + Name) + Signature;
}
