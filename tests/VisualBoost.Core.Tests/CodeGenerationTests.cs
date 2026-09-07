using System;
using System.IO;
using System.Linq;
using System.Threading;
using VisualBoost.Core.CodeGeneration;

namespace VisualBoost.Core.Tests;

internal static class CodeGenerationTests
{
    public static void Run()
    {
        var provider = new CppGenerationProvider();
        const string declaration = "virtual void Update(int count = Clamp(1, 2), const std::vector<int>& values = {1, 2}) const noexcept;";
        var source = "namespace Game { class Widget { public: " + declaration + " }; }";
        var fn = Function(source, declaration, "Update", false, "Game::Widget");
        const string target = "#include \"Widget.h\"\n";
        var plan = provider.Create(source, fn, target, GenerationDirection.Definition, "Widget.h");
        Check(plan.Signature == "void Game::Widget::Update(int count, const std::vector<int>& values) const noexcept", "기본 인수 중첩 제거·소속·const/noexcept");
        Check(plan.Offset == target.Length && !plan.Text.Contains("virtual"), "선언 전용 한정자 제거 및 파일 끝 삽입");
        Reject(() => provider.Create(source, fn, target + plan.Text, GenerationDirection.Definition, "Widget.h"), "두 번째 실행 중복 차단");
        const string reverse = "void Game::Widget::Reset(int count) & noexcept(false) { Call(); }";
        const string header = "#ifndef WIDGET_H\n#define WIDGET_H\nnamespace Game {\nclass Widget\n{\n};\n}\n#endif\n";
        var cls = new GenerationClass("Game::Widget", header.IndexOf("class"), header.IndexOf("Widget"), header.IndexOf("};") + 2);
        var reversePlan = provider.Create(reverse, Function(reverse, reverse, "Reset", true, "Game::Widget"), header, GenerationDirection.Declaration, "Widget.h", cls, "protected");
        Check(reversePlan.Signature == "void Reset(int count) & noexcept(false);" && reversePlan.Text.Contains("protected:"), "역방향 한정·접근 수준 유지");
        Check(header.Insert(reversePlan.Offset, reversePlan.Text).Contains("    void Reset"), "클래스 내부 들여쓰기");
        const string nonVoid = "static Result Find(const char* value = R\"tag(a,b)tag\");";
        var typeSource = "class Widget { public: " + nonVoid + " };";
        var nonVoidPlan = provider.Create(typeSource, Function(typeSource, nonVoid, "Find", false), target, GenerationDirection.Definition, "Widget.h");
        Check(nonVoidPlan.Signature == "auto Widget::Find(const char* value) -> Result" && nonVoidPlan.Text.Contains("static_assert(false"), "중첩 반환형 범위 및 미완성 반환값 차단");
        const string ctor = "explicit Widget(int x = 0);";
        var ctorSource = "class Widget { public: " + ctor + " };";
        Check(provider.Create(ctorSource, Function(ctorSource, ctor, "Widget", false), target, GenerationDirection.Definition, "Widget.h").Signature == "Widget::Widget(int x)", "생성자 반환형 없음");
        const string overload = "void Update(int count);";
        var overloadSource = "class Widget { " + overload + " };";
        var overloadFn = Function(overloadSource, overload, "Update", false);
        Check(provider.Create(overloadSource, overloadFn, target + "void Widget::Update(float count) {}", GenerationDirection.Definition, "Widget.h").Text.Contains("int count"), "명확한 기본형 오버로드 허용");
        const string alias = "void Update(unsigned count);";
        var aliasSource = "class Widget { " + alias + " };";
        Reject(() => provider.Create(aliasSource, Function(aliasSource, alias, "Update", false), target + "void Widget::Update(unsigned int other) {}", GenerationDirection.Definition, "Widget.h"), "동등한 기본형 표기 중복 차단");
        foreach (var withoutInclude in new[] { "", "// no direct include\n", "/*\n#include \"Widget.h\"\n*/", "#include \"pch.h\"\n", "#include \"Widget.h\"\n" })
        {
            var allowed = provider.Create(source, fn, withoutInclude, GenerationDirection.Definition, "DifferentModelPath.h");
            Check(allowed.Text.Contains("void Game::Widget::Update") && !allowed.Text.Contains("#include"), "include 부재·간접 의존성·모델 헤더 불일치에도 생성, include 자동 변경 없음");
        }
        const string simpleHeader = "#pragma once\r\nclass TestClass\r\n{\r\npublic:\r\n\tvoid TestFunction();\r\n};\r\n";
        Check(provider.Create(simpleHeader, Function(simpleHeader, "void TestFunction();", "TestFunction", false, "TestClass"), "#include \"TestClass.h\"\r\n", GenerationDirection.Definition, "TestClass.h").Signature == "void TestClass::TestFunction()", "제보된 최소 클래스·직접 include 구성");
        Reject(() => provider.Create(source, fn, target + "/*", GenerationDirection.Definition, "Widget.h"), "미완성 주석 차단");
        Reject(() => provider.Create(source, fn, target + "namespace Broken {", GenerationDirection.Definition, "Widget.h"), "미완성 범위 차단");
        foreach (var unsupported in new[] { "inline void Update();", "constexpr int Update();", "void Update() = 0;", "void Update() = delete;", "void Update(int x[4]);", "void Update() noexcept(Check());", "void Update(...) ;", "MYLIB_API int Update();", "void __stdcall Update();" })
        {
            var text = "class Widget { " + unsupported + " };";
            Reject(() => provider.Create(text, Function(text, unsupported, "Update", false), target, GenerationDirection.Definition, "Widget.h"), "미지원 구문 차단: " + unsupported);
        }
        var macro = "class Widget { UFUNCTION(BlueprintNativeEvent) void Update(); };";
        Reject(() => provider.Create(macro, Function(macro, "void Update();", "Update", false), target, GenerationDirection.Definition, "Widget.h"), "메타데이터 함수 그룹 10 분리");
        var conditional = "#if MODE\n" + source + "\n#endif";
        Reject(() => provider.Create(conditional, Function(conditional, declaration, "Update", false, "Game::Widget"), target, GenerationDirection.Definition, "Widget.h"), "조건부 컴파일 차단");
        var commented = "void Update(int count // 설명\n, float value = 1);";
        var commentSource = "class Widget { " + commented + " };";
        Check(provider.Create(commentSource, Function(commentSource, commented, "Update", false), target, GenerationDirection.Definition, "Widget.h").Signature == "void Widget::Update(int count, float value)", "줄 주석이 후속 매개변수를 삼키지 않음");
        var crlf = provider.Create(source, fn, target.Replace("\n", "\r\n"), GenerationDirection.Definition, "Widget.h");
        Check(!crlf.Text.Replace("\r\n", "").Contains('\n'), "대상 줄바꿈 유지");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { provider.Create(source, fn, target, GenerationDirection.Definition, "Widget.h", token: cancellation.Token); throw new Exception("취소 누락"); } catch (OperationCanceledException) { }
        var root = Path.GetFullPath("SafeGenerationFixture");
        GenerationPathPolicy.Validate(root, Path.Combine(root, "Source", "Widget.cpp"));
        foreach (var path in new[] { Path.Combine(root + "Outside", "Widget.cpp"), Path.Combine(root, "Engine", "Widget.cpp"), Path.Combine(root, "Vendor", "Widget.h"), Path.Combine(root, "Widget.gen.cpp") })
            Reject(() => GenerationPathPolicy.Validate(root, path), "외부·생성 파일 보호");
        Console.WriteLine("PASS: 생성 방향·기본 인수·한정자·클래스 삽입·오버로드·중복·미지원·경로 보호");
        CodeGenerationCompileTests.Run();
        CodeGenerationPlacementTests.Run();
    }
    public static GenerationFunction Function(string source, string fragment, string name, bool definition, string owner = "Widget")
    {
        var start = source.IndexOf(fragment, StringComparison.Ordinal);
        return new GenerationFunction(name, owner, start, source.IndexOf(name + "(", start, StringComparison.Ordinal), start + fragment.Length, definition);
    }
    private static void Reject(Action action, string message) { try { action(); } catch (GenerationNotSupportedException) { return; } throw new Exception(message); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
