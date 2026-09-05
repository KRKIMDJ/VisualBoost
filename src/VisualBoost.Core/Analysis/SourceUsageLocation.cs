using System;
using System.Collections.Generic;

namespace VisualBoost.Core.Analysis;

public sealed class SourceUsageLocation
{
    public SourceUsageLocation(
        string symbol,
        string path,
        int line,
        int column,
        string lineText,
        IReadOnlyList<SourceIdentifierSpan>? identifiers = null)
    {
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Line = line;
        Column = column;
        LineText = lineText ?? throw new ArgumentNullException(nameof(lineText));
        Identifiers = identifiers ?? Array.Empty<SourceIdentifierSpan>();
    }

    public string Symbol { get; }

    public string Path { get; }

    public int Line { get; }

    public int Column { get; }

    public string LineText { get; }

    public IReadOnlyList<SourceIdentifierSpan> Identifiers { get; }
}
