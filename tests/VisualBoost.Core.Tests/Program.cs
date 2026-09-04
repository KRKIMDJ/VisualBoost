using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VisualBoost.Core.FilePairing;
using VisualBoost.Core.Indexing;
using VisualBoost.Core.Searching;
using VisualBoost.Core.Analysis;

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
        Run("정확한 파일명 일치를 우선한다", ExactFileNameWinsFuzzySearch);
        Run("경로 구분자를 포함한 검색을 지원한다", PathSegmentsParticipateInFuzzySearch);
        Run("띄어쓴 검색어를 모두 만족해야 한다", MultipleSearchTokensMustMatch);
        Run("퍼지 검색 결과 수를 제한한다", FuzzySearchLimitsResults);
        Run("대규모 인덱스에서 퍼지 검색 결과를 결정적으로 정렬한다", LargeFuzzySearchIsDeterministic);
        Run("파일 변경 후 퍼지 검색 스냅샷을 갱신한다", FuzzySearchSnapshotTracksChanges);
        Run("퍼지 검색을 취소할 수 있다", FuzzySearchCanBeCancelled);
        Run("최근 파일을 동점 후보보다 우선한다", RecentFileWinsEquivalentMatch);
        Run("현재 프로젝트 파일을 동점 후보보다 우선한다", CurrentProjectWinsEquivalentMatch);
        Run("C++ include와 주요 심볼 위치를 추출한다", CppSourceAnalysisFindsIncludesAndSymbols);
        Run("주석 속 심볼은 분석에서 제외한다", CppSourceAnalysisIgnoresComments);
        Run("심볼 인덱스는 이름별 위치를 반환한다", SourceSymbolIndexFindsLocations);

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

    private static void ExactFileNameWinsFuzzySearch()
    {
        var root = Root();
        var expected = Path.Combine(root, "src", "widget.cpp");
        var matches = FuzzyFileSearch.Search("widget", new[]
        {
            Path.Combine(root, "src", "widgetFactory.cpp"),
            Path.Combine(root, "src", "my-widget.cpp"),
            expected,
        });

        Equal(expected, matches[0].Path);
    }

    private static void PathSegmentsParticipateInFuzzySearch()
    {
        var root = Root();
        var expected = Path.Combine(root, "src", "render", "widget.cpp");
        var matches = FuzzyFileSearch.Search("src/render", new[]
        {
            Path.Combine(root, "vendor", "render", "widget.cpp"),
            expected,
        });

        Equal(expected, matches[0].Path);
    }

    private static void MultipleSearchTokensMustMatch()
    {
        var root = Root();
        var expected = Path.Combine(root, "engine", "renderWidget.cpp");
        var matches = FuzzyFileSearch.Search("engine widget", new[]
        {
            Path.Combine(root, "engine", "renderer.cpp"),
            Path.Combine(root, "tools", "renderWidget.cpp"),
            expected,
        });

        Equal(1, matches.Count);
        Equal(expected, matches[0].Path);
    }

    private static void FuzzySearchLimitsResults()
    {
        var root = Root();
        var paths = new List<string>();
        for (var index = 0; index < 100; index++)
        {
            paths.Add(Path.Combine(root, $"widget-{index}.cpp"));
        }

        Equal(7, FuzzyFileSearch.Search("widget", paths, 7).Count);
        Equal(0, FuzzyFileSearch.Search("widget", paths, 0).Count);
    }

    private static void LargeFuzzySearchIsDeterministic()
    {
        var root = Root();
        var paths = new List<string>();
        for (var index = 0; index < 25_000; index++)
        {
            paths.Add(Path.Combine(root, "generated", $"file-{index}.cpp"));
        }

        var expected = Path.Combine(root, "src", "FilePathIndex.cs");
        paths.Add(expected);
        using var fileIndex = new FilePathIndex();
        fileIndex.ReplaceAll(paths);

        var first = fileIndex.Search("fpi", 10);
        var second = fileIndex.Search("fpi", 10);
        Equal(expected, first[0].Path);
        Equal(first.Count, second.Count);
        for (var resultIndex = 0; resultIndex < first.Count; resultIndex++)
        {
            Equal(first[resultIndex].Path, second[resultIndex].Path);
        }
    }

    private static void FuzzySearchSnapshotTracksChanges()
    {
        var root = Root();
        var original = Path.Combine(root, "widget.cpp");
        var added = Path.Combine(root, "renderer.cpp");
        using var index = new FilePathIndex();
        index.ReplaceAll(new[] { original });

        Equal(original, index.Search("widget")[0].Path);
        index.Add(added);
        Equal(added, index.Search("renderer")[0].Path);
        index.Remove(original);
        Equal(0, index.Search("widget").Count);
    }

    private static void FuzzySearchCanBeCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Throws<OperationCanceledException>(() =>
            FuzzyFileSearch.Search("widget", new[] { "widget.cpp" }, cancellationToken: cancellation.Token));
    }

    private static void RecentFileWinsEquivalentMatch()
    {
        var root = Root();
        var first = Path.Combine(root, "alpha", "Widget.cpp");
        var recent = Path.Combine(root, "bravo", "Widget.cpp");
        var context = new FileSearchRankingContext(recentPaths: new[] { recent });

        var matches = FuzzyFileSearch.Search("widget", new[] { first, recent }, context);

        Equal(recent, matches[0].Path);
    }

    private static void CurrentProjectWinsEquivalentMatch()
    {
        var root = Root();
        var preferredRoot = Path.Combine(root, "bravo");
        var first = Path.Combine(root, "alpha", "Widget.cpp");
        var preferred = Path.Combine(preferredRoot, "Widget.cpp");
        var context = new FileSearchRankingContext(preferredRoot);

        var matches = FuzzyFileSearch.Search("widget", new[] { first, preferred }, context);

        Equal(preferred, matches[0].Path);
    }

    private static void CppSourceAnalysisFindsIncludesAndSymbols()
    {
        const string source = """
            #include "Widget.h"
            #include <vector>
            #define WIDGET_ENABLED 1
            namespace Demo {
            class Widget final {};
            int BuildWidget(int value);
            static int WidgetCount = 0;
            }
            """;
        var analysis = CppSourceAnalyzer.Analyze("Widget.cpp", source);

        Equal(2, analysis.Includes.Count);
        Equal("Widget.h", analysis.Includes[0].Value);
        Equal(false, analysis.Includes[0].IsSystem);
        Equal(true, analysis.Includes[1].IsSystem);
        Equal(true, analysis.Symbols.Any(symbol =>
            symbol.Name == "WIDGET_ENABLED" && symbol.Kind == SourceSymbolKind.Macro));
        Equal(true, analysis.Symbols.Any(symbol =>
            symbol.Name == "Demo" && symbol.Kind == SourceSymbolKind.Namespace));
        Equal(true, analysis.Symbols.Any(symbol =>
            symbol.Name == "Widget" && symbol.Kind == SourceSymbolKind.Type));
        Equal(true, analysis.Symbols.Any(symbol =>
            symbol.Name == "BuildWidget" && symbol.Kind == SourceSymbolKind.Function));
        Equal(true, analysis.Symbols.Any(symbol =>
            symbol.Name == "WidgetCount" && symbol.Kind == SourceSymbolKind.Variable));
    }

    private static void CppSourceAnalysisIgnoresComments()
    {
        const string source = """
            // class HiddenType {};
            /*
            #define HIDDEN_MACRO 1
            */
            struct VisibleType {};
            """;
        var analysis = CppSourceAnalyzer.Analyze("Types.h", source);

        Equal(1, analysis.Symbols.Count);
        Equal("VisibleType", analysis.Symbols[0].Name);
    }

    private static void SourceSymbolIndexFindsLocations()
    {
        using var index = new SourceSymbolIndex();
        index.ReplaceAll(new[]
        {
            new SourceSymbolLocation("Widget", "B.h", 20, 3, SourceSymbolKind.Type),
            new SourceSymbolLocation("Widget", "A.cpp", 5, 1, SourceSymbolKind.Function),
        });

        Equal(2, index.Count);
        Equal("A.cpp", index.Find("widget")[0].Path);
        Equal(0, index.Find("missing").Count);
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

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception: {typeof(TException).Name}");
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
