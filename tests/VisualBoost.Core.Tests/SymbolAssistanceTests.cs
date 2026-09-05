using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.Tests;

internal static class SymbolAssistanceTests
{
    public static void Run()
    {
        var kinds = CppSourceAnalyzer.Analyze("Kinds.h", "class Actor {};\nstruct Data {};\nunion Value {};\nenum class Mode { First };\nenum struct State { Ready };\nvoid Function();\nint Variable;\n#define FLAG 1\n").Symbols;
        Check(kinds.Single(s => s.Name == "Actor").Kind == SourceSymbolKind.Class &&
              kinds.Single(s => s.Name == "Data").Kind == SourceSymbolKind.Struct &&
              kinds.Single(s => s.Name == "Value").Kind == SourceSymbolKind.Union &&
              kinds.Single(s => s.Name == "Mode").Kind == SourceSymbolKind.Enum &&
              kinds.Single(s => s.Name == "State").Kind == SourceSymbolKind.Enum &&
              kinds.Single(s => s.Name == "Function").Kind == SourceSymbolKind.Function &&
              kinds.Single(s => s.Name == "Variable").Kind == SourceSymbolKind.Variable &&
              kinds.Single(s => s.Name == "FLAG").Kind == SourceSymbolKind.Macro, "심볼 상세 종류 구분");
        const string source = """
            namespace Game {
            UCLASS()
            class Actor {
            public:
                void Move(float distance);
                void Move(Vector direction) const;
                void Configure(
                    int count,
                    const char* text);
            };
            void Actor::Move(float distance)
            {
                const char* noise = "class Fake { void Phantom(); }";
            }
            #define OPEN {
            void FreeFunction();
            }
            void GlobalFunction();
            """;
        var symbols = CppSourceAnalyzer.Analyze("Actor.cpp", source).Symbols;
        var moves = symbols.Where(s => s.Name == "Move").ToArray();
        Check(moves.Length == 3 && moves.All(s => s.Scope == "Game::Actor"), "동명 오버로드와 외부 구현의 소속");
        Check(moves.Select(s => s.Signature).Distinct().Count() == 2, "오버로드 인수와 const 표시");
        Check(moves[1].Signature == "(Vector direction) const", "함수 한정자 유지");
        Check(moves[2].Column == "void Actor::".Length + 1, "한정 함수 이름의 실제 이동 열");
        var configure = symbols.Single(s => s.Name == "Configure");
        Check(configure.Signature == "( int count, const char* text)" && configure.Scope == "Game::Actor", "여러 줄 인수 목록");
        Check(!symbols.Any(s => s.Name is "Fake" or "Phantom"), "문자열 내부 선언 제외");
        Check(symbols.Single(s => s.Name == "FreeFunction").Scope == "Game", "매크로 중괄호가 소속에 영향 없음");
        Check(symbols.Single(s => s.Name == "GlobalFunction").Scope == "", "네임스페이스 종료 복원");
        using var index = new SourceSymbolIndex();
        index.ReplaceAll(symbols);
        Check(index.Search("distance").Count == 0 && index.Search("Game").All(s => s.Location.Name == "Game"), "인수·소속을 이름 검색 대상으로 확장하지 않음");
        var old = index.CompletionSnapshot;
        Check(old.Find("Mov").Count == 1, "자동완성은 선언·정의·오버로드를 이름당 한 번 표시");
        index.ReplaceAll(Array.Empty<SourceSymbolLocation>());
        Check(index.CompletionSnapshot.Find("Mov").Count == 0 && old.Find("Mov").Count == 1, "자동완성 스냅샷 교체와 불변성");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try { old.Find("Mov", token: cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "자동완성 조회 취소");

        foreach (var text in new[] { "abc", "  SetMove", "return SetMove" })
            Check(CompletionInput.TryGetPrefix(text, text.Length, 3, out _, out _), "일반 이름 입력 " + text);
        foreach (var text in new[] { "ab", "123abc", "obj.SetMove", "obj->SetMove", "Game::SetMove", "#include SetMove", "// SetMove", "\"SetMove", "/* SetMove" })
            Check(!CompletionInput.TryGetPrefix(text, text.Length, 3, out _, out _), "간섭 방지 " + text);
        Check(!CompletionInput.TryGetPrefix("SetMove", 3, 3, out _, out _), "단어 중간 입력은 기존 텍스트를 덮어쓰지 않음");

        var watch = Stopwatch.StartNew();
        var large = new SymbolCompletionSnapshot(Enumerable.Range(0, 100_000)
            .Select(i => new SourceSymbolLocation("Symbol" + i.ToString("D6"), "Test.h", i + 1, 1, SourceSymbolKind.Function)));
        var buildMs = watch.Elapsed.TotalMilliseconds;
        large.Find("Symbol099");
        var timings = new double[1000];
        for (var i = 0; i < timings.Length; i++)
        {
            watch.Restart();
            var items = large.Find("Symbol099");
            timings[i] = watch.Elapsed.TotalMilliseconds;
            Check(items.Count == 30, "대규모 추천 결과 제한", quiet: true);
        }
        Array.Sort(timings);
        Console.WriteLine($"PASS: 100,000개 이름 스냅샷 구축 {buildMs:F2} ms · 조회 p95 {timings[950]:F3} ms / 1,000회");
        Console.WriteLine("PASS: 심볼 인수·소속·이름 전용 검색 및 자동완성 경계 검증");
    }

    private static void Check(bool condition, string message, bool quiet = false)
    {
        if (!condition) throw new InvalidOperationException(message);
        if (!quiet) Console.WriteLine("PASS: " + message);
    }
}
