using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VisualBoost.Core.FilePairing;

public sealed class FilePairResolver
{
    private readonly FilePairingOptions options;

    public FilePairResolver(FilePairingOptions? options = null)
    {
        this.options = options ?? new FilePairingOptions();
    }

    public IReadOnlyList<FilePairMatch> FindMatches(string currentFile, IEnumerable<string> candidateFiles)
    {
        if (string.IsNullOrWhiteSpace(currentFile))
        {
            throw new ArgumentException("현재 파일 경로가 필요합니다.", nameof(currentFile));
        }

        if (candidateFiles is null)
        {
            throw new ArgumentNullException(nameof(candidateFiles));
        }

        var normalizedCurrentFile = NormalizePath(currentFile);
        var currentKind = options.GetKind(Path.GetExtension(normalizedCurrentFile));
        if (currentKind == FilePairKind.Unknown)
        {
            return Array.Empty<FilePairMatch>();
        }

        var currentStem = Path.GetFileNameWithoutExtension(normalizedCurrentFile);
        var currentDirectory = Path.GetDirectoryName(normalizedCurrentFile) ?? string.Empty;
        var oppositeKind = currentKind == FilePairKind.Header ? FilePairKind.Source : FilePairKind.Header;

        return candidateFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Where(path => !string.Equals(path, normalizedCurrentFile, StringComparison.OrdinalIgnoreCase))
            .Where(path => options.GetKind(Path.GetExtension(path)) == oppositeKind)
            .Where(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                currentStem,
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => Score(normalizedCurrentFile, currentDirectory, path, currentKind))
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private FilePairMatch Score(
        string currentFile,
        string currentDirectory,
        string candidateFile,
        FilePairKind currentKind)
    {
        var candidateDirectory = Path.GetDirectoryName(candidateFile) ?? string.Empty;
        var score = GetExtensionPreference(candidateFile);
        var reasons = new List<string>();

        if (string.Equals(currentDirectory, candidateDirectory, StringComparison.OrdinalIgnoreCase))
        {
            score += 10_000;
            reasons.Add("같은 디렉터리");
        }

        if (IsConfiguredDirectoryPair(currentFile, candidateFile, currentKind))
        {
            score += 5_000;
            reasons.Add("대응 디렉터리");
        }

        var commonSegments = CountCommonDirectorySegments(currentDirectory, candidateDirectory);
        score += commonSegments * 100;
        if (commonSegments > 0)
        {
            reasons.Add($"공통 경로 {commonSegments}단계");
        }

        if (reasons.Count == 0)
        {
            reasons.Add("동일한 파일명");
        }

        return new FilePairMatch(candidateFile, score, string.Join(", ", reasons));
    }

    private int GetExtensionPreference(string path)
    {
        var extension = Path.GetExtension(path);
        var extensions = options.GetKind(extension) == FilePairKind.Header
            ? options.HeaderExtensions
            : options.SourceExtensions;

        for (var index = 0; index < extensions.Count; index++)
        {
            if (string.Equals(extensions[index], extension, StringComparison.OrdinalIgnoreCase))
            {
                return extensions.Count - index;
            }
        }

        return 0;
    }

    private bool IsConfiguredDirectoryPair(string currentFile, string candidateFile, FilePairKind currentKind)
    {
        var currentSegments = SplitDirectory(currentFile);
        var candidateSegments = SplitDirectory(candidateFile);

        foreach (var pair in options.DirectoryPairs)
        {
            var expectedCurrent = currentKind == FilePairKind.Header
                ? pair.HeaderDirectory
                : pair.SourceDirectory;
            var expectedCandidate = currentKind == FilePairKind.Header
                ? pair.SourceDirectory
                : pair.HeaderDirectory;

            if (ContainsSegment(currentSegments, expectedCurrent) &&
                ContainsSegment(candidateSegments, expectedCandidate))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountCommonDirectorySegments(string first, string second)
    {
        var firstSegments = SplitDirectory(first);
        var secondSegments = SplitDirectory(second);
        var length = Math.Min(firstSegments.Length, secondSegments.Length);
        var count = 0;

        while (count < length && string.Equals(
            firstSegments[count],
            secondSegments[count],
            StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    private static string[] SplitDirectory(string path)
    {
        var directory = Path.HasExtension(path) ? Path.GetDirectoryName(path) ?? string.Empty : path;
        return directory.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool ContainsSegment(IEnumerable<string> segments, string value) =>
        segments.Any(segment => string.Equals(segment, value, StringComparison.OrdinalIgnoreCase));

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

