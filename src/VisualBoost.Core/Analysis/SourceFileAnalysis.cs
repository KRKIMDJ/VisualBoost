using System;
using System.Collections.Generic;

namespace VisualBoost.Core.Analysis;

public sealed class SourceFileAnalysis
{
    public SourceFileAnalysis(
        string path,
        IReadOnlyList<SourceIncludeReference> includes,
        IReadOnlyList<SourceSymbolLocation> symbols)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Includes = includes ?? throw new ArgumentNullException(nameof(includes));
        Symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
    }

    public string Path { get; }

    public IReadOnlyList<SourceIncludeReference> Includes { get; }

    public IReadOnlyList<SourceSymbolLocation> Symbols { get; }
}
