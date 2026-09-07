using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VisualBoost.Core.CodeGeneration;

namespace VisualBoost.Core.Tests;

internal static class CodeGenerationCompileTests
{
    public static void Run()
    {
        var compiler = Environment.GetEnvironmentVariable("VISUALBOOST_TEST_CXX");
        if (string.IsNullOrWhiteSpace(compiler))
        { Console.WriteLine("SKIP: C++ 컴파일 검증은 VISUALBOOST_TEST_CXX에 cl.exe 경로를 지정하세요."); return; }
        if (!File.Exists(compiler)) throw new FileNotFoundException("C++ compiler not found", compiler);
        var folder = Path.Combine(Path.GetTempPath(), "VisualBoost-Generation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var provider = new CppGenerationProvider();
        const string header = "#pragma once\nnamespace Game { class Widget { public: using Result = int; explicit Widget(int count = 0); ~Widget(); virtual void Reset(int count = 1) const noexcept; static Result Find(const char* name); }; }\n";
        var definitions = "#include \"Widget.h\"\n";
        foreach (var spec in new[] { ("explicit Widget(int count = 0);", "Widget"), ("~Widget();", "~Widget"), ("virtual void Reset(int count = 1) const noexcept;", "Reset"), ("static Result Find(const char* name);", "Find") })
        {
            var fn = CodeGenerationTests.Function(header, spec.Item1, spec.Item2, false, "Game::Widget");
            var plan = provider.Create(header, fn, definitions, GenerationDirection.Definition, "Widget.h");
            definitions = definitions.Insert(plan.Offset, plan.Text);
        }
        File.WriteAllText(Path.Combine(folder, "Widget.h"), header);
        File.WriteAllText(Path.Combine(folder, "Widget.cpp"), definitions);
        Compile(false, "VisualBoost: implement return value");
        // 제품은 미완성 반환값을 강제 표시합니다. 이 테스트에서만 사용자가 본문을 완성한 상황을 구성합니다.
        definitions = definitions.Replace("static_assert(false, \"VisualBoost: implement return value\");", "return 1;");
        File.WriteAllText(Path.Combine(folder, "Widget.cpp"), definitions);
        Compile(true);
        const string reverse = "void Game::Widget::Extra(int count) & noexcept(false) {}";
        var cls = new GenerationClass("Game::Widget", header.IndexOf("class", StringComparison.Ordinal), header.IndexOf("Widget", StringComparison.Ordinal), header.IndexOf("};", StringComparison.Ordinal) + 2);
        var declaration = provider.Create(reverse, CodeGenerationTests.Function(reverse, reverse, "Extra", true, "Game::Widget"), header, GenerationDirection.Declaration, "Widget.h", cls, "protected");
        File.WriteAllText(Path.Combine(folder, "Widget.h"), header.Insert(declaration.Offset, declaration.Text));
        File.WriteAllText(Path.Combine(folder, "Widget.cpp"), definitions + reverse);
        Compile(true);
        const string orderedHeader = "#pragma once\nnamespace Game { class Widget { public:\n    void First(int value);\n    void Missing();\n    void Last();\n}; }\n";
        var orderedSource = "#include \"Widget.h\"\nnamespace Game {\nvoid Widget::First(int value) {}\nvoid Widget::Last() {}\n}\n";
        var orderedPlan = provider.Create(orderedHeader, CodeGenerationTests.Function(orderedHeader, "void Missing();", "Missing", false, "Game::Widget"), orderedSource, GenerationDirection.Definition, "Widget.h");
        orderedSource = orderedSource.Insert(orderedPlan.Offset, orderedPlan.Text);
        File.WriteAllText(Path.Combine(folder, "Widget.h"), orderedHeader);
        File.WriteAllText(Path.Combine(folder, "Widget.cpp"), orderedSource);
        Compile(true);
        var missingHeader = orderedHeader.Replace("    void Missing();\n", "");
        var orderedClass = new GenerationClass("Game::Widget", missingHeader.IndexOf("class", StringComparison.Ordinal), missingHeader.IndexOf("Widget", StringComparison.Ordinal), missingHeader.IndexOf("};", StringComparison.Ordinal) + 2);
        // 역방향 배치까지 컴파일해 접근 수준과 namespace 내부 생성 위치를 확인합니다.
        var reverseOrdered = "void Game::Widget::First(int value) {}\nvoid Game::Widget::Missing() {}\nvoid Game::Widget::Last() {}\n";
        var reverseOrderedPlan = provider.Create(reverseOrdered, CodeGenerationTests.Function(reverseOrdered, "void Game::Widget::Missing() {}", "Missing", true, "Game::Widget"), missingHeader, GenerationDirection.Declaration, "Widget.h", orderedClass, "public");
        File.WriteAllText(Path.Combine(folder, "Widget.h"), missingHeader.Insert(reverseOrderedPlan.Offset, reverseOrderedPlan.Text));
        Compile(true);
        Console.WriteLine("PASS: MSVC C++17 정·역방향 생성 결과 컴파일 및 미완성 반환 본문의 의도된 실패");
        Console.WriteLine("C++ test artifacts: " + folder);

        void Compile(bool expectedSuccess, string requiredDiagnostic = "")
        {
            var start = new ProcessStartInfo(compiler!) { WorkingDirectory = folder, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { "/nologo", "/utf-8", "/std:c++17", "/EHsc", "/c", "/W4", "/WX", "/wd4100", "Widget.cpp" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(); throw new Exception("C++ compile timed out"); }
            var log = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            if ((process.ExitCode == 0) != expectedSuccess || (requiredDiagnostic.Length > 0 && !log.Contains(requiredDiagnostic, StringComparison.Ordinal)))
                throw new Exception("C++ compile expectation failed: " + log);
        }
    }
}
