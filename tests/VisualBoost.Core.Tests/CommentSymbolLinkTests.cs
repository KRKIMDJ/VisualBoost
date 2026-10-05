using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.DocumentNavigation;

namespace VisualBoost.Core.Tests;

internal static class CommentSymbolLinkTests
{
    internal static void Run()
    {
        const string comment = "// Widget, Demo and Demo::Widget::Update(); Widget.h:20 https://host/Widget notWidget widget ordinary words";
        var parsed = CommentSymbolReferences.Parse(comment, n => n is "Widget" or "Demo");
        Check(parsed.Select(r => r.Name).SequenceEqual(new[] { "Widget", "Demo", "Demo::Widget::Update" }), "파일/URL/일반 문장 제외와 한정 이름");
        Check(parsed.All(r => comment.Substring(r.Start, r.Length) == r.Name), "밑줄 범위");
        Check(parsed[2].ShortName == "Update" && parsed[0].ShortName == "Widget", "조회 키");
        var sources = new Dictionary<string, string>
        {
            ["Widget.h"] = "namespace Demo {\nclass Widget {\npublic:\n void Update(int value);\n void Inline() {}\n};\n}",
            ["Widget.cpp"] = "namespace Demo {\nvoid Widget::Update(int value)\n{\n}\nvoid Widget::Update(float value) {}\n}\nnamespace Other {\nvoid Widget::Update(int value) {}\n}",
            ["Types.h"] = "namespace Tools::Detail\n{\nclass Helper {};\n}\nnamespace Demo {\n}\nclass Widget* GetWidget() { return nullptr; }\nclass Widget;\n"
        };
        var indexed = sources.SelectMany(s => CppSourceAnalyzer.Analyze(s.Key, s.Value).Symbols).ToArray();
        IReadOnlyList<SourceSymbolLocation> Resolve(string name) => CommentSymbolReferences.Resolve(new(0, name.Length, name), indexed, p => sources[p]);
        var definitions = Resolve("Demo::Widget::Update");
        Check(definitions.Count == 2 && definitions.All(s => s.Path == "Widget.cpp" && s.Kind == SourceSymbolKind.Function), "선언 제외 및 구현 오버로드 유지");
        Check(definitions.Select(s => s.Line).SequenceEqual(new[] { 2, 5 }), "구현 위치와 라인");
        Check(Resolve("Demo::Widget::Inline").Single().Path == "Widget.h", "헤더 인라인 구현 허용");
        Check(Resolve("Demo::Widget").Single().Kind == SourceSymbolKind.Class, "한정 클래스");
        Check(Resolve("Widget").Single().Path == "Widget.h", "단독 클래스");
        Check(Resolve("Demo").Count == 3, "재개방 네임스페이스 후보 유지");
        Check(Resolve("Tools::Detail").Single().Path == "Types.h", "축약 중첩 네임스페이스");
        Check(Resolve("tools::Detail").Count == 0, "대소문자 구분");
        Check(Resolve("Demo::Widget::Missing").Count == 0, "없는 함수 제외");
        sources["Widget.cpp"] = "// void Demo::Widget::Update(int value) {}\n";
        Check(Resolve("Demo::Widget::Update").Count == 0, "오래된 인덱스의 삭제된 구현 재검증");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { CommentSymbolReferences.Resolve(new(0, 6, "Widget"), indexed, p => sources[p], cancel.Token); throw new Exception("취소 누락"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: 주석 심볼 링크 파싱·구현 판별·인라인·클래스·namespace·동명·최신 소스·취소");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
