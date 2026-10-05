using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VisualBoost.CommentLinks;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.DocumentNavigation;

internal static class CommentSymbolProjectTests
{
    // 실제 프로젝트 확인은 저장소 밖의 사례 파일로만 지정합니다. 프로젝트별 경로·이름을 테스트 코드에 두지 않기 위해서입니다.
    // 사례 파일은 UTF-8 탭 구분 형식이며 `#` 줄과 빈 줄은 무시합니다.
    //   file<TAB>소스 경로(절대 경로 또는 사례 파일 기준 상대 경로)
    //   link<TAB>주석에 쓸 이름<TAB>기대 대상 파일 이름<TAB>SourceSymbolKind
    // 명시적으로 요청한 실행에서만 지정 파일을 읽습니다. 쓰기·빌드·캐시 갱신은 하지 않습니다.
    internal static void Run(string caseFile)
    {
        var (files, cases) = ReadCases(Path.GetFullPath(caseFile));
        var hashes = files.ToDictionary(f => f, Hash);
        using var index = new SourceSymbolIndex();
        index.ReplaceAll(files.SelectMany(f => CppSourceAnalyzer.Analyze(f, File.ReadAllText(f)).Symbols));
        foreach (var (name, expectedFile, kind) in cases)
        {
            var parsed = CommentSymbolReferences.Parse("// " + name, n => index.Find(n).Any(s => CommentSymbolReferences.IsTypeOrNamespace(s.Kind)));
            Check(parsed.Count == 1, name + " 주석 인식");
            var clock = Stopwatch.StartNew();
            var result = CommentSymbolLinkResolver.Resolve(parsed[0], index.Find, default);
            clock.Stop();
            Check(!result.Limited && result.Targets.Any(t => string.Equals(Path.GetFileName(t.Path), expectedFile, StringComparison.OrdinalIgnoreCase) && t.Kind == kind),
                $"{name} 대상 확인(찾은 대상: {string.Join(", ", result.Targets.Select(t => $"{Path.GetFileName(t.Path)}:{t.Line} {t.Kind}"))}, 색인 후보: {string.Join(", ", index.Find(parsed[0].ShortName).Select(s => $"{s.Scope}::{s.Name} {Path.GetFileName(s.Path)}:{s.Line} {s.Kind}"))})");
            if (kind == SourceSymbolKind.Class) Check(result.Targets.Count == 1, "클래스 사용부·전방 선언 제외");
            foreach (var target in result.Targets)
            {
                var lines = File.ReadAllLines(target.Path);
                Check(target.Line >= 1 && target.Line <= lines.Length, "라인 범위");
                Console.WriteLine($"LINK: {name} -> {Path.GetFileName(target.Path)}:{target.Line} / {clock.Elapsed.TotalMilliseconds:F1} ms");
            }
        }
        foreach (var file in files) Check(hashes[file] == Hash(file), "읽기 전후 원본 SHA-256 동일");
        Console.WriteLine($"PASS: 사례 파일의 {files.Count}개 파일·{cases.Count}개 링크 읽기 전용 주석 링크 검증");
    }

    // 일반 회귀에서 사례 파일 해석과 실행 경로를 임시 샘플로 확인합니다. 외부 프로젝트는 읽지 않습니다.
    internal static void RunSample()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.CommentLinkCases." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Source", "Public"));
            Directory.CreateDirectory(Path.Combine(root, "Source", "Private"));
            File.WriteAllText(Path.Combine(root, "Source", "Public", "SampleLibrary.h"), string.Join("\n",
                "#pragma once",
                "namespace Sample",
                "{",
                "namespace Detail",
                "{",
                "int ReadFlag();",
                "}",
                "}",
                "class USampleLibrary",
                "{",
                "public:",
                "    static int GetValue();",
                "};",
                ""));
            File.WriteAllText(Path.Combine(root, "Source", "Private", "SampleLibrary.cpp"), string.Join("\n",
                "#include \"SampleLibrary.h\"",
                "namespace Sample",
                "{",
                "namespace Detail",
                "{",
                "int ReadFlag()",
                "{",
                "    return 1;",
                "}",
                "}",
                "}",
                "int USampleLibrary::GetValue()",
                "{",
                "    return Sample::Detail::ReadFlag();",
                "}",
                ""));
            var caseFile = Path.Combine(root, "cases.tsv");
            File.WriteAllText(caseFile, string.Join("\n",
                "# 임시 샘플",
                "file\tSource/Public/SampleLibrary.h",
                "file\tSource/Private/SampleLibrary.cpp",
                "",
                "link\tUSampleLibrary::GetValue\tSampleLibrary.cpp\tFunction",
                "link\tUSampleLibrary\tSampleLibrary.h\tClass",
                "link\tSample::Detail\tSampleLibrary.h\tNamespace",
                "link\tSample::Detail::ReadFlag\tSampleLibrary.cpp\tFunction"), new UTF8Encoding(false));
            Run(caseFile);

            File.WriteAllText(caseFile, "file\tSource/Public/SampleLibrary.h\nlink\tUSampleLibrary\tSampleLibrary.h\tNotAKind\n");
            CheckRejected(caseFile, "알 수 없는 심볼 종류 거부");
            File.WriteAllText(caseFile, "file\tSource/Public/Missing.h\nlink\tUSampleLibrary\tSampleLibrary.h\tClass\n");
            CheckRejected(caseFile, "없는 소스 파일 거부");
            File.WriteAllText(caseFile, "# 사례 없음\n");
            CheckRejected(caseFile, "빈 사례 파일 거부");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static (IReadOnlyList<string> Files, IReadOnlyList<(string Name, string ExpectedFile, SourceSymbolKind Kind)> Cases) ReadCases(string caseFile)
    {
        var baseDirectory = Path.GetDirectoryName(caseFile)!;
        var files = new List<string>();
        var cases = new List<(string, string, SourceSymbolKind)>();
        var lines = File.ReadAllLines(caseFile, Encoding.UTF8);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var fields = line.Split('\t').Select(f => f.Trim()).ToArray();
            var at = $"{Path.GetFileName(caseFile)}:{i + 1}";
            switch (fields[0])
            {
                case "file" when fields.Length == 2:
                    var path = Path.GetFullPath(Path.IsPathRooted(fields[1]) ? fields[1] : Path.Combine(baseDirectory, fields[1]));
                    if (!File.Exists(path)) throw new FileNotFoundException("사례 파일이 지정한 소스가 없습니다: " + at, path);
                    files.Add(path);
                    break;
                case "link" when fields.Length == 4:
                    if (!Enum.TryParse<SourceSymbolKind>(fields[3], true, out var kind) || !Enum.IsDefined(typeof(SourceSymbolKind), kind))
                        throw new FormatException("알 수 없는 심볼 종류입니다: " + at);
                    cases.Add((fields[1], fields[2], kind));
                    break;
                default:
                    throw new FormatException("해석할 수 없는 사례 줄입니다: " + at);
            }
        }
        if (files.Count == 0 || cases.Count == 0) throw new FormatException("사례 파일에 file과 link 줄이 하나 이상 필요합니다: " + caseFile);
        return (files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), cases);
    }

    private static void CheckRejected(string caseFile, string message)
    {
        try { ReadCases(caseFile); }
        catch (Exception exception) when (exception is FormatException or FileNotFoundException) { Console.WriteLine("PASS: " + message); return; }
        throw new InvalidOperationException(message);
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(stream)); }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
