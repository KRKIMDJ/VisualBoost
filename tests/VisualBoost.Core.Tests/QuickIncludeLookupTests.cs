using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.CodeGeneration;

namespace VisualBoost.Core.Tests;

internal static class QuickIncludeLookupTests
{
    public static void Run()
    {
        const string source = @"C:\Test\TestClass.cpp";
        const string header = @"C:\Test\MyClass.h";
        var empty = Array.Empty<SourceSymbolLocation>();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [header] = "#pragma once\nclass MyClass\n{ public: void MyFunction(); };" };
        var reads = 0;
        string? Read(string path) { reads++; return files.TryGetValue(path, out var text) ? text : null; }
        IReadOnlyList<string> Find(string name = "MyClass", string owner = "", IEnumerable<SourceSymbolLocation>? symbols = null, IEnumerable<string>? named = null) =>
            QuickIncludeHeaderLookup.Find(source, @"C:\Test", name, owner, symbols ?? empty, named ?? new[] { header }, Read);
        Check(QuickInclude.RankHeaders(source, @"C:\Test", empty.Select(s => s.Path)).Count == 0, "수정 전 빈 심볼 인덱스의 후보 누락 재현");
        Check(Find().SequenceEqual(new[] { header }), "수정 후 동명 헤더 선언 확인");
        Check(Find(named: Array.Empty<string>()).SequenceEqual(new[] { header }), "파일 이벤트보다 빠른 실행도 같은 폴더 헤더 확인");
        files[header] = "// class MyClass {};\nclass Other {};";
        Check(Find().Count == 0, "파일명/주석만 같으면 include하지 않음");
        files[header] = "class MyClass;"; Check(Find().Count == 0, "전방 선언만 있는 헤더 제외");
        foreach (var declaration in new[] { "class MyClass;", "struct MyClass;", "enum class MyClass;", "union MyClass;" })
            Check(!CppSourceAnalyzer.Analyze(header, declaration).Symbols.Any(s => s.Name == "MyClass"), "전방 선언의 변수 오인 방지");
        Check(CppSourceAnalyzer.Analyze(header, "struct MyClass* instance;").Symbols.Any(s => s.Name == "instance" && s.Kind == SourceSymbolKind.Variable), "타입 지정 변수는 유지");
        files[header] = "class myclass {};"; Check(Find().Count == 0, "C++ 대소문자 구분");
        files[header] = "namespace A {\nclass MyClass {};\n}";
        Check(Find(owner: "B").Count == 0 && Find(owner: "A").Count == 1, "명시적 namespace 보존");
        reads = 0;
        var cached = new[] { new SourceSymbolLocation("MyClass", @"C:\Test\Types.h", 1, 1, SourceSymbolKind.Class) };
        Check(Find(symbols: cached)[0] == cached[0].Path && reads == 0, "기존 인덱스 조회는 추가 파일 읽기 없음");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { QuickIncludeHeaderLookup.Find(source, null, "MyClass", "", empty, new[] { header }, Read, cancel.Token); throw new Exception("취소 누락"); } catch (OperationCanceledException) { }
        var many = Enumerable.Range(0, 100).Select(i => $@"C:\Test\Dir{i}\MyClass.h"); reads = 0;
        QuickIncludeHeaderLookup.Find(source, null, "MyClass", "", empty, many, _ => { reads++; return "class Other {};"; });
        Check(reads <= 16, "누락 조회의 파일 수 상한");
        ReadOnlyProjectProbe();
        Console.WriteLine("PASS: 새 헤더의 빠른 include 누락 재현·선언 검증·인덱스 빠른 경로·상한·취소");
    }
    private static void ReadOnlyProjectProbe()
    {
        var root = Environment.GetEnvironmentVariable("VISUALBOOST_TEST_INCLUDE_PROJECT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var source = Path.Combine(root, "TestClass.cpp"); var header = Path.Combine(root, "MyClass.h");
        var beforeSource = File.ReadAllBytes(source); var beforeHeader = File.ReadAllBytes(header);
        var sourceText = File.ReadAllText(source);
        var result = QuickIncludeHeaderLookup.Find(source, root, "MyClass", "", Array.Empty<SourceSymbolLocation>(), new[] { header }, p => File.Exists(p) ? File.ReadAllText(p) : null);
        Check(result.Count == 1 && result[0] == header, "실제 테스트 프로젝트에서 MyClass 선언 헤더 확인");
        var plan = QuickInclude.Create(sourceText, source, result[0]);
        Check(plan.Text.Contains("#include \"MyClass.h\""), "실제 테스트 프로젝트의 상대 include 계획");
        Check(beforeSource.SequenceEqual(File.ReadAllBytes(source)) && beforeHeader.SequenceEqual(File.ReadAllBytes(header)), "테스트 프로젝트 원본 무변경");
        Console.WriteLine("PASS: 지정된 프로젝트 읽기 전용 검증 · MyClass.h 선택 및 include 계획 · 원본 바이트 일치");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
