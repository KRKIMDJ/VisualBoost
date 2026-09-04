using System;

namespace VisualBoost.Core.Analysis;

public sealed class SourceIncludeReference
{
    public SourceIncludeReference(string value, bool isSystem, int line)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        IsSystem = isSystem;
        Line = line;
    }

    public string Value { get; }

    public bool IsSystem { get; }

    public int Line { get; }
}
