using System;
using System.IO;
using System.Threading;
using VisualBoost.Analysis;
using VisualBoost.Core.Coloring;
using VisualBoost.Services;
using VisualBoost.UI;

/// <summary>코드 미리보기 이름 판정: 실제 이름 인덱스에서 정확히 같은 이름만 세고, 인덱스 공개 번호가 바뀌면 캐시를 비웁니다.</summary>
internal static class CodePreviewNamesTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoostPreviewNames-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Source");
        Directory.CreateDirectory(source);
        try
        {
            var header = Path.Combine(source, "Names.h");
            File.WriteAllText(header, string.Join("\n",
                "#define UE_API GAME_API",
                "class UInputUserSettings { public: UInputUserSettings(); };",
                "void ComputeTotal(int value);",
                "namespace GameUi {}",
                "class Value {};"));
            var solution = Path.Combine(root, "Preview.sln");
            var service = new SolutionFileIndexService(new SolutionSourceAnalyzer(new SourceAnalysisCache(Path.Combine(root, "Cache"))),
                new FileIndexCache(Path.Combine(root, "FileCache")));
            try
            {
                service.Configure(new SolutionFileIndexConfiguration(true, true, TimeSpan.Zero));
                service.Start(new SolutionIndexDiscoveryResult(solution, new[] { source }, new[] { header }));
                service.WaitUntilReadyAsync().GetAwaiter().GetResult();
                service.WaitUntilAnalysisReadyAsync().GetAwaiter().GetResult();
                var names = new CodePreviewNames(service);
                Check(names.Resolve("UE_API") == CodePreviewKind.Macro, "헤더의 매크로");
                Check(names.Resolve("UInputUserSettings") == CodePreviewKind.Type, "타입과 생성자는 타입");
                Check(names.Resolve("ComputeTotal") == CodePreviewKind.Function, "함수");
                Check(names.Resolve("GameUi") == CodePreviewKind.Namespace, "네임스페이스");
                Check(names.Resolve("Value") == CodePreviewKind.Type && names.Resolve("value") is null, "대소문자만 다른 이름은 세지 않음");
                Check(names.Resolve("FreshName") is null, "없는 이름");

                // 새 이름이 인덱스에 공개되면 공개 번호가 바뀌어, 앞서 캐시한 '없음' 답을 버리고 다시 찾습니다.
                File.WriteAllText(Path.Combine(source, "Fresh.h"), "class FreshName {};");
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (service.FindSymbol("FreshName").Count == 0)
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("새 파일 분석 대기");
                    Thread.Sleep(50);
                }

                Check(names.Resolve("FreshName") == CodePreviewKind.Type, "인덱스 공개 번호가 바뀌면 캐시를 비움");
            }
            finally
            {
                service.Dispose();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: 코드 미리보기 이름 판정 " + message);
    }
}
