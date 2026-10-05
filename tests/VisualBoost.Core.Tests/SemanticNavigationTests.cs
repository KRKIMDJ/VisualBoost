using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Core.SemanticNavigation;

namespace VisualBoost.Core.Tests;

internal static class SemanticNavigationTests
{
    public static void RunJson()
    {
        var parsed = JsonValue.Parse("{\"a\":[1,2.5,-3e2,true,false,null],\"s\":\"\\\"q\\\" \\\\ \\n \\u00e9 \\ud83d\\ude00 한글\",\"o\":{}}");
        Check(parsed["a"].Items.Count == 6 && parsed["a"].Items[0].AsInt32() == 1 && parsed["a"].Items[1].AsNumber() == 2.5 &&
              parsed["a"].Items[2].AsNumber() == -300 && parsed["a"].Items[3].AsBoolean() == true && parsed["a"].Items[5].IsNull, "배열 값");
        Check(parsed["s"].AsString() == "\"q\" \\ \n é 😀 한글", "이스케이프·대리 쌍·한글");
        Check(parsed["missing"].IsNull && parsed["a"]["x"].IsNull && parsed["o"].Properties.Count == 0, "없는 속성은 null");
        var roundTrip = JsonValue.Parse(parsed.ToJson());
        Check(roundTrip["s"].AsString() == parsed["s"].AsString() && roundTrip["a"].Items.Count == 6, "직렬화 왕복");
        Check(JsonValue.Object(("n", 3), ("d", 0.25), ("t", "\u0001")).ToJson() == "{\"n\":3,\"d\":0.25,\"t\":\"\\u0001\"}", "숫자·제어 문자 기록");
        foreach (var invalid in new[] { "", "{", "[1,]", "{\"a\" 1}", "tru", "\"abc", "1 2", "{\"a\":\"\u0001\"}", "-" })
        {
            Check(Throws<FormatException>(() => JsonValue.Parse(invalid)), "잘못된 JSON 거부: " + invalid);
        }

        Check(Throws<FormatException>(() => JsonValue.Parse(new string('[', 300) + new string(']', 300))), "깊은 중첩 거부");
    }

    public static void RunCommandLine()
    {
        Check(CommandLine.Split("/I \"C:/a b/c\" /DX=\"1\" /FI\"C:/x y/h.h\"").SequenceEqual(new[] { "/I", "C:/a b/c", "/DX=1", "/FIC:/x y/h.h" }), "따옴표 인자");
        Check(CommandLine.Split("a\\\\\"b c\" d\\\"e f\\g").SequenceEqual(new[] { "a\\b c", "d\"e", "f\\g" }), "역슬래시 규칙");
        Check(CommandLine.Split("  \r\n\t ").Count == 0 && CommandLine.Split("\"\"").SequenceEqual(new[] { "" }), "빈 인자");
    }

    public static void RunUri()
    {
        var path = @"C:\Work Space\한글 폴더\a#b.cpp";
        var uri = DocumentUri.FromPath(path);
        Check(uri.StartsWith("file:///C:/Work%20Space/", StringComparison.Ordinal) && !uri.Contains(' '), "URI 인코딩: " + uri);
        Check(DocumentUri.ToPath(uri) == path, "URI 왕복");
        Check(DocumentUri.ToPath("file:///c%3A/x/y.h") == @"C:\x\y.h" && DocumentUri.ToPath("file:///c:/x/y.h") == @"C:\x\y.h", "소문자 드라이브 보정");
        Check(DocumentUri.ToPath("untitled:Untitled-1") is null, "file이 아닌 URI 제외");
        var locations = NavigationLocation.FromLsp(JsonValue.Parse(
            "[{\"uri\":\"file:///c:/x/a.cpp\",\"range\":{\"start\":{\"line\":1,\"character\":2},\"end\":{\"line\":1,\"character\":5}}}," +
            "{\"targetUri\":\"file:///c:/x/b.h\",\"targetSelectionRange\":{\"start\":{\"line\":3,\"character\":4},\"end\":{\"line\":3,\"character\":7}}}]"));
        Check(locations.Count == 2 && locations[0].ToString() == @"C:\x\a.cpp:2:3" && locations[1].Line == 3, "Location·LocationLink 변환");
        Check(NavigationLocation.FromLsp(JsonValue.Parse("{\"uri\":\"file:///c:/x/a.cpp\",\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}}}")).Count == 1, "단일 Location");
        Check(NavigationLocation.Normalize(locations.Concat(locations)).Count == 2, "중복 위치 병합");
    }

    public static void RunConnection()
    {
        using var server = new FakeServer();
        var notifications = new List<string>();
        server.Client.Notification += (method, _) => { lock (notifications) notifications.Add(method); };
        server.Client.ServerRequestHandler = (method, _) => method == "window/workDoneProgress/create" ? JsonValue.Null : null;
        server.Client.Start();

        var request = server.Client.RequestAsync("textDocument/definition", JsonValue.Object(("x", 1)));
        var received = server.Read();
        Check(received["method"].AsString() == "textDocument/definition" && received["params"]["x"].AsInt32() == 1, "요청 전송");
        server.Write(JsonValue.Object(("jsonrpc", "2.0"), ("method", "$/progress"), ("params", JsonValue.Object(("token", "t")))));
        server.Write(JsonValue.Object(("jsonrpc", "2.0"), ("id", 99), ("method", "window/workDoneProgress/create"), ("params", JsonValue.Object())));
        server.Write(JsonValue.Object(("jsonrpc", "2.0"), ("id", 100), ("method", "unknown/request"), ("params", JsonValue.Object())));
        server.Write(JsonValue.Object(("jsonrpc", "2.0"), ("id", received["id"]), ("result", JsonValue.Array("한글"))));
        Check(request.Wait(5000) && request.Result.Items[0].AsString() == "한글", "응답 대응·UTF-8 본문");
        var created = server.Read();
        Check(created["id"].AsInt32() == 99 && created.Properties.ContainsKey("result") && created["result"].IsNull, "서버 요청에 null 결과 응답");
        var unknown = server.Read();
        Check(unknown["id"].AsInt32() == 100 && unknown["error"]["code"].AsInt32() == -32601, "모르는 서버 요청은 MethodNotFound");
        Check(SpinUntil(() => { lock (notifications) return notifications.Contains("$/progress"); }), "통지 전달");

        var error = server.Client.RequestAsync("bad", null);
        var badRequest = server.Read();
        server.Write(JsonValue.Object(("jsonrpc", "2.0"), ("id", badRequest["id"]), ("error", JsonValue.Object(("code", -32602), ("message", "nope")))));
        Check(WaitFault<LspRequestException>(error) is LspRequestException { Code: -32602 }, "오류 응답");

        using var cancellation = new CancellationTokenSource();
        var cancelled = server.Client.RequestAsync("slow", null, cancellation.Token);
        var slow = server.Read();
        cancellation.Cancel();
        Check(SpinUntil(() => cancelled.IsCanceled), "취소 즉시 완료");
        var cancelNotice = server.Read();
        Check(cancelNotice["method"].AsString() == "$/cancelRequest" && cancelNotice["params"]["id"].AsInt32() == slow["id"].AsInt32(), "$/cancelRequest 전송");
        server.Write(JsonValue.Object(("jsonrpc", "2.0"), ("id", slow["id"]), ("result", 1)));
        Check(server.Client.PendingCount == 0, "취소한 요청의 늦은 응답 무시");

        var pending = server.Client.RequestAsync("never", null);
        server.Read();
        Exception? closedWith = null;
        server.Client.Closed += e => closedWith = e;
        server.CloseServerOutput();
        Check(WaitFault<LspConnectionClosedException>(pending) is not null && SpinUntil(() => closedWith is not null), "EOF는 대기 요청을 실패로 끝냄");
        Check(Throws<LspConnectionClosedException>(() => server.Client.Notify("x", null)), "끊긴 연결에는 보내지 않음");

        using var malformed = new FakeServer();
        malformed.Client.Start();
        var waiting = malformed.Client.RequestAsync("x", null);
        malformed.Read();
        malformed.WriteRaw(Encoding.ASCII.GetBytes("Content-Length: 999999999999\r\n\r\n"));
        Check(WaitFault<LspConnectionClosedException>(waiting) is not null, "허용하지 않는 크기 거부");
    }

    public static void RunUnrealCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.UnrealCommands." + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = Path.Combine(root, "Engine Root");
            var project = Path.Combine(root, "Game Project");
            var source = Path.Combine(project, "Source", "Game");
            Write(Path.Combine(source, "A.cpp"), "int A;");
            Write(Path.Combine(source, "B.cpp"), "int B;");
            Write(Path.Combine(source, "C.cpp"), "int C;");
            var build = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "Development", "Game");
            var pch = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "GameEditor", "Development", "UnrealEd", "SharedPCH.UnrealEd.h").Replace('\\', '/');
            var definitions = Path.Combine(build, "Definitions.Game.h").Replace('\\', '/');
            Write(Path.Combine(build, "Game.Shared.rsp"), "/nologo\n/I \"Runtime/Core/Public\"\n/DWITH_EDITOR=1\n/errorReport:prompt\n/d2ExtendedWarningInfo\n/W4\n");
            string Rsp(string file) => $"\"{file.Replace('\\', '/')}\"\n@\"{Path.Combine(build, "Game.Shared.rsp").Replace('\\', '/')}\"\n/FI\"{pch}\"\n/FI\"{definitions}\"\n/Yu\"{pch}\"\n/Fp\"{pch}.pch\"\n/Fo\"x.obj\"\n/experimental:log \"x.sarif\"\n/sourceDependencies \"x.json\"\n/TP\n/std:c++20\n";
            Write(Path.Combine(build, "A.cpp.obj.rsp"), Rsp(Path.Combine(source, "A.cpp")));
            var unity = Path.Combine(build, "Module.Game.cpp");
            Write(unity, $"#include \"{Path.Combine(project, "Intermediate", "Build", "Win64", "UnrealEditor", "Inc", "Game", "UHT", "Game.gen.cpp").Replace('\\', '/')}\"\n" +
                         $"#include \"{Path.Combine(source, "A.cpp").Replace('\\', '/')}\"\n#include \"{Path.Combine(source, "B.cpp").Replace('\\', '/')}\"\n" +
                         $"#include \"{Path.Combine(source, "Missing.cpp").Replace('\\', '/')}\"\n");
            Write(unity + ".obj.rsp", Rsp(unity));
            var plugin = Path.Combine(project, "Plugins", "Group", "Feature");
            Write(Path.Combine(plugin, "Feature.uplugin"), "{}");
            Write(Path.Combine(plugin, "Source", "Feature", "Private", "F.cpp"), "int F;");
            var pluginBuild = Path.Combine(plugin, "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "Development", "Feature");
            Write(Path.Combine(pluginBuild, "F.cpp.obj.rsp"), $"\"{Path.Combine(plugin, "Source", "Feature", "Private", "F.cpp").Replace('\\', '/')}\"\n/I \"Runtime/Engine/Public\"\n/TP\n");
            var old = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "DebugGame", "Game", "C.cpp.obj.rsp");
            Write(old, Rsp(Path.Combine(source, "C.cpp")));
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));

            var variant = UnrealCompileCommands.DetectVariant(project);
            Check(variant is { Target: "UnrealEditor", Configuration: "Development" }, "가장 최근 구성 선택: " + variant);
            var result = UnrealCompileCommands.Build(project, engine, variant!, "cl.exe");
            var files = result.Commands.Select(c => Path.GetFileName(c.File)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Check(files.SequenceEqual(new[] { "A.cpp", "B.cpp", "F.cpp" }), "파일별·unity·플러그인 TU: " + string.Join(",", files));
            Check(result.SkippedGenerated == 1 && result.MissingSources == 1 && result.UnityMembers == 4, "생성·누락 파일 집계");
            var a = result.Commands.Single(c => c.File.EndsWith("/A.cpp", StringComparison.Ordinal));
            Check(a.Directory == Path.Combine(engine, "Engine", "Source").Replace('\\', '/'), "작업 경로는 Engine/Source");
            Check(a.Arguments[0] == "cl.exe" && a.Arguments[1] == "--driver-mode=cl" && a.Arguments.Last() == a.File, "컴파일러·source 위치");
            Check(!a.Arguments.Any(x => x.StartsWith("/Yu") || x.StartsWith("/Fp") || x.StartsWith("/Fo") || x.StartsWith("/d2") || x.StartsWith("/errorReport") ||
                                         x == "/experimental:log" || x == "x.sarif" || x == "/sourceDependencies"), "PCH·출력·로그 옵션 제거");
            Check(!a.Arguments.Contains(pch) && !a.Arguments.Any(x => x.StartsWith("/FI")), "공유 PCH 강제 include 제거");
            var include = Array.IndexOf(a.Arguments.ToArray(), definitions);
            Check(include >= 3 && a.Arguments[include - 3] == "-Xclang" && a.Arguments[include - 2] == "-include" && a.Arguments[include - 1] == "-Xclang", "정의 헤더는 -Xclang -include");
            Check(a.Arguments.Contains("Runtime/Core/Public") && a.Arguments.Contains("/DWITH_EDITOR=1") && a.Arguments.Contains("/std:c++20"), "공유 응답 파일 펼침");

            var output = Path.Combine(root, "db");
            CompileCommandDatabase.Write(output, result.Commands);
            var reread = CompileCommandDatabase.Read(Path.Combine(output, CompileCommandDatabase.FileName));
            Check(reread.Count == 3 && reread.Single(c => c.File == a.File).Arguments.SequenceEqual(a.Arguments), "database 기록·읽기 왕복");
            CompileCommandDatabase.Write(output, result.Commands.Take(1));
            Check(CompileCommandDatabase.Read(Path.Combine(output, CompileCommandDatabase.FileName)).Count == 1, "database 교체");

            var engineModule = Path.Combine(engine, "Engine", "Source", "Runtime", "Widgets");
            Write(Path.Combine(engineModule, "Widgets.Build.cs"), "");
            Write(Path.Combine(engineModule, "Private", "Widget.cpp"), "int W;");
            Directory.CreateDirectory(Path.Combine(engineModule, "Public"));
            Directory.CreateDirectory(Path.Combine(engine, "Engine", "Intermediate", "Build", "Win64", "UnrealEditor", "Inc", "Widgets", "UHT"));
            var synthesized = UnrealCompileCommands.Synthesize(Path.Combine(engineModule, "Private", "Widget.cpp"), result.Commands, Path.Combine(output, "modules"));
            Check(synthesized is not null && synthesized.Arguments.Last().EndsWith("Private/Widget.cpp") &&
                  synthesized.Arguments.Contains(Path.Combine(engineModule, "Public").Replace('\\', '/')) &&
                  synthesized.Arguments.Any(x => x.EndsWith("Inc/Widgets/UHT")), "엔진 모듈 근사 명령");
            var overrideHeader = Path.Combine(output, "modules", "Widgets.h");
            var overrideIndex = synthesized!.Arguments.ToList().IndexOf(overrideHeader.Replace('\\', '/'));
            Check(File.ReadAllText(overrideHeader).Contains("#define WIDGETS_API\n") && overrideIndex > 1 &&
                  synthesized.Arguments[overrideIndex - 2] == "-include" &&
                  synthesized.Arguments.Select((x, i) => (x, i)).Where(p => p.x == "-include").All(p => p.i <= overrideIndex - 2),
                  "모듈 API 매크로 재정의 헤더는 마지막 강제 include");
            Check(UnrealCompileCommands.Synthesize(Path.Combine(root, "loose.cpp"), result.Commands, output) is null, "모듈 밖 파일은 근사하지 않음");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>실제 clangd로 세션 수명·문서 동기화·정의·참조·background index 완료 신호를 확인합니다.</summary>
    public static void RunClangdIntegration()
    {
        var clangd = FindClangd();
        if (clangd is null)
        {
            Console.WriteLine("SKIP: clangd 통합 시험은 VISUALBOOST_TEST_CLANGD 또는 VS의 C++ Clang 도구가 필요합니다.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.Clangd 한글." + Guid.NewGuid().ToString("N"));
        try
        {
            var header = Path.Combine(root, "fixture.h");
            var main = Path.Combine(root, "main.cpp");
            var other = Path.Combine(root, "other.cpp");
            Write(header, "#pragma once\nstruct A {\n    void Run();\n    void Run() const;\n};\nstruct B {\n    void Run();\n};\n");
            var mainText = "#include \"fixture.h\"\nvoid A::Run() {}\nvoid A::Run() const {}\nvoid B::Run() {}\nvoid First(A& a, const A& ca, B& b) {\n    a.Run();\n    ca.Run();\n    b.Run();\n}\n";
            Write(main, mainText);
            Write(other, "#include \"fixture.h\"\nvoid Second(A& a) {\n    a.Run();\n}\n");
            var database = Path.Combine(root, "db");
            CompileCommandDatabase.Write(database, new[] { main, other }.Select(f =>
                new CompileCommand(root, f, new[] { "clang++", "-std=c++17", "-fsyntax-only", f })));

            using var session = ClangdSession.Start(new ClangdLaunchOptions
            {
                ExecutablePath = clangd, CompileCommandsDirectory = database, WorkerCount = 1, LogFilePath = Path.Combine(root, "clangd.log")
            });
            var progressEvents = 0;
            session.ProgressChanged += () => Interlocked.Increment(ref progressEvents);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            session.InitializeAsync(timeout.Token).Wait();
            session.OpenDocument(main, mainText, 1);
            session.WaitForDiagnosticsAsync(main, 1, timeout.Token).Wait();
            Check(SpinUntil(() => session.Progress.Completed, 30000), "background index 완료 신호(진행 알림 " + progressEvents + "회)");

            // 사람이 지정한 정답: a.Run()은 A::Run()(비 const) 정의 2행, ca.Run()은 const 정의 3행.
            var definition = session.DefinitionAsync(main, 5, 6, timeout.Token).Result;
            Check(definition.Count == 1 && definition[0].Line == 1 && definition[0].Path == main, "비 const 멤버 정의");
            var constDefinition = session.DefinitionAsync(main, 6, 7, timeout.Token).Result;
            Check(constDefinition.Count == 1 && constDefinition[0].Line == 2, "const 오버로드 정의");
            var declaration = session.DeclarationAsync(main, 5, 6, timeout.Token).Result;
            Check(declaration.Count == 1 && declaration[0].Path == header && declaration[0].Line == 2, "선언은 헤더");
            var references = NavigationLocation.Normalize(session.ReferencesAsync(main, 5, 6, false, timeout.Token).Result);
            Check(references.Select(r => (Path.GetFileName(r.Path), r.Line)).SequenceEqual(new[] { ("main.cpp", 5), ("other.cpp", 2) }), "열지 않은 cpp 참조 포함·다른 심볼 제외");
            var symbol = session.SymbolInfoAsync(main, 5, 6, timeout.Token).Result;
            Check(symbol is { Name: "Run", ContainerName: "A" } && symbol.Usr.Length > 0 && symbol.QualifiedName == "A::Run", "symbolInfo 소속 이름");

            // 미저장 편집: 수신 객체를 b로 바꾸면 정의가 B::Run으로 바뀌어야 합니다.
            var edited = mainText.Replace("    a.Run();", "    b.Run();");
            session.ChangeDocument(main, edited, 2);
            session.WaitForDiagnosticsAsync(main, 2, timeout.Token).Wait();
            Check(session.DefinitionAsync(main, 5, 6, timeout.Token).Result.Single().Line == 3, "미저장 편집 반영");
            Check(File.ReadAllText(main) == mainText, "디스크 원본 보존");
            session.CloseDocument(main);

            var exited = new ManualResetEventSlim();
            session.Exited += _ => exited.Set();
            session.ShutdownAsync(TimeSpan.FromSeconds(10)).Wait();
            Check(session.HasExited && exited.Wait(5000), "정상 종료");
            Check(Directory.Exists(Path.Combine(database, ".cache", "clangd", "index")), "색인은 database 폴더 아래에만 저장");
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static void RunDocumentSet()
    {
        var events = new List<string>();
        var set = new ClangdDocumentSet(2, (p, t, v) => events.Add($"open {p} {t} {v}"), (p, t, v) => events.Add($"change {p} {t} {v}"),
            p => events.Add("close " + p));
        Check(set.Acquire(new DocumentText("a", "x", 1)) == 1 && set.Acquire(new DocumentText("a", "x", 1)) == 1, "같은 내용 재임대는 버전 유지");
        set.Release("a");
        set.Release("a");
        Check(set.Update(new DocumentText("b", () => throw new InvalidOperationException("열리지 않은 문서의 내용을 만들면 안 됩니다."), 9)) is null,
            "열리지 않은 문서는 갱신·내용 계산 안 함");
        set.Acquire(new DocumentText("b", "y", 5));
        Check(set.Update(new DocumentText("b", "old", 3)) == 1 && set.Update(new DocumentText("b", "disk", 0)) == 1, "오래된 리비전은 버림");
        Check(set.Update(new DocumentText("b", "y2", 5)) == 2, "같거나 새 리비전의 다른 내용은 전송");
        set.Acquire(new DocumentText("c", "z"));
        set.Release("c");
        Check(!set.Contains("a") && set.Contains("b") && set.Contains("c") && events.Contains("close a"), "용량 초과 시 임대 없는 오래된 문서부터 닫음");
        Check(!set.TryClose("b"), "임대 중인 문서는 닫지 않음");
        set.Release("b");
        Check(set.TryClose("b") && events.Last() == "close b", "임대가 끝나면 닫음");
        Check(events.SequenceEqual(new[] { "open a x 1", "open b y 1", "change b y2 2", "open c z 1", "close a", "close b" }), "알림 순서: " + string.Join(" | ", events));
        set.Reset();
        Check(set.OpenPaths.Count == 0 && events.Count == 6, "재시작 초기화는 알림 없음");
    }

    public static void RunCompileContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.CompileContext." + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = Path.Combine(root, "cache");
            var first = CompileContextBuilder.CacheDirectory(cache, Path.Combine(root, "A b", "My Game!.sln"));
            Check(first == CompileContextBuilder.CacheDirectory(cache, Path.Combine(root, "A b", "My Game!.sln")) &&
                  Path.GetFileName(first).StartsWith("My_Game_-", StringComparison.Ordinal) &&
                  first != CompileContextBuilder.CacheDirectory(cache, Path.Combine(root, "Other", "My Game!.sln")), "Solution별 고정 캐시 폴더");

            var cmake = Path.Combine(root, "cmake");
            var source = Path.Combine(cmake, "main.cpp");
            Write(source, "int main() {}");
            Write(Path.Combine(cmake, "out", "build", "x64-Debug", CompileCommandDatabase.FileName),
                "[{\"directory\":\"" + cmake.Replace("\\", "\\\\") + "\",\"file\":\"main.cpp\",\"command\":\"clang++ -std=c++20 \\\"-DNAME=a b\\\" main.cpp\"}]");
            var context = CompileContextBuilder.Prepare(Path.Combine(cmake, "App.sln"), cache, null, "clang-cl.exe");
            Check(context.Kind == CompileContextKind.Database && context.Changed && context.Commands.Single().File == source &&
                  context.Commands.Single().Arguments.Contains("-DNAME=a b"), "기존 database 가져오기: " + context.Reason);
            Check(File.Exists(Path.Combine(context.Directory, CompileCommandDatabase.FileName)) && context.Directory.StartsWith(cache, StringComparison.Ordinal),
                "database는 캐시 폴더에만 기록");
            Check(!CompileContextBuilder.Prepare(Path.Combine(cmake, "App.sln"), cache, null, "clang-cl.exe").Changed, "같은 내용이면 다시 쓰지 않음");

            var game = Path.Combine(root, "game");
            Write(Path.Combine(game, "Game.uproject"), "{}");
            var noEngine = CompileContextBuilder.Prepare(Path.Combine(game, "Game.sln"), cache, null, "clang-cl.exe");
            Check(noEngine.Kind == CompileContextKind.None && noEngine.Reason!.Contains("엔진"), "엔진 경로 없음 안내");
            var notBuilt = CompileContextBuilder.Prepare(Path.Combine(game, "Game.sln"), cache, Path.Combine(root, "engine"), "clang-cl.exe");
            Check(notBuilt.Kind == CompileContextKind.None && notBuilt.Reason!.Contains("빌드"), "빌드 전 안내");
            Check(CompileContextBuilder.Prepare(Path.Combine(root, "empty", "E.sln"), cache, null, "clang-cl.exe").Reason!.Contains("compile_commands.json"),
                "명령 없음 안내");
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static void RunPreviewAndCandidates()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.Preview." + Guid.NewGuid().ToString("N"));
        try
        {
            var disk = Path.Combine(root, "disk.cpp");
            Directory.CreateDirectory(root);
            File.WriteAllText(disk, "first\r\n\tsecond 한글\r\nthird", new UTF8Encoding(true));
            var open = Path.Combine(root, "open.cpp");
            var lines = SourceLinePreview.Load(new[]
            {
                new NavigationLocation(disk, 1, 0, 1, 1), new NavigationLocation(open, 0, 0, 0, 1), new NavigationLocation(disk, 9, 0, 9, 1),
                new NavigationLocation(Path.Combine(root, "missing.cpp"), 0, 0, 0, 1)
            }, path => path == open ? "  edited  \nx" : null, maxLength: 8);
            Check(lines.SequenceEqual(new[] { "second 한…", "edited", "", "" }), "디스크·편집기 내용, BOM, 범위 밖: " + string.Join("|", lines));

            Check(DefinitionCandidates.LooksLikeTypeOrMacro("class COREUOBJECT_API UPackage : public UObject") &&
                  DefinitionCandidates.LooksLikeTypeOrMacro("template <typename T> struct TArray") &&
                  DefinitionCandidates.LooksLikeTypeOrMacro("  #define CHECK(x)") && DefinitionCandidates.LooksLikeTypeOrMacro("using FType = int;") &&
                  !DefinitionCandidates.LooksLikeTypeOrMacro("\tstatic bool SavePackage(UPackage* InOuter);") &&
                  !DefinitionCandidates.LooksLikeTypeOrMacro("UPackage* CreatePackage(const TCHAR* PackageName);"), "타입·매크로 줄 판별");

            var engine = Path.Combine(root, "Engine", "Source", "Runtime", "CoreUObject");
            var header = Path.Combine(engine, "Public", "Package.h");
            var symbols = new[]
            {
                new Analysis.SourceSymbolLocation("SavePackage", Path.Combine(root, "Elsewhere", "Other.cpp"), 1, 1, Analysis.SourceSymbolKind.Function, "UOther"),
                new Analysis.SourceSymbolLocation("SavePackage", Path.Combine(root, "Elsewhere", "Free.cpp"), 1, 1, Analysis.SourceSymbolKind.Function),
                new Analysis.SourceSymbolLocation("SavePackage", Path.Combine(engine, "Private", "SavePackage2.cpp"), 1, 1, Analysis.SourceSymbolKind.Function, "UPackage"),
                new Analysis.SourceSymbolLocation("SavePackage", header, 1, 1, Analysis.SourceSymbolKind.Function, "UPackage"),
                new Analysis.SourceSymbolLocation("SavePackage", Path.Combine(engine, "Private", "Var.cpp"), 1, 1, Analysis.SourceSymbolKind.Variable, "UPackage")
            };
            var stems = new[] { Path.Combine(engine, "Private", "Package.cpp"), Path.Combine(engine, "Public", "Package.h") };
            var selected = DefinitionCandidates.Select("SavePackage", "UPackage", header, symbols, stems, 5).Select(Path.GetFileName).ToArray();
            Check(selected.SequenceEqual(new[] { "SavePackage2.cpp", "Free.cpp", "Package.cpp" }), "소속 일치·소속 없음·같은 이름 순, 다른 소속 제외: " + string.Join(",", selected));
            Check(DefinitionCandidates.Select("SavePackage", "UE::Core::UPackage::", header, symbols, stems, 1).Select(Path.GetFileName).Single() == "SavePackage2.cpp",
                "한정 소속의 마지막 이름 비교와 개수 제한");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// 가짜 Unreal 프로젝트·엔진 배치에서 응답 파일 변환 → clangd 색인 → 엔진 정의 요청 시점 확정 → 저장 반영까지 확인합니다.
    /// </summary>
    public static void RunNavigatorIntegration()
    {
        var clangd = FindClangd();
        if (clangd is null)
        {
            Console.WriteLine("SKIP: clangd 탐색 통합 시험은 VISUALBOOST_TEST_CLANGD 또는 VS의 C++ Clang 도구가 필요합니다.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.Navigator 한글." + Guid.NewGuid().ToString("N"));
        try
        {
            var engineRoot = Path.Combine(root, "Engine Root");
            var module = Path.Combine(engineRoot, "Engine", "Source", "Runtime", "Mod");
            Write(Path.Combine(module, "Mod.Build.cs"), "");
            var header = Path.Combine(module, "Public", "Mod.h");
            Write(header, "#pragma once\nstruct MOD_API FMod\n{\n    static int Compute(int Value);\n};\n");
            var engineSource = Path.Combine(module, "Private", "Mod.cpp");
            Write(engineSource, "#include \"Mod.h\"\nint FMod::Compute(int Value) { return Value * 2; }\n");

            var project = Path.Combine(root, "Game");
            Write(Path.Combine(project, "Game.uproject"), "{}");
            var gameSource = Path.Combine(project, "Source", "Game");
            Write(Path.Combine(gameSource, "Game.Build.cs"), "");
            var use = Path.Combine(gameSource, "Use.cpp");
            var useText = "#include \"Mod.h\"\nint Use() { return FMod::Compute(3); }\n";
            Write(use, useText);
            var other = Path.Combine(gameSource, "Other.cpp");
            Write(other, "#include \"Mod.h\"\nint Other() { return FMod::Compute(4); }\n");
            var build = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "Development", "Game");
            foreach (var file in new[] { use, other })
            {
                // 프로젝트 쪽 정의는 의존 모듈 API를 dllimport로 둡니다. 엔진 cpp 근사 명령은 이를 다시 비워야 합니다.
                Write(Path.Combine(build, Path.GetFileName(file) + ".obj.rsp"),
                    $"\"{file.Replace('\\', '/')}\"\n/I \"{Path.Combine(module, "Public").Replace('\\', '/')}\"\n/DMOD_API=__declspec(dllimport)\n/TP\n/std:c++17\n/c\n");
            }

            var cacheRoot = Path.Combine(root, "cache");
            using var navigator = ClangdNavigator.StartAsync(new ClangdNavigatorOptions
            {
                ClangdPath = clangd, CacheRoot = cacheRoot, SolutionPath = Path.Combine(project, "Game.sln"), EngineRoot = engineRoot, WorkerCount = 1,
                FindSymbols = name => name == "Compute"
                    ? new[] { new Analysis.SourceSymbolLocation("Compute", engineSource, 2, 11, Analysis.SourceSymbolKind.Function, "FMod") }
                    : Array.Empty<Analysis.SourceSymbolLocation>()
            }, CancellationToken.None).Result;
            Check(navigator.Context.Kind == CompileContextKind.Unreal && navigator.Context.Commands.Count == 2, "응답 파일에서 프로젝트 명령 준비");
            Check(SpinUntil(() => navigator.Progress.Completed, 60000), "프로젝트 색인 완료");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var position = useText.Split('\n')[1].IndexOf("Compute", StringComparison.Ordinal);
            var query = new NavigationQuery(new DocumentText(use, useText, 1), 1, position);
            var reports = new List<string>();
            var resolved = navigator.DefinitionAsync(query, new SyncProgress(reports.Add), timeout.Token).Result;
            Check(resolved.ResolvedOnDemand && resolved.Locations.Single().Path == engineSource && resolved.Locations[0].Line == 1 &&
                  reports.Any(r => r.Contains("Mod.cpp")), "엔진 cpp를 요청 시점에 열어 정의 확정: " + string.Join(",", resolved.Locations));
            Check(!navigator.IsOpen(engineSource) && navigator.IsOpen(use), "후보는 닫고 요청 문서는 유지");
            var cached = default(NavigationResult);
            Check(SpinUntil(() =>
            {
                cached = navigator.DefinitionAsync(query, null, timeout.Token).Result;
                return cached.Locations.Any(l => l.Path == engineSource);
            }, 30000) && !cached!.ResolvedOnDemand, "확정한 엔진 정의는 색인에 남아 다시 열지 않음");

            var references = navigator.ReferencesAsync(query, timeout.Token).Result;
            Check(references.Symbol is { Name: "Compute", ContainerName: "FMod" } &&
                  references.Locations.Any(l => l.Path == other) && references.Locations.Any(l => l.Path == header), "참조·선언과 심볼 정보");

            // 편집기에서 열지 않은 파일을 저장한 경우: 저장 내용으로 잠시 열어 색인에 반영합니다.
            var otherText = "#include \"Mod.h\"\nint Other() { return FMod::Compute(4); }\nint Again() { return FMod::Compute(5); }\n";
            File.WriteAllText(other, otherText, new UTF8Encoding(false));
            navigator.Saved(new DocumentText(other, otherText, 1));
            Check(SpinUntil(() => navigator.ReferencesAsync(query, timeout.Token).Result.Locations.Count(l => l.Path == other) == 2, 30000),
                "저장한 파일의 새 참조 반영");
            Check(SpinUntil(() => !navigator.IsOpen(other), 10000), "저장 반영 뒤 문서 닫기");

            // 미저장 편집: 맨 위에 빈 줄을 넣으면 같은 호출이 한 줄 아래에서 찾아져야 합니다.
            var edited = "\n" + useText;
            var moved = navigator.DefinitionAsync(new NavigationQuery(new DocumentText(use, edited, 2), 2, position), null, timeout.Token).Result;
            Check(moved.Locations.Any(l => l.Path == engineSource), "미저장 편집 위치로 정의 요청");
            Check(File.ReadAllText(use) == useText && File.ReadAllText(engineSource).Contains("Value * 2"), "원본 파일 보존");
            Check(!Directory.EnumerateFiles(project, "compile_commands.json", SearchOption.AllDirectories).Any(), "프로젝트 폴더에 database를 쓰지 않음");
            navigator.ShutdownAsync(TimeSpan.FromSeconds(10)).Wait();
            Check(navigator.HasExited, "정상 종료");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>보고를 즉시 같은 스레드에서 기록합니다(<see cref="Progress{T}"/>는 비동기로 전달됩니다).</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        private readonly Action<string> report;

        public SyncProgress(Action<string> report) => this.report = report;

        public void Report(string value)
        {
            lock (this) report(value);
        }
    }

    private static string? FindClangd()
    {
        var configured = Environment.GetEnvironmentVariable("VISUALBOOST_TEST_CLANGD");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var vs = Path.Combine(programFiles, "Microsoft Visual Studio");
            if (!Directory.Exists(vs)) continue;
            var found = Directory.GetDirectories(vs).SelectMany(Directory.GetDirectories)
                .Select(edition => Path.Combine(edition, "VC", "Tools", "Llvm", "x64", "bin", "clangd.exe"))
                .Where(File.Exists).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (found is not null) return found;
        }

        return null;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static void TryDelete(string root)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // 종료 직후 clangd가 파일을 놓기까지 잠시 기다립니다.
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static bool SpinUntil(Func<bool> condition, int milliseconds = 5000) => SpinWait.SpinUntil(condition, milliseconds);

    private static TException? WaitFault<TException>(Task task) where TException : Exception
    {
        try
        {
            task.Wait(5000);
        }
        catch (AggregateException exception)
        {
            return exception.InnerException as TException;
        }

        return null;
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>익명 파이프 두 개로 만든 시험용 LSP 상대편입니다.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly AnonymousPipeServerStream toClient = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream clientInput;
        private readonly AnonymousPipeServerStream fromClient = new(PipeDirection.In);
        private readonly AnonymousPipeClientStream clientOutput;

        public FakeServer()
        {
            clientInput = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
            clientOutput = new AnonymousPipeClientStream(PipeDirection.Out, fromClient.ClientSafePipeHandle);
            Client = new LspConnection(clientInput, clientOutput, 1024 * 1024);
        }

        public LspConnection Client { get; }

        public JsonValue Read()
        {
            var length = 0;
            var line = new StringBuilder();
            while (true)
            {
                var b = fromClient.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                if (b != '\n') { line.Append((char)b); continue; }
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0) break;
                length = int.Parse(text.Split(':')[1].Trim());
            }

            var body = new byte[length];
            var read = 0;
            while (read < length) read += fromClient.Read(body, read, length - read);
            return JsonValue.Parse(Encoding.UTF8.GetString(body));
        }

        public void Write(JsonValue message)
        {
            var body = Encoding.UTF8.GetBytes(message.ToJson());
            WriteRaw(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n").Concat(body).ToArray());
        }

        public void WriteRaw(byte[] bytes)
        {
            toClient.Write(bytes, 0, bytes.Length);
            toClient.Flush();
        }

        public void CloseServerOutput() => toClient.Dispose();

        public void Dispose()
        {
            Client.Dispose();
            toClient.Dispose();
            fromClient.Dispose();
            clientInput.Dispose();
            clientOutput.Dispose();
        }
    }
}
