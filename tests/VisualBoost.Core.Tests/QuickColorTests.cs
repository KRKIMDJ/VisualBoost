using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Core.Tests;

internal static class QuickColorTests
{
    internal static void Run()
    {
        const string code = "#define LIMIT 8\nnamespace Demo::Detail { class Widget {}; struct Record {}; interface IService {}; enum class Mode { Idle, Active = (1 << 2), Last }; typedef int Count; using Alias = Widget; void Update(int value) { Widget item; Update(value); Mode mode = Mode::Idle; int count = LIMIT; } }";
        foreach (var name in new[] { "Widget", "Record", "IService", "Mode", "Count", "Alias" }) Expect(code, name, SemanticColorKind.Type);
        foreach (var name in new[] { "value", "item", "mode", "count" }) Expect(code, name, SemanticColorKind.Variable);
        foreach (var name in new[] { "Idle", "Active", "Last" }) Expect(code, name, SemanticColorKind.EnumMember);
        Expect(code, "Demo", SemanticColorKind.Namespace);
        Expect(code, "Detail", SemanticColorKind.Namespace);
        Expect(code, "LIMIT", SemanticColorKind.Macro);
        Expect(code, "Update", SemanticColorKind.Function);
        var spans = CppQuickColorScanner.Scan(code);
        Assert(spans.Select(s => s.Start).SequenceEqual(spans.Select(s => s.Start).OrderBy(s => s)), "색상 범위 정렬");
        Assert(spans.All(s => s.Start >= 0 && s.Start + s.Length <= code.Length), "범위 유효성");

        const string ignored = "// class Ghost {}; \\\nvoid Hidden();\n/* namespace Fake { } */\nconst char* text = u8R\"tag(class RawGhost {}; Run();)tag\";\nconst char* other = \"Call(\\\"class StringGhost\\\")\";\nchar ch = '\\'';\n#include \"HiddenHeader.h\"\n#define FLAG(x) \\\nIgnoredMacroBody(x)\nUNKNOWN_MACRO(); External value;";
        foreach (var name in new[] { "Ghost", "Hidden", "Fake", "RawGhost", "Run", "Call", "StringGhost", "HiddenHeader", "IgnoredMacroBody", "UNKNOWN_MACRO", "External", "value" }) Absent(ignored, name);
        Expect(ignored, "FLAG", SemanticColorKind.Macro);
        Expect("enum { First, Second = Make(1, 2), Third };", "Third", SemanticColorKind.EnumMember);
        Expect("class GAME_API Player {}; Player p;", "Player", SemanticColorKind.Type);
        Absent("class Widget {}; widget;", "widget");
        // 같은 이름의 서로 다른 분류를 문서 전체로 전파하지 않습니다.
        var collision = "class Shared {}; void F() { int Shared; } Shared;";
        var last = collision.LastIndexOf("Shared", StringComparison.Ordinal);
        Assert(!CppQuickColorScanner.Scan(collision).Any(s => s.Start == last), "서로 다른 분류의 이름 충돌 보류");
        Expect("int before;", "before", SemanticColorKind.Variable);
        Absent("// int before;", "before");
        Assert(CppQuickColorScanner.Scan(new string('x', CppQuickColorScanner.MaximumLength + 1)).Count == 0, "초대형 문서 상한");
        Assert(CppQuickColorScanner.Scan(string.Concat(Enumerable.Repeat("x;", 126000))).Count == 0, "토큰 상한");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { CppQuickColorScanner.Scan(code, cancelled.Token); throw new InvalidOperationException("취소 누락"); }
        catch (OperationCanceledException) { }

        foreach (var lines in new[] { 2000, 10000 })
        {
            var fixture = "class Widget {};\n" + string.Concat(Enumerable.Range(0, lines).Select(i => $"void Method{i}(int value{i}) {{ Widget item{i}; }}\n"));
            var clock = Stopwatch.StartNew();
            var parsed = CppQuickColorScanner.Scan(fixture);
            clock.Stop();
            Assert(parsed.Count >= lines * 4, "대형 독립 샘플의 분류 결과");
            Assert(clock.Elapsed < TimeSpan.FromSeconds(5), "선형 처리 시간 회귀");
            Console.WriteLine($"COLOR: {lines:N0} lines / {fixture.Length:N0} chars / {clock.Elapsed.TotalMilliseconds:F1} ms");
        }
        var broken = string.Concat(Enumerable.Repeat("enum Broken { A, ", 8000));
        var timer = Stopwatch.StartNew();
        CppQuickColorScanner.Scan(broken);
        Assert(timer.Elapsed < TimeSpan.FromSeconds(5), "닫히지 않은 enum의 재검사 예산");
        using var running = new CancellationTokenSource();
        running.CancelAfter(1);
        try { CppQuickColorScanner.Scan(new string(' ', CppQuickColorScanner.MaximumLength), running.Token); }
        catch (OperationCanceledException) { }
    }

    private static void Expect(string code, string name, SemanticColorKind kind)
    {
        var matches = CppQuickColorScanner.Scan(code).Where(s => code.Substring(s.Start, s.Length) == name).ToArray();
        Assert(matches.Length > 0 && matches.All(s => s.Kind == kind), name + " 분류: " + kind);
    }
    private static void Absent(string code, string name) => Assert(!CppQuickColorScanner.Scan(code).Any(s => code.Substring(s.Start, s.Length) == name), name + " 보수적 생략");
    private static void Assert(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
}
