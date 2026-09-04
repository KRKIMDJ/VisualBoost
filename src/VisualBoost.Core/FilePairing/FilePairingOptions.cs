using System;
using System.Collections.Generic;
using System.Linq;

namespace VisualBoost.Core.FilePairing;

public sealed class FilePairingOptions
{
    private static readonly string[] DefaultHeaderExtensions = { ".h", ".hpp", ".hh", ".hxx", ".inl" };
    private static readonly string[] DefaultSourceExtensions = { ".cpp", ".cc", ".cxx", ".c" };
    private static readonly (string HeaderDirectory, string SourceDirectory)[] DefaultDirectoryPairs =
    {
        ("include", "src"),
        ("include", "source"),
        ("inc", "src"),
        ("headers", "source"),
    };

    public FilePairingOptions(
        IEnumerable<string>? headerExtensions = null,
        IEnumerable<string>? sourceExtensions = null,
        IEnumerable<(string HeaderDirectory, string SourceDirectory)>? directoryPairs = null)
    {
        HeaderExtensions = NormalizeExtensions(headerExtensions ?? DefaultHeaderExtensions);
        SourceExtensions = NormalizeExtensions(sourceExtensions ?? DefaultSourceExtensions);
        DirectoryPairs = (directoryPairs ?? DefaultDirectoryPairs)
            .Where(pair =>
                !string.IsNullOrWhiteSpace(pair.HeaderDirectory) &&
                !string.IsNullOrWhiteSpace(pair.SourceDirectory))
            .Select(pair => (pair.HeaderDirectory.Trim(), pair.SourceDirectory.Trim()))
            .Distinct()
            .ToArray();
    }

    public IReadOnlyList<string> HeaderExtensions { get; }

    public IReadOnlyList<string> SourceExtensions { get; }

    public IReadOnlyList<(string HeaderDirectory, string SourceDirectory)> DirectoryPairs { get; }

    public FilePairKind GetKind(string extension)
    {
        if (Contains(HeaderExtensions, extension))
        {
            return FilePairKind.Header;
        }

        return Contains(SourceExtensions, extension) ? FilePairKind.Source : FilePairKind.Unknown;
    }

    private static bool Contains(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> NormalizeExtensions(IEnumerable<string> extensions) =>
        extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.Trim())
            .Select(extension => extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
