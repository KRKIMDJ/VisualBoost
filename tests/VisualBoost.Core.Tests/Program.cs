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
        Run("파일 카탈로그는 생성물 디렉터리를 제외한다", FileCatalogExcludesGeneratedDirectories);
        Run("대규모 파일 인덱스에서 이름으로 후보를 제한한다", FileIndexNarrowsLargeCandidateSet);
        Run("사용자 확장자와 대응 디렉터리를 적용한다", CustomFilePairingOptionsAreApplied);
        Run("정확한 파일명 일치를 우선한다", ExactFileNameWinsFuzzySearch);
        Run("경로 구분자를 포함한 검색을 지원한다", PathSegmentsParticipateInFuzzySearch);
        Run("띄어쓴 검색어를 모두 만족해야 한다", MultipleSearchTokensMustMatch);
        Run("퍼지 검색 결과 수를 제한한다", FuzzySearchLimitsResults);
        Run("대규모 인덱스에서 퍼지 검색 결과를 결정적으로 정렬한다", LargeFuzzySearchIsDeterministic);
        Run("파일 변경 후 퍼지 검색 스냅샷을 갱신한다", FuzzySearchSnapshotTracksChanges);
        Run("퍼지 검색을 취소할 수 있다", FuzzySearchCanBeCancelled);
        Run("진행 중인 대규모 퍼지 검색을 취소할 수 있다", RunningFuzzySearchCanBeCancelled);
        Run("최근 파일을 동점 후보보다 우선한다", RecentFileWinsEquivalentMatch);
        Run("현재 프로젝트 파일을 동점 후보보다 우선한다", CurrentProjectWinsEquivalentMatch);
        Run("현재 프로젝트 검색 범위는 지정한 루트만 포함한다", CurrentProjectScopeIncludesPreferredRoot);
        Run("열린 파일 검색 범위는 열린 문서만 포함한다", OpenFilesScopeIncludesOnlyOpenDocuments);
        Run("외부 소스 검색 범위는 Solution 바깥만 포함한다", ExternalScopeExcludesSolutionFiles);
        Run("C++ include와 주요 심볼 위치를 추출한다", CppSourceAnalysisFindsIncludesAndSymbols);
        Run("주석 속 심볼은 분석에서 제외한다", CppSourceAnalysisIgnoresComments);
        Run("함수 호출은 심볼 선언에서 제외한다", CppSourceAnalysisIgnoresFunctionCalls);
        Run("클래스 전방 선언은 타입 정의에서 제외한다", CppSourceAnalysisIgnoresForwardDeclarations);
        Run("심볼 인덱스는 이름별 위치를 반환한다", SourceSymbolIndexFindsLocations);
        Run("심볼 검색은 정확한 이름을 우선한다", ExactSymbolNameWinsSearch);
        Run("심볼 검색은 파일 경로를 검색하지 않는다", SymbolSearchIgnoresFilePaths);
        Run("심볼 검색은 결과 수와 취소를 적용한다", SymbolSearchLimitsResultsAndCancels);
        Run("최적화된 심볼 검색의 점수와 순서가 기존 검색과 일치한다", SymbolSnapshotMatchesBaseline);
        Run("심볼 스냅샷 교체와 취소를 반영한다", SymbolSnapshotReplacesAndCancels);
        Run("실행 프로젝트의 외부 연결 파일을 포함하고 다른 프로젝트는 제외한다", SourceProjectMembershipIsExact);
        Run("C++ 사용처 검색은 정확한 식별자 경계를 찾는다", CppUsageSearchMatchesIdentifierBoundaries);
        Run("C++ 사용처 검색은 주석과 문자열을 제외한다", CppUsageSearchIgnoresCommentsAndStrings);

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

    private static void FileCatalogExcludesGeneratedDirectories()
    {
        var root = Root();

        Equal(true, FileSystemPathCatalog.IsExcludedPath(
            Path.Combine(root, "Intermediate", "Build", "Widget.cpp")));
        Equal(true, FileSystemPathCatalog.IsExcludedPath(
            Path.Combine(root, ".vs", "Cache", "Widget.cpp")));
        Equal(false, FileSystemPathCatalog.IsExcludedPath(
            Path.Combine(root, "Source", "Widget.cpp")));
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

    private static void RunningFuzzySearchCanBeCancelled()
    {
        using var cancellation = new CancellationTokenSource();

        Throws<OperationCanceledException>(() =>
            FuzzyFileSearch.Search(
                "widget",
                EnumerateAndCancel(cancellation),
                cancellationToken: cancellation.Token));
    }

    private static IEnumerable<string> EnumerateAndCancel(CancellationTokenSource cancellation)
    {
        for (var index = 0; index < 1_000; index++)
        {
            if (index == 10)
            {
                cancellation.Cancel();
            }

            yield return $"Widget-{index}.cpp";
        }
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

    private static void CurrentProjectScopeIncludesPreferredRoot()
    {
        var root = Root();
        var projectRoot = Path.Combine(root, "Game");

        Equal(true, FileSearchScopeFilter.Includes(
            Path.Combine(projectRoot, "Source", "Widget.cpp"),
            FileSearchScope.CurrentProject,
            projectRoot,
            root));
        Equal(false, FileSearchScopeFilter.Includes(
            Path.Combine(root, "Engine", "Widget.cpp"),
            FileSearchScope.CurrentProject,
            projectRoot,
            root));
    }

    private static void OpenFilesScopeIncludesOnlyOpenDocuments()
    {
        var root = Root();
        var openPath = Path.Combine(root, "Widget.cpp");
        var openFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { openPath };

        Equal(true, FileSearchScopeFilter.Includes(
            openPath.ToUpperInvariant(),
            FileSearchScope.OpenFiles,
            root,
            root,
            openFiles));
        Equal(false, FileSearchScopeFilter.Includes(
            Path.Combine(root, "Other.cpp"),
            FileSearchScope.OpenFiles,
            root,
            root,
            openFiles));
    }

    private static void ExternalScopeExcludesSolutionFiles()
    {
        var root = Root();
        var solutionRoot = Path.Combine(root, "Game");

        Equal(false, FileSearchScopeFilter.Includes(
            Path.Combine(solutionRoot, "Source", "Widget.cpp"),
            FileSearchScope.ExternalSources,
            solutionRoot,
            solutionRoot));
        Equal(true, FileSearchScopeFilter.Includes(
            Path.Combine(root, "Engine", "Source", "Widget.cpp"),
            FileSearchScope.ExternalSources,
            solutionRoot,
            solutionRoot));
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

    private static void CppSourceAnalysisIgnoresFunctionCalls()
    {
        const string source = """
            class Widget {};
            Widget CreateWidget();
            void BuildWidget() {
            CreateWidget();
            Widget();
            object.CreateWidget();
            auto value = CreateWidget();
            return CreateWidget();
            """;

        var analysis = CppSourceAnalyzer.Analyze("Widget.cpp", source);
        var functions = analysis.Symbols
            .Where(symbol => symbol.Kind == SourceSymbolKind.Function)
            .Select(symbol => symbol.Name)
            .ToArray();

        Equal(2, functions.Length);
        Equal("CreateWidget", functions[0]);
        Equal("BuildWidget", functions[1]);
    }

    private static void CppSourceAnalysisIgnoresForwardDeclarations()
    {
        const string source = """
            class ForwardOnly;
            class DefinedType final
            {
            };
            class PROJECT_API ExportedType {};
            """;

        var analysis = CppSourceAnalyzer.Analyze("Types.h", source);
        var types = analysis.Symbols.Where(symbol => symbol.Kind == SourceSymbolKind.Type).ToArray();

        Equal(2, types.Length);
        Equal("DefinedType", types[0].Name);
        Equal("ExportedType", types[1].Name);
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

    private static void ExactSymbolNameWinsSearch()
    {
        var symbols = new[]
        {
            new SourceSymbolLocation("WidgetFactory", "Factory.cpp", 2, 1, SourceSymbolKind.Type),
            new SourceSymbolLocation("Widget", "Widget.h", 5, 7, SourceSymbolKind.Type),
            new SourceSymbolLocation("CreateWidget", "Widget.cpp", 9, 3, SourceSymbolKind.Function),
        };

        var matches = FuzzySymbolSearch.Search("Widget", symbols);

        Equal("Widget", matches[0].Location.Name);
    }

    private static void SymbolSearchLimitsResultsAndCancels()
    {
        var symbols = Enumerable.Range(0, 100)
            .Select(index => new SourceSymbolLocation(
                $"Widget{index}",
                $"Widget{index}.cpp",
                index + 1,
                1,
                SourceSymbolKind.Function))
            .ToArray();

        Equal(7, FuzzySymbolSearch.Search("Widget", symbols, 7).Count);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() =>
            FuzzySymbolSearch.Search("Widget", symbols, cancellationToken: cancellation.Token));
    }

    private static void SymbolSearchIgnoresFilePaths()
    {
        var symbols = new[]
        {
            new SourceSymbolLocation("BuildWidget", "Runtime/Character/Pawn.cpp", 8, 1, SourceSymbolKind.Function),
        };

        Equal(0, FuzzySymbolSearch.Search("Character", symbols).Count);
        Equal(1, FuzzySymbolSearch.Search("BuildWidget", symbols).Count);
    }

    private static void SymbolSnapshotMatchesBaseline()
    {
        var names = new[] { "SetMovementMode", "setMovementMode", "SMMode", "GetWorld", "Set_Mode", "État", "상태", "I", "ı" };
        var symbols = Enumerable.Range(0, 3000).Select(i => new SourceSymbolLocation(
            names[i % names.Length] + (i % 3 == 0 ? (i % 100).ToString() : ""),
            $"Folder{i % 7}/File{i % 31}.cpp", i + 1, 1, SourceSymbolKind.Function)).ToArray();
        using var index = new SourceSymbolIndex();
        index.ReplaceAll(symbols);
        foreach (var query in new[] { "S", "Set", "SMMode", "Set Mode", "setmovementmode", "GetWorld", "ét", "상태", "I", "missing", "" })
        foreach (var limit in new[] { 1, 17, 200 })
        {
            var expected = FuzzySymbolSearch.Search(query, symbols, limit);
            var actual = index.Search(query, limit);
            Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Equal(expected[i].Score, actual[i].Score);
                Equal(expected[i].Location, actual[i].Location);
            }
        }
    }

    private static void SymbolSnapshotReplacesAndCancels()
    {
        using var index = new SourceSymbolIndex();
        index.ReplaceAll(new[] { new SourceSymbolLocation("Old", "A.h", 1, 1, SourceSymbolKind.Type) });
        Equal(1, index.Search("Old").Count);
        index.ReplaceAll(new[] { new SourceSymbolLocation("New", "B.h", 1, 1, SourceSymbolKind.Type) });
        Equal(0, index.Search("Old").Count);
        Equal(1, index.Search("New").Count);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => index.Search("New", cancellationToken: cancellation.Token));
    }

    private static void SourceProjectMembershipIsExact()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoostProjectTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Projects"));
        try
        {
            var project = Path.Combine(root, "Projects", "Game.vcxproj");
            var shared = Path.Combine(root, "Projects", "Shared.vcxitems");
            File.WriteAllText(project, """
                <Project><ItemGroup>
                <ClCompile Include="../Source/Game.cpp" />
                <ClCompile Include="../Plugins/**/*.cpp" />
                <ClInclude Include="$(ProjectDir)../Source/Game.h" />
                </ItemGroup><Import Project="Shared.vcxitems" /></Project>
                """);
            File.WriteAllText(shared, """
                <Project><ItemGroup><ClInclude Include="$(MSBuildThisFileDirectory)../Shared/Public.h" /></ItemGroup></Project>
                """);
            var owned = new[] { "Source/Game.cpp", "Source/Game.h", "Plugins/Top.cpp", "Plugins/Feature/Linked.cpp", "Shared/Public.h" }
                .Select(path => Path.GetFullPath(Path.Combine(root, path))).ToArray();
            var other = Path.Combine(root, "Source", "OtherProject.cpp");
            var files = SourceProjectFiles.Read(project, owned.Concat(new[] { other }).ToArray());
            Equal(owned.Length, files.Count);
            foreach (var path in owned) Equal(true, files.Contains(path));
            Equal(false, files.Contains(other));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => SourceProjectFiles.Read(project, owned, cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CppUsageSearchMatchesIdentifierBoundaries()
    {
        const string source = "Widget value; WidgetFactory factory; value = Widget();";

        var matches = CppIdentifierUsageScanner.Find("Widget", "Widget.cpp", source);

        Equal(2, matches.Count);
        Equal(1, matches[0].Line);
        Equal(1, matches[0].Column);
    }

    private static void CppUsageSearchIgnoresCommentsAndStrings()
    {
        const string source = """
            // Widget in line comment
            const char* text = "Widget";
            /* Widget in block comment
               Widget still in block comment */
            Widget visible;
            """;

        var matches = CppIdentifierUsageScanner.Find("Widget", "Widget.cpp", source);

        Equal(1, matches.Count);
        Equal(5, matches[0].Line);
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
