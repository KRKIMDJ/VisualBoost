using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using VisualBoost.Core.CodeGeneration;
using VisualBoost.Core.DocumentNavigation;

namespace VisualBoost.Core.Tests;

internal static class EditorToolsTests
{
    public static void Run()
    {
        const string sourcePath = @"C:\Work\Source\Foo.cpp";
        const string header = @"C:\Work\Include\Widget.h";
        string Insert(string text) { var p = QuickInclude.Create(text, sourcePath, header); return text.Insert(p.Offset, p.Text); }
        Check(QuickInclude.IncludePath(sourcePath, header) == "../Include/Widget.h", "상대 경로 및 슬래시");
        Check(QuickInclude.IncludePath(sourcePath, @"D:\UE\Engine\Source\Runtime\Engine\Classes\GameFramework\Actor.h", true) == "GameFramework/Actor.h", "Unreal 공개 include 루트 기준 경로");
        Check(QuickInclude.IncludePath(sourcePath, @"C:\Work\Source\Public\Thing.h") == "Public/Thing.h", "일반 Source/Public 폴더를 Unreal로 추정하지 않음");
        Check(Insert("#include \"Foo.h\"\r\n\r\nvoid Work() {}") == "#include \"Foo.h\"\r\n#include \"../Include/Widget.h\"\r\n\r\nvoid Work() {}", "CPP 자기 헤더 바로 뒤·빈 줄·CRLF 보존");
        Check(Insert("#include \"Foo.h\" /* note\n */\nvoid Work() {}").Contains("*/\n#include"), "include 뒤 여러 줄 주석을 분리하지 않음");
        Check(Insert("#pragma once /* note\n */\nclass Foo {};").Contains("*/\n#include"), "pragma 뒤 여러 줄 주석을 분리하지 않음");
        var unreal = "#pragma once\n#include \"CoreMinimal.h\"\n#include \"Foo.generated.h\"\nclass Foo {};";
        Check(Insert(unreal).Contains("Widget.h\"\n#include \"Foo.generated.h\""), "generated 헤더 바로 위 삽입");
        Check(Insert("#pragma once\n#include \"CoreMinimal.h\"\n\n#include \"Foo.generated.h\"\n\nclass Foo {};").Contains("#include \"../Include/Widget.h\"\n#include \"Foo.generated.h\"\n\nclass"), "generated 우선 규칙과 본문 구분 빈 줄 보존");
        Check(Insert("// copyright\n#pragma once\nclass Foo {};").StartsWith("// copyright\n#pragma once\n#include"), "저작권 및 pragma once 유지");
        Check(Insert("/* license\n */class Foo {};").StartsWith("/* license\n */\n#include"), "여러 줄 주석 내부 삽입 금지");
        Check(Insert("// license").StartsWith("// license\n#include"), "줄바꿈 없는 주석 끝");
        Check(Insert("").StartsWith("#include"), "빈 파일");
        Check(Insert("#ifndef FOO_H\n#define FOO_H\nclass Foo {};\n#endif\n").Contains("#define FOO_H\n#include"), "헤더 가드 내부");
        Check(Insert("#if FEATURE\n#include \"Other.h\"\n#endif\nclass Foo {};").Contains("#endif\n#include"), "조건부 헤더 묶음 뒤·조건 밖에 삽입");
        Check(Insert("#include \"Foo.h\"\n\n// 함수 설명\nvoid Work() {}") == "#include \"Foo.h\"\n#include \"../Include/Widget.h\"\n\n// 함수 설명\nvoid Work() {}", "다음 함수 설명과 헤더 묶음 분리 유지");
        Check(Insert("#include \"pch.h\" // PCH\n#include <vector>\n\n#include \"Foo.h\"\n\nvoid Work() {}").Contains("Foo.h\"\n#include \"../Include/Widget.h\"\n\nvoid"), "PCH·시스템·로컬 순서 보존 및 마지막 클러스터 뒤 삽입");
        Check(Insert("#include \"Foo.h\" /* note\n */\n\n// 함수 설명\nvoid Work() {}").Contains("*/\n#include \"../Include/Widget.h\"\n\n// 함수 설명"), "연결된 여러 줄 주석만 보존");
        Check(Insert("#pragma once\n\n// 클래스 설명\nclass Foo {};").Contains("#pragma once\n#include \"../Include/Widget.h\"\n\n// 클래스 설명"), "include 없는 pragma 헤더의 설명 보존");
        Check(Insert("#ifndef FOO_H\n#define FOO_H\n#if A\n#if B\n#include \"Foo.h\"\n#endif\n#endif\n\nclass Foo {};\n#endif").Contains("#endif\n#endif\n#include \"../Include/Widget.h\"\n\nclass"), "헤더 가드 내부·중첩 조건부 클러스터 외부");
        Check(Insert("#include \"Foo.h\"").EndsWith("Foo.h\"\n#include \"../Include/Widget.h\"\n"), "줄바꿈 없는 include 끝");
        Check(Insert("#include \"Foo.h\"\nvoid Work() {\n#include \"Local.h\"\n}").StartsWith("#include \"Foo.h\"\n#include \"../Include/Widget.h\"\nvoid"), "본문 내부 include를 클러스터로 취급하지 않음");
        foreach (var text in new[] {
            "#include \"../Include/Widget.h\"\n", "#include <../Include/Widget.h>\n", "#if A\n#include \"../Include/Widget.h\"\n#endif\n",
            "#include \"Foo.generated.h\"\n#include \"Other.h\"\n", "#if X\n#include \"Foo.generated.h\"\n#endif\n",
            "#include \"Foo.generated.h\"\n#include \"Foo.generated.h\"\n", "#if X\n", "#endif\n", "export module M;\n", "/* unfinished",
            "#define MULTI \\\n anything\n", "#include HEADER\n", "#ifndef FOO\n#define FOO\n#else\n#endif\n" }) Reject(() => Insert(text), "안전하지 않은 include 거부: " + text);
        Reject(() => QuickInclude.IncludePath(sourcePath, sourcePath), "자기 include 제외");
        Reject(() => QuickInclude.IncludePath(sourcePath, @"C:\Work\Foo.generated.h"), "생성 헤더 제외");
        Reject(() => QuickInclude.IncludePath(sourcePath, @"D:\Library\Other.h"), "일반 교차 드라이브 절대 include 금지");
        var paths = new[] { @"D:\SDK\Widget.h", header, @"C:\Work\Source\Widget.h", @"C:\Work\Source\Widget.generated.h", @"C:\Work\Source\Widget.cpp", header };
        var ranked = QuickInclude.RankHeaders(sourcePath, @"C:\Work", paths);
        Check(ranked.SequenceEqual(new[] { paths[2], header, paths[0] }), "같은 폴더·프로젝트 우선 및 중복/구현/생성 헤더 제외");
        Check(QuickInclude.RankHeaders(sourcePath, @"C:\Work", paths.Reverse()).SequenceEqual(ranked), "입력 순서와 무관한 1순위");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { QuickInclude.Create("", sourcePath, header, cancel.Token); throw new Exception("취소 누락"); } catch (OperationCanceledException) { }

        var links = CommentFileReferences.Parse("// Foo.h Other.cpp:42 \"Some Dir/Thing.cs\":12 ../Include/Widget.h:3 한글.h:8");
        Check(links.Count == 5 && links[0].Line == 1 && links[1].Line == 42 && links[2].Path == "Some Dir/Thing.cs" && links[4].Path == "한글.h", "파일·라인·공백·상대 경로·한글 주석 링크");
        Check(CommentFileReferences.Parse("// Foo.h. Other.cpp:42.").Count == 2, "문장 끝 마침표와 링크 구분");
        foreach (var value in new[] { "http://site/Foo.h", "https://site/Foo.h:12", @"C:\Work\Foo.h", "Foo.exe", "Foo.h:0", "Foo.h:999999999999", "Foo.h:2:3", "Foo.h.invalid" })
            Check(CommentFileReferences.Parse(value).Count == 0, "URL·실행 파일·절대 경로·잘못된 라인 제외: " + value);
        var duplicates = new[] { @"C:\Work\Source\Foo.h", @"C:\Work\Other\Foo.h", @"C:\Work\Third\Foo.h" };
        Check(CommentFileReferences.Resolve(sourcePath, "Foo.h", duplicates).SequenceEqual(new[] { duplicates[0] }), "주석 링크 같은 폴더 우선");
        Check(CommentFileReferences.Resolve(sourcePath, "Other/Foo.h", duplicates).SequenceEqual(new[] { duplicates[1] }), "경로 접미사 일치");
        Check(CommentFileReferences.Resolve(sourcePath, "Foo.h", duplicates.Skip(1)).Count == 2, "동명 파일 선택 후보 유지");
        Check(CommentFileReferences.Resolve(sourcePath, "Absent.h", duplicates).Count == 0, "없는 파일");
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) CommentFileReferences.Parse("// file.cpp:42 Other.h");
        Check(watch.ElapsedMilliseconds < 1000, "보이는 줄 링크 파싱 성능");
        Console.WriteLine($"PASS: 빠른 include 경로·배치·중복·1순위·취소 및 주석 링크 파싱·해석 (1,000줄 {watch.Elapsed.TotalMilliseconds:F1}ms)");
    }
    private static void Reject(Action action, string message) { try { action(); } catch (GenerationNotSupportedException) { return; } throw new Exception(message); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
