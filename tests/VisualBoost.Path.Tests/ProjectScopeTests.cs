using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Analysis;
using VisualBoost.Core.Indexing;
using VisualBoost.Core.Searching;
using VisualBoost.Services;

internal static class ProjectScopeTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoostScope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Write(string relative, string text = "void ScopedSymbol();")
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                return path;
            }
            var header = Write(@"Engine\Plugins\Runtime\CommonUI\Source\CommonUI\Public\Input\UIActionRouterTypes.h");
            var added = Write(@"Engine\Plugins\Runtime\CommonUI\Source\CommonUI\Private\NewCode.cpp");
            var asset = Write(@"Engine\Plugins\Runtime\CommonUI\Source\CommonUI\Private\Ignored.uasset");
            var unrelated = Write(@"Unrelated\Source\Other.cpp");
            var project = Path.Combine(root, @"Game\Intermediate\ProjectFiles\Engine.vcxproj");
            var roots = ProjectSourceScope.GetRoots(project, new[] { header });
            Check(roots.Count == 1 && roots[0].EndsWith(@"Source\CommonUI"), "연결된 플러그인 모듈만 소스 루트로 추론");
            var files = FileSystemPathCatalog.GetFiles(roots, includeFile: ProjectSourceScope.IsSupplementalCode);
            Check(files.Count == 2 && files.Contains(added) && !files.Contains(asset), "솔루션 재생성 전 새 코드 발견 및 에셋·무관한 프로젝트 제외");

            var cache = new SourceAnalysisCache(Path.Combine(root, "Cache"));
            var solution = Path.Combine(root, "Independent.sln");
            var fileCache = new FileIndexCache(Path.Combine(root, "FileCache"));
            fileCache.Save(solution, new[] { unrelated, asset, header });
            using var service = new SolutionFileIndexService(new SolutionSourceAnalyzer(cache), fileCache);
            service.Configure(new SolutionFileIndexConfiguration(true, true, TimeSpan.Zero));
            service.Start(new SolutionIndexDiscoveryResult(solution, roots, new[] { header }));
            service.WaitUntilReadyAsync().GetAwaiter().GetResult();
            service.WaitUntilAnalysisReadyAsync().GetAwaiter().GetResult();
            Check(service.FindByStem("UIActionRouterTypes").Contains(header), "프로젝트 외부 플러그인 헤더 검색");
            Check(!service.GetFilePathsSnapshot().Contains(unrelated) && !service.GetFilePathsSnapshot().Contains(asset),
                "구 파일 캐시에서도 무관한 코드·에셋 제외");
            var assetGeneration = service.Generation;
            File.Delete(asset);
            Thread.Sleep(1100);
            Check(service.Generation == assetGeneration, "미수집 에셋 삭제가 재인덱싱을 유발하지 않음");
            var fresh = Write(@"Engine\Plugins\Runtime\CommonUI\Source\CommonUI\Private\Fresh.cpp", "void FreshSymbol();");
            Until(() => service.FindSymbol("FreshSymbol").Count == 1, "새 파일 감시 및 심볼 갱신");
            File.WriteAllText(fresh, "void ChangedSymbol();");
            Until(() => service.FindSymbol("ChangedSymbol").Count == 1 && service.FindSymbol("FreshSymbol").Count == 0,
                "저장 후 이전 심볼 제거");
            Check(service.CompletionSnapshot.Find("ChangedSym").Any(symbol => symbol.Name == "ChangedSymbol"), "저장한 파일의 새 이름을 입력 추천에 반영");
            var renamed = Path.Combine(Path.GetDirectoryName(fresh)!, "Renamed.cpp");
            File.Move(fresh, renamed);
            Until(() => service.FindByStem("Fresh").Count == 0 && service.FindByStem("Renamed").Count == 1 &&
                service.FindSymbol("ChangedSymbol").All(symbol => symbol.Path == renamed), "이름 변경 후 경로 갱신");
            File.Delete(renamed);
            Until(() => service.FindByStem("Renamed").Count == 0 && service.FindSymbol("ChangedSymbol").Count == 0,
                "삭제 후 파일·심볼 제거");
            Check(service.Generation == assetGeneration && service.GetSnapshot().State == SolutionFileIndexState.Ready,
                "파일 생성·저장·이름 변경·삭제는 전체 다시 수집 없이 반영");
            service.Start(new SolutionIndexDiscoveryResult(solution, Array.Empty<string>(), new[] { header }), force: true);
            service.WaitUntilReadyAsync().GetAwaiter().GetResult();
            service.WaitUntilAnalysisReadyAsync().GetAwaiter().GetResult();
            Check(service.GetFilePathsSnapshot().Count == 1 && service.FindSymbol("ScopedSymbol").Count == 1,
                "범위 축소 후 이전 파일·심볼 캐시 재유입 방지");

            var many = Enumerable.Range(0, 600).Select(i => Write("Batch/Unit" + i + ".h", "void BatchSymbol();")).ToArray();
            var batchSolution = Path.Combine(root, "Batch.sln");
            using var analyzer = new SolutionSourceAnalyzer(cache);
            var partialSeen = false;
            analyzer.SymbolsPublished += count =>
            {
                if (count < many.Length && analyzer.FindSymbol("BatchSymbol").Count > 0) partialSeen = true;
            };
            analyzer.Analyze(batchSolution, many, Array.Empty<string>(), CancellationToken.None);
            Check(partialSeen && analyzer.FindSymbol("BatchSymbol").Count == many.Length, "전체 분석 완료 전 부분 검색 결과 공개");
            analyzer.LoadCachedSymbols(batchSolution, CancellationToken.None, new[] { many[0] });
            Check(analyzer.FindSymbol("BatchSymbol").Count == 1, "캐시 복원 시 현재 프로젝트 파일만 공개");

            var literal = new VisualBoost.Core.Analysis.SourceSymbolLocation("VeryLongToonRenderingSettings", header, 1, 1,
                VisualBoost.Core.Analysis.SourceSymbolKind.Function);
            var fuzzy = new VisualBoost.Core.Analysis.SourceSymbolLocation("ToOnSettings", header, 2, 1,
                VisualBoost.Core.Analysis.SourceSymbolKind.Function);
            var separated = new VisualBoost.Core.Analysis.SourceSymbolLocation("T_o_o_nSettings", header, 3, 1,
                VisualBoost.Core.Analysis.SourceSymbolKind.Function);
            foreach (var query in new[] { "Toon Settings", "Settings Toon" })
            {
                using var symbolIndex = new VisualBoost.Core.Analysis.SourceSymbolIndex();
                symbolIndex.ReplaceAll(new[] { separated, literal, fuzzy });
                var result = symbolIndex.Search(query, 3);
                Check(result.Last().Location == separated, "AND 전체 단어 포함 후보가 퍼지 후보보다 우선");
                Check(FuzzySymbolSearch.Search(query, new[] { separated, literal }, 1)[0].Location == literal,
                    "일반·스냅샷 심볼 검색 동일 정렬");
            }
            Check(FuzzyFileSearch.Search("Config", new[] { Path.Combine(root, "Config.md"), Path.Combine(root, "ConfigLong.cpp") }, 1)[0].Path.EndsWith(".cpp"),
                "같은 일치 등급에서는 코드 파일 우선");
            var delayedRefresh = typeof(SolutionFileIndexService).GetMethod("ScheduleRefreshNoLock",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            delayedRefresh.Invoke(service, null);
            service.Clear();
            var clearedGeneration = service.Generation;
            Thread.Sleep(1100);
            Check(service.GetSnapshot().State == SolutionFileIndexState.Empty && service.Generation == clearedGeneration,
                "솔루션 종료 후 예약된 감시 갱신이 이전 상태를 복구하지 않음");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Until(Func<bool> condition, string message)
    {
        var timeout = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < timeout) Thread.Sleep(50);
        Check(condition(), message);
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
