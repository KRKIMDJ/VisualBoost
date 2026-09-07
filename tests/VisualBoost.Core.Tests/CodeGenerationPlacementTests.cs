using System;
using VisualBoost.Core.CodeGeneration;

namespace VisualBoost.Core.Tests;

internal static class CodeGenerationPlacementTests
{
    public static void Run()
    {
        const string header = "class Widget\n{\npublic:\n    void First(int value);\n    void Missing();\n    void Last();\n};\n";
        const string target = "void Widget::First(int other) {} // 앞 함수\n\n// 뒤 함수 설명\nvoid Widget::Last() {}\n";
        var provider = new CppGenerationProvider();
        var function = CodeGenerationTests.Function(header, "void Missing();", "Missing", false);
        string Define(string destination, string input = header, GenerationFunction? fn = null)
        { var p = provider.Create(input, fn ?? function, destination, GenerationDirection.Definition, "Widget.h"); return destination.Insert(p.Offset, p.Text); }
        var result = Define(target);
        Check(result.IndexOf("앞 함수", StringComparison.Ordinal) < result.IndexOf("Widget::Missing", StringComparison.Ordinal) && result.IndexOf("Widget::Missing", StringComparison.Ordinal) < result.IndexOf("뒤 함수 설명", StringComparison.Ordinal), "위 함수 뒤 배치·주석 보호");
        var below = Define("// 뒤 함수 설명\nvoid Widget::Last() {}\n");
        Check(below.IndexOf("Widget::Missing", StringComparison.Ordinal) < below.IndexOf("뒤 함수 설명", StringComparison.Ordinal), "위 대응이 없으면 아래 함수의 설명 앞");
        var misleadingComment = Define("void Widget::First(int x) {} // /* 설명\nvoid Widget::Last() { /* 본문 */ }\n");
        Check(misleadingComment.IndexOf("Widget::Missing", StringComparison.Ordinal) < misleadingComment.IndexOf("Widget::Last", StringComparison.Ordinal), "줄 주석 안의 블록 주석 표기를 구문으로 오인하지 않음");
        var multilineComment = Define("/* 아래 함수\n\n설명 */\nvoid Widget::Last() {}\n");
        Check(multilineComment.IndexOf("Widget::Missing", StringComparison.Ordinal) < multilineComment.IndexOf("/* 아래", StringComparison.Ordinal), "빈 줄을 포함한 설명 주석 내부 삽입 방지");
        var reverseOrder = Define("void Widget::Last() {}\nvoid Widget::First(int x) {}\n");
        Check(reverseOrder.LastIndexOf("Widget::Missing", StringComparison.Ordinal) > reverseOrder.LastIndexOf("Widget::First", StringComparison.Ordinal), "대상 순서가 달라도 위 함수 우선");
        var wrongClass = Define("void Other::First(int x) {}\nvoid Widget::Last() {}\n");
        Check(wrongClass.IndexOf("Widget::Missing", StringComparison.Ordinal) < wrongClass.IndexOf("Widget::Last", StringComparison.Ordinal), "다른 클래스의 동명 함수 제외");
        var overloaded = Define("void Widget::First(float x) {}\nvoid Widget::Last() {}\nvoid Widget::First(int x) {}\n");
        Check(overloaded.LastIndexOf("Widget::Missing", StringComparison.Ordinal) > overloaded.LastIndexOf("Widget::First(int", StringComparison.Ordinal), "오버로드 인수 기준 대응");
        var nsHeader = "namespace A {\n" + header + "}\n";
        var nsFn = CodeGenerationTests.Function(nsHeader, "void Missing();", "Missing", false, "A::Widget");
        var namespaced = Define("namespace A {\nvoid Widget::First(int x) {}\nvoid Widget::Last() {}\n}\n", nsHeader, nsFn);
        Check(namespaced.IndexOf("A::Widget::Missing", StringComparison.Ordinal) < namespaced.IndexOf("Widget::Last", StringComparison.Ordinal), "namespace 내부 한정 이름 연결");
        const string implementation = "void Widget::First(int x) {}\nvoid Widget::Missing() {}\nvoid Widget::Last() {}\n";
        const string destination = "class Widget\n{\npublic:\n    void First(int value);\n    // 마지막 설명\n    void Last();\n};\n";
        string Declare(string input, string access = "public")
        {
            var cls = new GenerationClass("Widget", 0, input.IndexOf("Widget", StringComparison.Ordinal), input.LastIndexOf("};", StringComparison.Ordinal) + 2);
            var fn = CodeGenerationTests.Function(implementation, "void Widget::Missing() {}", "Missing", true);
            var p = provider.Create(implementation, fn, input, GenerationDirection.Declaration, "Widget.h", cls, access);
            return input.Insert(p.Offset, p.Text);
        }
        Check(Declare(destination).Contains("void First(int value);\n    void Missing();\n    // 마지막 설명"), "선언도 위 함수 뒤·동일 접근 수준·들여쓰기");
        Check(Declare(destination, "private").Contains("private:\n    void Missing();"), "이웃 접근 수준이 다르면 선택한 구역으로 안전 복귀");
        var onlyBelow = Declare(destination.Replace("    void First(int value);\n", ""));
        Check(onlyBelow.IndexOf("void Missing", StringComparison.Ordinal) < onlyBelow.IndexOf("마지막 설명", StringComparison.Ordinal), "선언의 아래 이웃 및 설명 보존");
        try { Declare(destination.Replace("    void First(int value);", "#if FEATURE\n    void First(int value);\n#endif")); throw new Exception("조건부 문서 차단 누락"); }
        catch (GenerationNotSupportedException) { }
        var crlf = Define(target.Replace("\n", "\r\n"));
        Check(!crlf.Replace("\r\n", "").Contains('\n'), "이웃 삽입도 CRLF 보존");
        Console.WriteLine("PASS: 주변 함수 순서·위쪽 우선·아래쪽 대안·소속·오버로드·namespace·접근 수준·주석·CRLF 배치");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
