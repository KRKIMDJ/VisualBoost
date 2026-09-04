using System;
using System.Collections.Generic;
using System.IO;
using VisualBoost.Core.FilePairing;
using VisualBoost.Core.Indexing;

namespace VisualBoost.Core.Tests;

internal static class Program
{
    private static int failures;

    private static int Main()
    {
        Run("같은 디렉터리의 구현 파일을 가장 먼저 선택한다", SameDirectoryWins);
        Run("include와 src 디렉터리를 대응시킨다", IncludeAndSrcArePaired);
        Run("파일명은 대소문자를 구분하지 않는다", FileNameMatchingIsCaseInsensitive);
        Run("지원하지 않는 파일 형식은 결과가 없다", UnknownExtensionHasNoMatches);
        Run("중복 후보를 제거한다", DuplicateCandidatesAreRemoved);
        Run("파일 인덱스는 언어와 무관하게 같은 이름을 찾는다", FileIndexSupportsMultipleLanguages);
        Run("파일 인덱스는 생성과 삭제를 증분 반영한다", FileIndexTracksAddsAndRemoves);
        Run("대규모 파일 인덱스에서 이름으로 후보를 제한한다", FileIndexNarrowsLargeCandidateSet);
        Run("사용자 확장자와 대응 디렉터리를 적용한다", CustomFilePairingOptionsAreApplied);

        Console.WriteLine(failures == 0
            ? "모든 VisualBoost.Core 테스트가 통과했습니다."
            : $"{failures}개 테스트가 실패했습니다.");
        return failures == 0 ? 0 : 1;
    }

    private static void SameDirectoryWins()
    {
        var root = Root();
        var current = Path.Combine(root, "widget.h");
        var expected = Path.Combine(root, "widget.cpp");
        var matches = Resolver().FindMatches(current, new[]
        {
            Path.Combine(root, "other", "widget.cpp"),
            expected,
        });

        Equal(expected, matches[0].Path);
    }

    private static void IncludeAndSrcArePaired()
    {
        var root = Root();
        var current = Path.Combine(root, "include", "widget.hpp");
        var expected = Path.Combine(root, "src", "widget.cpp");
        var matches = Resolver().FindMatches(current, new[]
        {
            Path.Combine(root, "vendor", "widget.cpp"),
            expected,
        });

        Equal(expected, matches[0].Path);
    }

    private static void FileNameMatchingIsCaseInsensitive()
    {
        var root = Root();
        var matches = Resolver().FindMatches(
            Path.Combine(root, "Widget.H"),
            new[] { Path.Combine(root, "widget.CPP") });

        Equal(1, matches.Count);
    }

    private static void UnknownExtensionHasNoMatches()
    {
        var root = Root();
        var matches = Resolver().FindMatches(
            Path.Combine(root, "widget.txt"),
            new[] { Path.Combine(root, "widget.cpp") });

        Equal(0, matches.Count);
    }

    private static void DuplicateCandidatesAreRemoved()
    {
        var root = Root();
        var candidate = Path.Combine(root, "widget.cpp");
        var matches = Resolver().FindMatches(
            Path.Combine(root, "widget.h"),
            new[] { candidate, candidate.ToUpperInvariant() });

        Equal(1, matches.Count);
    }

    private static void FileIndexSupportsMultipleLanguages()
    {
        var root = Root();
        using var index = new FilePathIndex();
        index.ReplaceAll(new[]
        {
            Path.Combine(root, "native", "widget.cpp"),
            Path.Combine(root, "managed", "Widget.cs"),
            Path.Combine(root, "scripts", "widget.py"),
        });

        Equal(3, index.FindByStem("widget").Count);
    }

    private static void FileIndexTracksAddsAndRemoves()
    {
        var root = Root();
        var path = Path.Combine(root, "widget.rs");
        using var index = new FilePathIndex();

        Equal(true, index.Add(path));
        Equal(1, index.FindByStem("widget").Count);
        Equal(true, index.Remove(path));
        Equal(0, index.FindByStem("widget").Count);
    }

    private static void FileIndexNarrowsLargeCandidateSet()
    {
        var root = Root();
        var paths = new List<string>();
        for (var index = 0; index < 10_000; index++)
        {
            paths.Add(Path.Combine(root, "src", $"file-{index}.cpp"));
        }

        paths.Add(Path.Combine(root, "include", "target.hpp"));
        paths.Add(Path.Combine(root, "src", "target.cpp"));

        using var fileIndex = new FilePathIndex();
        fileIndex.ReplaceAll(paths);

        Equal(10_002, fileIndex.Count);
        Equal(2, fileIndex.FindByStem("target").Count);
    }

    private static void CustomFilePairingOptionsAreApplied()
    {
        var root = Root();
        var current = Path.Combine(root, "public", "widget.header");
        var expected = Path.Combine(root, "private", "widget.impl");
        var options = new FilePairingOptions(
            new[] { "header" },
            new[] { "impl" },
            new[] { ("public", "private") });
        var matches = new FilePairResolver(options).FindMatches(current, new[]
        {
            Path.Combine(root, "other", "widget.impl"),
            expected,
        });

        Equal(expected, matches[0].Path);
    }

    private static FilePairResolver Resolver() => new();

    private static string Root() => Path.Combine(Path.GetTempPath(), "VisualBoostTests");

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected: {expected}; Actual: {actual}");
        }
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine($"FAIL: {name}\n{exception}");
        }
    }
}
