using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Core.Analysis;
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
        var contained = NavigationLocation.FromLsp(JsonValue.Parse(
            "[{\"uri\":\"file:///c:/x/a.cpp\",\"range\":{\"start\":{\"line\":1,\"character\":2},\"end\":{\"line\":1,\"character\":5}},\"containerName\":\"Game::Tick\"}]"));
        Check(contained[0].Container == "Game::Tick" && locations[0].Container is null && contained[0].Equals(locations[0]), "참조 포함 함수 이름(위치 동일성과 무관)");
        Check(NavigationLocation.FromLsp(JsonValue.Parse("{\"uri\":\"file:///c:/x/a.cpp\",\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}}}")).Count == 1, "단일 Location");
        Check(NavigationLocation.Normalize(locations.Concat(locations)).Count == 2, "중복 위치 병합");
    }

    public static void RunReferenceRoles()
    {
        // clangd 22로 확인한 응답 모양을 그대로 씁니다. a.h: 1행 int Foo(); 2행 inline int Bar() {...} 3행 struct S; 4행 struct S {...}; 5행 int Foo();
        // b.cpp: 1행 Foo 정의, 2행 Foo·Bar·S 사용. c.cpp: 1행 Foo 사용.
        static NavigationLocation At(string file, int line, int character) => new(@"C:\p\" + file, line, character, line, character + 3);
        var fooDecl = At("a.h", 1, 4);
        var fooRedecl = At("a.h", 5, 4);
        var fooDef = At("b.cpp", 1, 4);
        var useInB = At("b.cpp", 2, 26);
        var useInC = At("c.cpp", 1, 17);
        var all = new[] { fooDef, useInB, fooDecl, fooRedecl, useInC };
        var uses = new[] { useInB, useInC };
        string Roles(IReadOnlyDictionary<NavigationLocation, NavigationRole> roles) =>
            string.Join(" ", all.Select(location => !roles.TryGetValue(location, out var role) ? "-" : role == NavigationRole.Definition ? "def" : "decl"));

        // 사용 위치에서: 정의(AST)·대표 선언(AST)과 선언 묶음의 나머지(재선언)를 표시합니다. 일반 사용에는 붙이지 않습니다.
        var fromUse = ReferenceRoles.Classify(all, uses, fooDef, fooDecl, new[] { fooDef }, new[] { fooDecl });
        Check(Roles(fromUse) == "def - decl decl -", "사용 위치에서 정의·선언: " + Roles(fromUse));
        Check(ReferenceRoles.Text(NavigationRole.Definition) == "정의" && ReferenceRoles.Text(NavigationRole.Declaration) == "선언" &&
              ReferenceRoles.Text(NavigationRole.None).Length == 0, "역할 표식 글자");

        // 정의가 다른 번역 단위에만 있으면 AST 정의가 없어도 정의·선언 이동 응답이 서로 다르므로 색인의 정의를 씁니다.
        Check(Roles(ReferenceRoles.Classify(all, uses, null, fooDecl, new[] { fooDef }, new[] { fooDecl })) == "def - decl decl -", "다른 번역 단위의 정의");

        // 선언 제외 참조를 받지 못하면(시간 상한·결과 수 제한) 확실한 정의·대표 선언만 표시합니다.
        Check(Roles(ReferenceRoles.Classify(all, null, fooDef, fooDecl, new[] { fooDef }, new[] { fooDecl })) == "def - decl - -", "선언 묶음 없이");

        // 대표 선언 위에서 찾으면 정의·선언 이동이 같은 곳(정의)으로 토글되어 근거가 되지 못합니다. 정의를 모르면 선언 묶음의 나머지도 표시하지 않습니다.
        Check(Roles(ReferenceRoles.Classify(all, uses, null, fooDecl, new[] { fooDef }, new[] { fooDef })) == "- - decl - -", "토글 응답은 쓰지 않음");

        // 근거가 없으면 비어 있고, 여러 심볼 응답은 쓰지 않으며, clangd가 일반 사용으로 돌려준 위치에는 다른 근거가 있어도 붙이지 않습니다.
        Check(ReferenceRoles.Classify(all, uses, null, null, null, null).Count == 0, "근거 없음");
        Check(Roles(ReferenceRoles.Classify(all, uses, null, null, new[] { fooDef, fooRedecl }, new[] { fooDecl })) == "- - - - -", "여러 위치 응답 무시");
        Check(Roles(ReferenceRoles.Classify(all, uses, useInB, null, null, null)) == "- - - - -", "일반 사용과 어긋나는 근거 무시");

        // 정의와 대표 선언이 같은 위치(헤더의 inline 함수)이면 정의만 표시합니다.
        var bar = At("a.h", 2, 11);
        var barAll = new[] { At("b.cpp", 1, 19), bar };
        var barRoles = ReferenceRoles.Classify(barAll, new[] { barAll[0] }, bar, bar, new[] { bar }, new[] { bar });
        Check(barRoles.Count == 1 && barRoles[bar] == NavigationRole.Definition, "정의이자 선언은 정의만");

        // 앞선 전방 선언이 있는 타입: 정의·선언 이동이 모두 정의를 돌려줘도 AST 범위로 둘을 가립니다.
        var forward = At("a.h", 3, 7);
        var type = At("a.h", 4, 7);
        var typeRoles = ReferenceRoles.Classify(new[] { At("b.cpp", 2, 12), forward, type }, new[] { At("b.cpp", 2, 12) }, type, forward, new[] { type }, new[] { type });
        Check(typeRoles.Count == 2 && typeRoles[type] == NavigationRole.Definition && typeRoles[forward] == NavigationRole.Declaration, "전방 선언과 타입 정의");
    }

    public static void RunWorkerDefaults()
    {
        const long Gib = 1024L * 1024 * 1024;
        // 작업 2개를 쓰되 논리 코어 8개 미만이거나 VS 몫 8 GiB를 남기고 작업당 2.5 GiB를 잡을 수 없으면 1개입니다.
        Check(ClangdLaunchOptions.DefaultWorkerCount(16, 64 * Gib) == 2 && ClangdLaunchOptions.DefaultWorkerCount(64, 256 * Gib) == 2 &&
              ClangdLaunchOptions.DefaultWorkerCount(8, 16 * Gib) == 2 && ClangdLaunchOptions.DefaultWorkerCount(4, 32 * Gib) == 1 &&
              ClangdLaunchOptions.DefaultWorkerCount(16, 12 * Gib) == 1 && ClangdLaunchOptions.DefaultWorkerCount(1, 0) == 1 &&
              ClangdLaunchOptions.DefaultWorkerCount(12, 0) == 2, "clangd 색인 작업 수 기본값");
        Check(ClangdLaunchOptions.ResolveWorkerCount(3) == 3 && ClangdLaunchOptions.ResolveWorkerCount(0) >= 1, "지정한 작업 수 우선");
    }

    public static void RunDocumentErrors()
    {
        var diagnostics = JsonValue.Parse("[" +
            "{\"severity\":2,\"range\":{\"start\":{\"line\":1,\"character\":0}},\"message\":\"unused variable\"}," +
            "{\"severity\":1,\"range\":{\"start\":{\"line\":9,\"character\":2}},\"message\":\"use of undeclared identifier 'GEngine'\"}," +
            "{\"severity\":1,\"range\":{\"start\":{\"line\":4,\"character\":7}},\"message\":\"member access into incomplete type 'ULocalPlayer'\\n\\nLocalPlayer.h:3: note: forward declaration\"}]");
        var errors = DocumentErrors.From(diagnostics.Items);
        Check(errors is { Count: 2, FirstLine: 4, FirstMessage: "member access into incomplete type 'ULocalPlayer'" },
            "오류만 세고 가장 앞 오류의 첫 줄 메시지: " + errors?.FirstLine + " " + errors?.FirstMessage);
        Check(DocumentErrors.From(JsonValue.Parse("[{\"severity\":2,\"message\":\"w\"}]").Items) is null && DocumentErrors.From(JsonValue.Parse("[]").Items) is null,
            "경고만 있거나 비면 오류 요약 없음");
    }

    public static void RunMemoryPolicy()
    {
        const long Gib = 1024L * 1024 * 1024;
        Check(ClangdMemoryPolicy.DefaultLimitBytes(64 * Gib) == 8 * Gib && ClangdMemoryPolicy.DefaultLimitBytes(32 * Gib) == 4 * Gib &&
              ClangdMemoryPolicy.DefaultLimitBytes(8 * Gib) == 2 * Gib && ClangdMemoryPolicy.DefaultLimitBytes(256 * Gib) == 8 * Gib &&
              ClangdMemoryPolicy.DefaultLimitBytes(0) == 3 * Gib, "메모리 정리 기준 기본값(물리 메모리의 1/8, 2~8 GiB)");
        Check(ClangdMemoryPolicy.ResolveLimitBytes(2048) == 2 * Gib && ClangdMemoryPolicy.ResolveLimitBytes(0) >= 2 * Gib, "지정한 정리 기준 우선");

        var idle = TimeSpan.FromMinutes(10);
        ClangdMemorySample Sample(long bytes, bool indexing = false, bool busy = false, TimeSpan? sinceRequest = null, TimeSpan? sinceStart = null) =>
            new(bytes, indexing, busy, sinceRequest ?? idle, sinceStart ?? idle);

        var policy = new ClangdMemoryPolicy(4 * Gib);
        Check(!policy.ShouldRestart(Sample(3 * Gib)), "기준 이하는 유지");
        Check(!policy.ShouldRestart(Sample(5 * Gib, indexing: true)) && !policy.ShouldRestart(Sample(5 * Gib, busy: true)) &&
              !policy.ShouldRestart(Sample(5 * Gib, sinceRequest: TimeSpan.FromSeconds(30))) &&
              !policy.ShouldRestart(Sample(5 * Gib, sinceStart: TimeSpan.FromMinutes(1))), "색인·요청 중이거나 최근 요청·시작 직후에는 다시 시작하지 않음");
        Check(policy.ShouldRestart(Sample(5 * Gib)), "유휴 상태에서 기준을 넘으면 다시 시작");

        // 다시 시작한 직후와 색인을 다시 읽는 동안은 기준 사용량을 재지 않고, 안정된 뒤 첫 표본으로 잽니다.
        Check(!policy.ShouldRestart(Sample(5 * Gib, sinceStart: TimeSpan.FromSeconds(20))) &&
              !policy.ShouldRestart(Sample(5 * Gib, indexing: true)), "다시 시작 직후에는 판단하지 않음");
        Check(!policy.ShouldRestart(Sample(1 * Gib)) && policy.EffectiveLimitBytes == 4 * Gib, "작은 기준 사용량은 기준을 바꾸지 않음");
        Check(policy.ShouldRestart(Sample(5 * Gib)), "기준 사용량을 잰 뒤 다시 넘으면 다시 시작");

        // 다시 시작해도 기준 가까이 남는 큰 프로젝트: 기준을 1.5배로 올려 되풀이하지 않습니다.
        Check(!policy.ShouldRestart(Sample(3500L * 1024 * 1024)) && policy.EffectiveLimitBytes == 3500L * 1024 * 1024 * 3 / 2,
            "큰 기준 사용량이면 정리 기준을 올림: " + policy.EffectiveLimitBytes / (1024 * 1024));
        Check(!policy.ShouldRestart(Sample(5 * Gib)) && policy.ShouldRestart(Sample(6 * Gib)), "올린 기준으로 판단");
        Check(!new ClangdMemoryPolicy(0).ShouldRestart(Sample(64 * Gib)), "기준 0은 정리하지 않음");

        // 색인 결과 다시 읽기는 메모리와 상관없이, 색인·요청이 멈추고 짧은 유휴 뒤에 하되 Solution마다 횟수를 제한합니다.
        var quiet = Sample(1 * Gib, sinceRequest: ClangdMemoryPolicy.ReloadIdle, sinceStart: TimeSpan.FromSeconds(30));
        Check(ClangdMemoryPolicy.ShouldReload(true, quiet, 0) && !ClangdMemoryPolicy.ShouldReload(false, quiet, 0) &&
              !ClangdMemoryPolicy.ShouldReload(true, Sample(1 * Gib, indexing: true), 0) && !ClangdMemoryPolicy.ShouldReload(true, Sample(1 * Gib, busy: true), 0) &&
              !ClangdMemoryPolicy.ShouldReload(true, Sample(1 * Gib, sinceRequest: TimeSpan.FromSeconds(3)), 0) &&
              !ClangdMemoryPolicy.ShouldReload(true, quiet, ClangdMemoryPolicy.MaxReloads), "색인 결과 다시 읽기 판단");

        // 색인한 세션은 정리 기준보다 적어도, 오래 쉬고 남은 메모리가 하한을 넘으면 한 번 다시 시작해 돌려받습니다.
        var indexed = Sample(ClangdMemoryPolicy.ReclaimFloorBytes + Gib / 10, sinceRequest: ClangdMemoryPolicy.DefaultIdle, sinceStart: TimeSpan.FromMinutes(1));
        Check(ClangdMemoryPolicy.ShouldReclaimAfterIndex(12, indexed, 0) && !ClangdMemoryPolicy.ShouldReclaimAfterIndex(0, indexed, 0) &&
              !ClangdMemoryPolicy.ShouldReclaimAfterIndex(12, Sample(ClangdMemoryPolicy.ReclaimFloorBytes / 2), 0) &&
              !ClangdMemoryPolicy.ShouldReclaimAfterIndex(12, Sample(2 * Gib, sinceRequest: ClangdMemoryPolicy.ReloadIdle), 0) &&
              !ClangdMemoryPolicy.ShouldReclaimAfterIndex(12, Sample(2 * Gib, indexing: true), 0) &&
              !ClangdMemoryPolicy.ShouldReclaimAfterIndex(12, indexed, ClangdMemoryPolicy.MaxReclaims), "색인 뒤 메모리 회수 판단");

        // 로그 형식 확인은 이 세션이 쓴 색인 파일이 있는지로 실제 색인을 판단합니다.
        var shards = Path.Combine(Path.GetTempPath(), "VisualBoost.Shards." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(shards);
            File.WriteAllText(Path.Combine(shards, "a.idx"), "x");
            var written = DateTime.UtcNow;
            // clangd가 시작할 때마다 다시 쓰는 .gitignore는 색인으로 보지 않습니다.
            File.WriteAllText(Path.Combine(shards, ".gitignore"), "*");
            File.SetLastWriteTimeUtc(Path.Combine(shards, ".gitignore"), written.AddMinutes(5));
            Check(!ClangdNavigator.IndexWrittenSince(shards, written.AddMinutes(1)) && ClangdNavigator.IndexWrittenSince(shards, DateTime.UtcNow.AddMinutes(-1)) && !ClangdNavigator.IndexWrittenSince(shards, DateTime.UtcNow.AddMinutes(1)) &&
                  !ClangdNavigator.IndexWrittenSince(Path.Combine(shards, "none"), DateTime.MinValue), "세션 뒤 쓴 색인 파일 판단");
        }
        finally
        {
            TryDelete(shards);
        }
    }

    public static void RunOwnDefinitionReferences()
    {
        // clangd 22가 클래스 참조로 돌려준 줄·열·소속 이름을 그대로 씁니다. 1은 뺄 위치, 0은 남길 위치입니다.
        var cases = new (string Line, int Character, string? Container, string Name, int Own)[]
        {
            ("class AActorX {", 6, null, "AActorX", 0),
            ("    AActorX();", 4, "AActorX", "AActorX", 1),
            ("    ~AActorX();", 5, "AActorX", "AActorX", 1),
            ("    static AActorX* Get();", 11, "AActorX::Get", "AActorX", 0),
            ("    AActorX(const AActorX& o) {}", 4, "AActorX", "AActorX", 1),
            ("    AActorX(const AActorX& o) {}", 18, "AActorX::AActorX", "AActorX", 0),
            ("    static AActorX Make() { return AActorX(); }", 11, "AActorX::Make", "AActorX", 0),
            ("    static AActorX Make() { return AActorX(); }", 35, "AActorX::Make", "AActorX", 0),
            ("int AActorX::Count = 0;", 4, "AActorX::Count", "AActorX", 1),
            ("AActorX::AActorX() {}", 0, "AActorX::AActorX", "AActorX", 1),
            ("AActorX::AActorX() {}", 9, "AActorX", "AActorX", 1),
            ("AActorX::~AActorX() {}", 0, "AActorX::~AActorX", "AActorX", 1),
            ("AActorX::~AActorX() {}", 10, "AActorX", "AActorX", 1),
            ("void AActorX::BeginPlay()", 5, "AActorX::BeginPlay", "AActorX", 1),
            ("    AActorX* Self = AActorX::Get();", 4, "AActorX::BeginPlay", "AActorX", 0),
            ("    AActorX* Self = AActorX::Get();", 20, "AActorX::BeginPlay", "AActorX", 0),
            ("void AActorX::FInner::Do() {}", 5, "AActorX::FInner::Do", "AActorX", 1),
            ("AActorX MakeOne() { return AActorX(); }", 0, "MakeOne", "AActorX", 0),
            ("AActorX MakeOne() { return AActorX(); }", 27, "MakeOne", "AActorX", 0),
            ("AActorX* AActorX::Get() { return nullptr; }", 0, "AActorX::Get", "AActorX", 0),
            ("AActorX* AActorX::Get() { return nullptr; }", 9, "AActorX::Get", "AActorX", 1),
            ("int C() { AActorX X; return AActorX::Count + ns::Helper(); }", 28, "C", "AActorX", 0),
            ("void ns::AActorX::Tick(float DeltaTime)", 9, "ns::AActorX::Tick", "AActorX", 1),
            ("template <class T> struct TBox { void Put(T v); TBox(); };", 48, "TBox", "TBox", 1),
            ("template <class T> void TBox<T>::Put(T v) {}", 24, "TBox::Put", "TBox", 1),
            ("template <class T> TBox<T>::TBox() {}", 19, "TBox::TBox<T>", "TBox", 1),
            ("template <class T> TBox<T>::TBox() {}", 28, "TBox", "TBox", 1),
            // 생성자·소멸자 몸체와 초기화 목록 안의 사용은 소속 이름이 그 생성자·소멸자입니다(clangd 22 확인).
            ("    AActorX(double d) : AActorX(1) {}", 4, "AActorX", "AActorX", 1),
            ("    AActorX(double d) : AActorX(1) {}", 24, "AActorX::AActorX", "AActorX", 0),
            ("AActorX::AActorX() : AActorX(0) {}", 0, "AActorX::AActorX", "AActorX", 1),
            ("AActorX::AActorX() : AActorX(0) {}", 9, "AActorX", "AActorX", 1),
            ("AActorX::AActorX() : AActorX(0) {}", 21, "AActorX::AActorX", "AActorX", 0),
            ("    AActorX Copy = AActorX(1);", 4, "AActorX::AActorX", "AActorX", 0),
            ("    AActorX Copy = AActorX(1);", 19, "AActorX::AActorX", "AActorX", 0),
            ("AActorX::~AActorX() { AActorX(2); }", 22, "AActorX::~AActorX", "AActorX", 0),
            ("AActorX::AActorX(const AActorX&) = default;", 0, "AActorX::AActorX", "AActorX", 1),
            // 몸체 안에서 자기 자신을 한정해 부르는 호출은 정의 머리와 사슬이 같지만 남깁니다.
            ("int AActorX::F(int n) { return n ? AActorX::F(n - 1) : AActorX::F(n, 0); }", 4, "AActorX::F", "AActorX", 1),
            ("int AActorX::F(int n) { return n ? AActorX::F(n - 1) : AActorX::F(n, 0); }", 35, "AActorX::F", "AActorX", 0),
            ("int AActorX::F(int n) { return n ? AActorX::F(n - 1) : AActorX::F(n, 0); }", 55, "AActorX::F", "AActorX", 0),
            ("int AActorX::F(int n, int m)", 4, "AActorX::F", "AActorX", 1),
            ("    AActorX::F(n - 1);", 4, "AActorX::F", "AActorX", 0),
            ("int* AActorX::F(int n) const {", 5, "AActorX::F", "AActorX", 1),
            ("    return x * AActorX::F(n);", 15, "AActorX::F", "AActorX", 0),
            ("    if (p) p->AActorX::F(1);", 14, "AActorX::F", "AActorX", 0),
            ("void AActorX::F(int n,", 5, "AActorX::F", "AActorX", 1),
            // 같은 줄 뒤의 주석은 줄 끝으로 봅니다. 매크로로 감싼 정의 머리(Unreal 생성 코드)는 앞 글자 '('로 호출처럼 보여 남습니다.
            ("void AActorX::F() // 설명", 5, "AActorX::F", "AActorX", 1),
            ("void AActorX::F() /* 설명 */ {", 5, "AActorX::F", "AActorX", 1),
            ("DEFINE_FUNCTION(AActorX::execFoo)", 16, "AActorX::execFoo", "AActorX", 0),
            // 연산자 정의 머리는 사슬(operator)과 소속 이름(operator=)이 달라 남습니다.
            ("AActorX& AActorX::operator=(const AActorX& o)", 9, "AActorX::operator=", "AActorX", 0),
        };
        var locations = cases.Select((c, i) => new NavigationLocation(@"C:\p\a.h", i, c.Character, i, c.Character + c.Name.Length, c.Container)).ToArray();
        var lines = cases.Select(c => c.Line).ToArray();
        var kept = OwnDefinitionReferences.Kept(locations, lines, symbolIsType: true);
        var expected = Enumerable.Range(0, cases.Length).Where(i => cases[i].Own == 0).ToArray();
        Check(kept.SequenceEqual(expected), "클래스 자신의 정의 이름 제외: " + string.Join(",", Enumerable.Range(0, cases.Length).Where(i => kept.Contains(i) != (cases[i].Own == 0))));

        // 함수 참조: 재귀 호출은 생성자와 모양이 같으므로 타입이 아니면 남깁니다. 함수 정의의 소속 이름은 클래스라 어느 규칙에도 걸리지 않습니다.
        var recursive = new NavigationLocation(@"C:\p\b.cpp", 0, 26, 0, 29, "AActorX::Get");
        Check(!OwnDefinitionReferences.IsOwnDefinitionName(recursive, "AActorX* AActorX::Get() { Get(); }", symbolIsType: false), "재귀 호출 유지");
        var functionDefinition = new NavigationLocation(@"C:\p\b.cpp", 0, 14, 0, 23, "AActorX");
        Check(!OwnDefinitionReferences.IsOwnDefinitionName(functionDefinition, "void AActorX::BeginPlay()", symbolIsType: false), "함수 정의 유지");
        // 타입이 아니면 생성자 규칙은 쓰지 않지만 한정자 규칙은 씁니다(함수 이름 뒤에는 ::가 오지 않음).
        Check(!OwnDefinitionReferences.IsOwnDefinitionName(locations[1], lines[1], symbolIsType: false) &&
              OwnDefinitionReferences.IsOwnDefinitionName(locations[13], lines[13], symbolIsType: false), "종류를 모를 때");
        // 소속 이름이 없거나 줄이 범위와 맞지 않으면 빼지 않습니다.
        Check(!OwnDefinitionReferences.IsOwnDefinitionName(new NavigationLocation(@"C:\p\b.cpp", 0, 5, 0, 12), "void AActorX::BeginPlay()", true) &&
              !OwnDefinitionReferences.IsOwnDefinitionName(locations[13], "short", true) &&
              !OwnDefinitionReferences.IsOwnDefinitionName(locations[13], null, true), "근거 부족");
    }

    /// <summary>참조 결과를 거르는 근거(조건식 매크로, 매크로가 펼친 위치, 정의되지 않은 조건 매크로 진단, clangd 색인 파일)를 확인합니다.</summary>
    public static void RunReferenceFilters()
    {
        var used = ConditionMacros.Used(
            "#define LOCAL_FLAG 1\n" +
            "#if WITH_EDITOR && !defined(UE_BUILD_SHIPPING) // WITH_COMMENT\n" +
            "#elif PLATFORM_WINDOWS || \\\n    WITH_CONTINUED\n" +
            "#  if UE_VERSION_NEWER_THAN(5, 3, NOT_A_MACRO) and __has_include(<x.h>) && _MSC_VER >= 1930 && LOCAL_FLAG\n" +
            "#if true /* WITH_BLOCK */ || WITH_EDITOR\n" +
            "#ifdef IGNORED_IFDEF\n#undef UNDEFINED_HERE\n#if UNDEFINED_HERE\n#endif\n");
        Check(used.SequenceEqual(new[] { "WITH_EDITOR", "PLATFORM_WINDOWS", "WITH_CONTINUED", "UE_VERSION_NEWER_THAN" }),
            "조건식 매크로(defined·인수·예약·키워드·주석·파일 안 정의 제외, 줄 이음 포함): " + string.Join(",", used));

        // 매크로가 펼친 위치: 범위 글자가 찾는 이름과 다른 식별자일 때만 뺍니다.
        var fname = new NavigationLocation(@"C:\p\a.h", 3, 1, 3, 15);
        Check(OwnDefinitionReferences.IsMacroExpansion(fname, "\tGENERATED_BODY()", "FName") &&
              !OwnDefinitionReferences.IsMacroExpansion(new NavigationLocation(@"C:\p\a.h", 3, 1, 3, 6), "\tFName X;", "FName") &&
              !OwnDefinitionReferences.IsMacroExpansion(fname, "\tGENERATED_BODY()", "operator==") &&
              !OwnDefinitionReferences.IsMacroExpansion(new NavigationLocation(@"C:\p\a.h", 3, 1, 3, 3), "\t==", "FName") &&
              !OwnDefinitionReferences.IsMacroExpansion(fname, null, "FName") && !OwnDefinitionReferences.IsMacroExpansion(fname, "short", "FName"),
            "매크로가 펼친 위치 판단");
        var kept = OwnDefinitionReferences.Kept(new[] { fname, new NavigationLocation(@"C:\p\a.h", 4, 1, 4, 6) }, new[] { "\tGENERATED_BODY()", "\tFName X;" }, true, "FName");
        Check(kept.SequenceEqual(new[] { 1 }), "이름을 주면 매크로가 펼친 위치를 뺌");
        // 요청 위치에 쓰인 이름: symbolInfo의 대표 항목(별칭·매크로)을 고르는 근거입니다.
        const string Spelled = "int a;\r\n  using FPair = TTuple<int>; x2 = 1;\n";
        Check(ClangdNavigator.IdentifierAt(Spelled, 1, 8) == "FPair" && ClangdNavigator.IdentifierAt(Spelled, 1, 13) == "FPair" &&
              ClangdNavigator.IdentifierAt(Spelled, 1, 14) is null && ClangdNavigator.IdentifierAt(Spelled, 1, 30) == "x2" && ClangdNavigator.IdentifierAt(Spelled, 1, 99) is null &&
              ClangdNavigator.IdentifierAt(Spelled, 5, 0) is null && ClangdNavigator.IdentifierAt("a 12b", 0, 3) is null, "요청 위치의 식별자");
        // 한정자 후보: 이름 뒤에 (템플릿 인수를 건너뛰고) ::가 오는 위치. 다른 이름의 일부는 후보가 아닙니다.
        var qualified = "std::vector<int> a; // std::x\r\nint b = Ostd::y + std ::z + TBox<int, TPair<a, b>>::F() + TBox<int> c + std;\n";
        var second = qualified.Split('\n')[1];
        Check(ClangdNavigator.QualifierCandidates(qualified, "std").SequenceEqual(new[] { (0, 0), (0, qualified.IndexOf("std::x", StringComparison.Ordinal)), (1, second.IndexOf("std ::", StringComparison.Ordinal)) }) &&
              ClangdNavigator.QualifierCandidates(qualified, "TBox").SequenceEqual(new[] { (1, second.IndexOf("TBox", StringComparison.Ordinal)) }),
            "한정자 후보: " + string.Join(",", ClangdNavigator.QualifierCandidates(qualified, "std")));
        // 코드 안의 이름: 주석·문자열·문자·원시 문자열은 빼고, 전처리 줄(이어진 줄 포함)은 표시합니다.
        var words = "int TEXT_x; // TEXT\n#define M(x) TEXT(x) \\\n   TEXT\n/* TEXT\n TEXT */ auto s = TEXT(\"TEXT\"); char c = 'T'; auto r = R\"(TEXT)\"; TEXT\r\n" +
                    "#if X\n#elif Y\n#  else\n#endif\n#include \"a.h\"\n";
        var wordLines = words.Split('\n');
        var foundWords = CodeWords.Find(words, "TEXT").Select(w => (w.Line, w.Character, w.Directive)).ToArray();
        Check(foundWords.SequenceEqual(new[]
              {
                  (1, wordLines[1].IndexOf("TEXT", StringComparison.Ordinal), true), (2, 3, true),
                  (4, wordLines[4].IndexOf("TEXT(", StringComparison.Ordinal), false), (4, wordLines[4].LastIndexOf("TEXT", StringComparison.Ordinal), false)
              }) && CodeWords.ConditionalLines(words).SequenceEqual(new[] { 5, 6, 7, 8 }),
            "코드 안의 이름과 조건부 지시문 줄: " + string.Join(",", foundWords));

        Check(DocumentErrors.HasUndefinedConditionMacro(JsonValue.Parse("[{\"severity\":2,\"code\":\"-Wundef\",\"message\":\"'X' is not defined, evaluates to 0\"}]").Items) &&
              !DocumentErrors.HasUndefinedConditionMacro(JsonValue.Parse("[{\"severity\":2,\"code\":\"unused_variable\"},{\"severity\":2}]").Items),
            "정의되지 않은 조건 매크로 진단");

        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.IndexShards." + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "src", "Use.cpp");
            var other = Path.Combine(root, "src", "Other", "Use.cpp");
            Write(source, "int x;\n");
            Write(other, "int y;\n");
            var index = Path.Combine(root, "index");
            Directory.CreateDirectory(index);
            var references = new[] { (0x0102030405060708UL, (byte)13, 2, 4, 9), (0x0102030405060708UL, (byte)4, 5, 0, 5), (0xA0B0C0D0E0F00011UL, (byte)12, 7, 2, 6) };
            var plain = IndexShard(ClangdIndexShards.FormatVersion, DocumentUri.FromPath(source), false, references);
            var parsed = ClangdIndexShards.Parse(plain)!;
            Check(parsed.Count == 3 && parsed[0].SymbolId == "0102030405060708" && parsed[0].Kind == 13 && parsed[0].Spelled && parsed[0].Line == 2 &&
                  parsed[0].Character == 4 && parsed[0].EndCharacter == 9 && !parsed[1].Spelled && parsed[2].SymbolId == "A0B0C0D0E0F00011" &&
                  string.Equals(Path.GetFullPath(parsed[0].Path), source, StringComparison.OrdinalIgnoreCase), "색인 파일 참조 읽기");
            Check(ClangdIndexShards.Parse(IndexShard(ClangdIndexShards.FormatVersion, DocumentUri.FromPath(source), true, references))!.Select(r => r.Line)
                  .SequenceEqual(new[] { 2, 5, 7 }), "압축한 문자열 표");
            Check(ClangdIndexShards.Parse(IndexShard(ClangdIndexShards.FormatVersion + 1, DocumentUri.FromPath(source), false, references)) is null, "다른 형식 버전은 읽지 않음");
            Check(Throws<InvalidDataException>(() => ClangdIndexShards.Parse(plain.Take(plain.Length - 9).ToArray())) &&
                  Throws<InvalidDataException>(() => ClangdIndexShards.Parse(Encoding.ASCII.GetBytes("RIFF\0\0\0\0XXXX"))), "깨진 색인 파일");

            // 같은 이름의 다른 파일 색인은 경로로 가르고, 원본보다 오래된 색인 파일은 믿지 않습니다.
            var shard = Path.Combine(index, "Use.cpp.0123456789ABCDEF.idx");
            File.WriteAllBytes(shard, plain);
            File.WriteAllBytes(Path.Combine(index, "Use.cpp.FEDCBA9876543210.idx"), IndexShard(ClangdIndexShards.FormatVersion, DocumentUri.FromPath(other), false, references[2]));
            var shards = new ClangdIndexShards(index);
            Check(shards.ReferencesIn(source) is { Count: 3 } && shards.ReferencesIn(other) is { Count: 1 } && shards.ReferencesIn(Path.Combine(root, "src", "None.cpp")) is null,
                "파일별 색인 참조");
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(5));
            Check(shards.ReferencesIn(source) is null, "원본이 더 새로우면 읽지 않음");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>clangd background index 색인 파일(RIFF <c>CdIx</c>)을 참조 표만 담아 만듭니다. 모든 참조의 파일은 문자열 0번입니다.</summary>
    private static byte[] IndexShard(uint version, string fileUri, bool compressStrings, params (ulong Id, byte Kind, int Line, int Character, int EndCharacter)[] references)
    {
        var body = new MemoryStream();
        void Chunk(string id, byte[] data)
        {
            body.Write(Encoding.ASCII.GetBytes(id));
            body.Write(BitConverter.GetBytes((uint)data.Length));
            body.Write(data);
            if ((data.Length & 1) != 0) body.WriteByte(0);
        }

        Chunk("meta", BitConverter.GetBytes(version));
        var raw = Encoding.UTF8.GetBytes(fileUri + "\0");
        if (compressStrings)
        {
            var compressed = new MemoryStream();
            using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true)) zlib.Write(raw);
            Chunk("stri", BitConverter.GetBytes((uint)raw.Length).Concat(compressed.ToArray()).ToArray());
        }
        else
        {
            Chunk("stri", BitConverter.GetBytes(0u).Concat(raw).ToArray());
        }

        var refs = new MemoryStream();
        void Var(int value)
        {
            var v = (uint)value;
            while (v >= 0x80)
            {
                refs.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }

            refs.WriteByte((byte)v);
        }

        foreach (var group in references.GroupBy(r => r.Id))
        {
            refs.Write(BitConverter.GetBytes(group.Key).Reverse().ToArray());
            Var(group.Count());
            foreach (var reference in group)
            {
                refs.WriteByte(reference.Kind);
                Var(0);
                Var(reference.Line);
                Var(reference.Character);
                Var(reference.Line);
                Var(reference.EndCharacter);
                refs.Write(new byte[8]);
            }
        }

        Chunk("refs", refs.ToArray());
        var content = body.ToArray();
        return Encoding.ASCII.GetBytes("RIFF").Concat(BitConverter.GetBytes((uint)(content.Length + 4))).Concat(Encoding.ASCII.GetBytes("CdIx")).Concat(content).ToArray();
    }

    /// <summary>
    /// 모듈 규칙 파일로 만든 근사 명령을 확인합니다: 의존 사슬의 공개 경로, 엔진끼리만 보는 Internal, 짧은 이름 생성 폴더, 경로 변수,
    /// 의존 모듈 API 매크로와 공개 정의, 생성 소스 대체 파일.
    /// </summary>
    public static void RunUnrealModuleGraph()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.ModuleGraph." + Guid.NewGuid().ToString("N"));
        try
        {
            var engineRoot = Path.Combine(root, "Engine Root");
            var runtime = Path.Combine(engineRoot, "Engine", "Source", "Runtime");
            string Module(string directory, string name, string rules, params string[] folders)
            {
                var path = Path.Combine(directory, name);
                Write(Path.Combine(path, name + ".Build.cs"), rules);
                foreach (var folder in folders) Directory.CreateDirectory(Path.Combine(path, folder));
                return path;
            }

            var core = Module(runtime, "Core", "PublicDefinitions.Add(\"WITH_CORE_FLAG=1\");\nPublicDefinitions.Add(\"WITH_COMPUTED=\" + (Target.bX ? \"1\" : \"0\"));", "Public", "Internal");
            var renderCore = Module(runtime, "RenderCore", "", "Public");
            var renderer = Module(runtime, "Renderer", "PublicDependencyModuleNames.Add(\"RenderCore\");", "Public", "Private");
            var unused = Module(runtime, "Unused", "", "Public");
            var engineModule = Module(runtime, "Engine",
                "PublicDependencyModuleNames.AddRange(new string[] { \"Core\" });\n// PublicDependencyModuleNames.Add(\"Unused\");\n" +
                "PrivateDependencyModuleNames.Add(\"Renderer\");\nstring Extra = Path.Combine(ModuleDirectory, \"Extra\");\nPublicIncludePaths.Add(Extra);",
                "Public", "Private", "Extra");
            var plugin = Path.Combine(engineRoot, "Engine", "Plugins", "Group", "Material");
            Write(Path.Combine(plugin, "Material.uplugin"), "{}");
            var editor = Module(Path.Combine(plugin, "Source"), "MaterialEditorTools", "ShortName = \"MatEd\";\nPrivateDependencyModuleNames.Add(\"Engine\");", "Public", "Private");
            var generated = Path.Combine(plugin, "Intermediate", "Build", "Win64", "UnrealEditor", "Inc", "MatEd", "UHT");
            Directory.CreateDirectory(generated);
            var widget = Path.Combine(editor, "Private", "Widget.cpp");
            Write(widget, "#include \"Widget.h\"\n#include UE_INLINE_GENERATED_CPP_BY_NAME(Widget)\n");

            var project = Path.Combine(root, "Game");
            var gameModule = Module(Path.Combine(project, "Source"), "Game", "PublicDependencyModuleNames.Add(\"Engine\");", "Private");
            var graph = UnrealModuleGraph.For(engineRoot, project);
            graph.Prepare();
            Check(graph.DirectoryOf("Renderer") == renderer && graph.DirectoryOf("Missing") is null && graph.RulesOf("MaterialEditorTools")!.ShortName == "MatEd",
                "모듈 위치와 짧은 이름");

            var environment = graph.Environment("MaterialEditorTools", editor);
            var includes = environment.IncludeDirectories.ToList();
            int At(string directory) => includes.FindIndex(d => string.Equals(d, directory, StringComparison.OrdinalIgnoreCase));
            Check(At(Path.Combine(editor, "Private")) == 0 && At(generated) > 0 && At(Path.Combine(engineModule, "Public")) > 0 && At(Path.Combine(engineModule, "Extra")) > 0 &&
                  At(Path.Combine(core, "Public")) > 0 && At(Path.Combine(core, "Internal")) > 0 && At(Path.Combine(renderer, "Public")) < 0 &&
                  At(Path.Combine(engineModule, "Private")) < 0 && At(Path.Combine(unused, "Public")) < 0,
                "공개 의존 사슬만 전파(비공개 의존의 의존·주석 제외), 엔진 플러그인은 엔진 Internal을 봄: " + string.Join(" | ", includes.Select(d => d.Substring(root.Length))));
            Check(At(Path.GetDirectoryName(editor)!) > At(Path.Combine(core, "Public")), "상위 폴더 경로는 뒤에");
            Check(environment.ApiModules.SequenceEqual(new[] { "MaterialEditorTools", "Engine", "Core" }) && environment.Definitions.SequenceEqual(new[] { "WITH_CORE_FLAG=1" }),
                "API 모듈과 상수 정의(계산식 제외): " + string.Join(",", environment.ApiModules) + " / " + string.Join(",", environment.Definitions));
            var engineEnvironment = graph.Environment("Engine", engineModule);
            Check(engineEnvironment.IncludeDirectories.Contains(Path.Combine(core, "Internal")) && engineEnvironment.IncludeDirectories.Contains(Path.Combine(renderer, "Public")) &&
                  engineEnvironment.IncludeDirectories.Contains(Path.Combine(renderCore, "Public")), "엔진 모듈은 Internal과 비공개 의존의 공개 사슬");
            Check(!graph.Environment("Game", gameModule).IncludeDirectories.Contains(Path.Combine(core, "Internal")), "프로젝트 모듈은 엔진 Internal을 보지 않음");

            // 근사 명령: 의존 경로는 프로젝트 명령의 경로보다 앞, 재정의 헤더는 의존 API·정의를 채우고, 생성 소스는 빈 대체 파일로 찾습니다.
            var gameSource = Path.Combine(gameModule, "Private", "Game.cpp");
            Write(gameSource, "int G;");
            var projectCommand = new CompileCommand(Path.Combine(engineRoot, "Engine", "Source"), gameSource.Replace('\\', '/'),
                new[] { "clang-cl.exe", "--driver-mode=cl", "/DGAME=1", "/I", "Runtime/Core/Public", gameSource.Replace('\\', '/') });
            var overrides = Path.Combine(root, "modules");
            var command = UnrealCompileCommands.Synthesize(widget, new[] { projectCommand }, overrides, graph: graph)!;
            var arguments = command.Arguments.ToList();
            var leading = arguments.IndexOf(Path.Combine(editor, "Private").Replace('\\', '/'));
            Check(leading > 0 && leading < arguments.IndexOf("Runtime/Core/Public") && arguments.Count(a => string.Equals(a, Path.Combine(core, "Public").Replace('\\', '/'),
                      StringComparison.OrdinalIgnoreCase)) == 0, "의존 경로는 기존 경로 앞, 기존 경로와 같은 폴더는 다시 넣지 않음: " + string.Join(" ", arguments));
            var header = File.ReadAllText(Path.Combine(overrides, "MaterialEditorTools.h"));
            Check(header.Contains("#define MATERIALEDITORTOOLS_API\n") && header.Contains("#ifndef ENGINE_API\n#define ENGINE_API\n#endif\n") &&
                  header.Contains("#ifndef WITH_CORE_FLAG\n#define WITH_CORE_FLAG 1\n#endif\n") && header.Contains("#define UE_IS_ENGINE_MODULE 1") &&
                  !header.Contains("WITH_COMPUTED"), "재정의 헤더: " + header);
            var stubs = Path.Combine(overrides, "generated-stubs");
            Check(File.Exists(Path.Combine(stubs, "Widget.gen.cpp")) && arguments[arguments.Count - 2] == stubs.Replace('\\', '/') && arguments[arguments.Count - 3] == "/I",
                "생성 소스 대체 파일 폴더는 마지막 포함 경로");
            Check(UnrealCompileCommands.Synthesize(gameSource, new[] { projectCommand }, overrides, graph: graph) is not null &&
                  File.ReadAllText(Path.Combine(overrides, "Game.h")) is var gameHeader && !gameHeader.Contains("UE_IS_ENGINE_MODULE") &&
                  gameHeader.Contains("#ifndef ENGINE_API\n"), "프로젝트 모듈 재정의 헤더는 엔진 표시 없이 의존 API만");
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static void RunPathAliases()
    {
        // clangd에는 실제 경로를 보내고, 받은 경로는 연 경로로 되돌립니다. 같아진 위치는 처음 것만 남깁니다.
        var real = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"J:\Smoke"] = @"C:\Real\Smoke",
            [@"J:\Smoke\Plugins\Shared"] = @"D:\Shared",
            [@"P:\"] = @"C:\Work\Proj",
        };
        string? Resolve(string path) => real.TryGetValue(path, out var found) ? found : path;
        var aliases = PathAliases.ForRoots(new[] { @"J:\Smoke\", @"J:\Smoke\Plugins\Shared", @"C:\Plain", null, @"P:\" }, Resolve);
        Check(aliases.Count == 3 && PathAliases.ForRoots(new[] { @"C:\Plain" }, Resolve) == PathAliases.None, "링크를 거친 루트만 대응");
        var cased = PathAliases.ForRoots(new[] { @"C:\work\engine" }, path => @"C:\Work\Engine");
        Check(cased.Count == 1 && cased.ToReal(@"C:\work\engine\Source\A.h") == @"C:\Work\Engine\Source\A.h" &&
              cased.ToGiven(@"C:\Work\Engine\Source\A.h") == @"C:\work\engine\Source\A.h", "대소문자만 다른 루트도 디스크 대소문자로 대응");
        Check(aliases.ToGiven(@"C:\Real\Smoke\App\Main.cpp") == @"J:\Smoke\App\Main.cpp" && aliases.ToGiven(@"c:\real\smoke") == @"J:\Smoke" &&
              aliases.ToGiven(@"D:\Shared\Lib.h") == @"J:\Smoke\Plugins\Shared\Lib.h", "실제 경로 → 연 경로(대소문자 무시·가장 긴 루트)");
        Check(aliases.ToReal(@"J:\Smoke\App\Main.cpp") == @"C:\Real\Smoke\App\Main.cpp" && aliases.ToReal(@"J:\Smoke\Plugins\Shared\Lib.h") == @"D:\Shared\Lib.h" &&
              aliases.ToReal("J:/Smoke/App/Main.cpp") == "C:/Real/Smoke/App/Main.cpp", "연 경로 → 실제 경로('/' 구분자 유지)");
        Check(aliases.ToReal(@"P:\Source\A.cpp") == @"C:\Work\Proj\Source\A.cpp" && aliases.ToGiven(@"C:\Work\Proj\Source\A.cpp") == @"P:\Source\A.cpp",
            "subst 드라이브 루트 왕복");
        Check(aliases.ToGiven(@"C:\Real\Smoke2\a.cpp") == @"C:\Real\Smoke2\a.cpp" && aliases.ToReal(@"J:\Smoke2\a.cpp") == @"J:\Smoke2\a.cpp" &&
              aliases.ToReal(@"C:\Other\a.cpp") == @"C:\Other\a.cpp", "폴더 이름 일부만 같은 경로는 그대로");
        var commands = aliases.ToReal(new[] { new CompileCommand("J:/Smoke/App", "J:/Smoke/App/Main.cpp", new[] { "clang-cl.exe", "/IJ:/Smoke/inc" }) });
        Check(commands[0].Directory == "C:/Real/Smoke/App" && commands[0].File == "C:/Real/Smoke/App/Main.cpp" && commands[0].Arguments[1] == "/IJ:/Smoke/inc",
            "compilation database 명령은 파일·작업 폴더만 실제 경로");
        var mapped = aliases.ToGiven(new[]
        {
            new NavigationLocation(@"J:\Smoke\App\Main.cpp", 3, 50, 3, 56),
            new NavigationLocation(@"C:\Real\Smoke\App\Main.cpp", 3, 50, 3, 56, "main"),
            new NavigationLocation(@"C:\Real\Smoke\Tool\Tool.cpp", 1, 24, 1, 30, "ToolMain"),
        });
        Check(mapped.Count == 2 && mapped[0].Path == @"J:\Smoke\App\Main.cpp" && mapped[1].Path == @"J:\Smoke\Tool\Tool.cpp" &&
              mapped[1].Container == "ToolMain" && mapped[1].EndCharacter == 30, "같아진 위치 병합과 순서 유지");
        var plain = new[] { new NavigationLocation(@"C:\Work\a.cpp", 0, 0, 0, 1) };
        Check(ReferenceEquals(PathAliases.None.ToGiven(plain), plain) && PathAliases.None.ToReal(@"J:\Smoke\a.cpp") == @"J:\Smoke\a.cpp",
            "링크가 없으면 경로를 바꾸지 않음");

        // 실제 junction에서 Windows 최종 경로 조회와 compile_commands.json 기록을 확인합니다. 테스트 전용 임시 폴더만 만들고 지웁니다.
        var created = Path.Combine(Path.GetTempPath(), "VisualBoost.PathAliases." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(created);
        // TEMP가 8.3 짧은 이름이나 링크를 거치면 링크가 아닌 폴더도 대응이 생기므로, 실제 경로로 푼 임시 폴더 아래에서 확인합니다.
        var root = PathAliases.ForRoots(new[] { created }).ToReal(created);
        var target = Path.Combine(root, "real");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(Path.Combine(target, "App"));
        try
        {
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false }))
            {
                mklink!.WaitForExit();
                Check(mklink.ExitCode == 0 && Directory.Exists(Path.Combine(link, "App")), "테스트 junction 생성");
            }

            var system = PathAliases.ForRoots(new[] { Path.Combine(link, "App"), target, Path.Combine(root, "missing") });
            Check(system.Count == 1 && system.ToGiven(Path.Combine(target, "App", "Main.cpp")) == Path.Combine(link, "App", "Main.cpp") &&
                  system.ToReal(Path.Combine(link, "App", "Main.cpp")) == Path.Combine(target, "App", "Main.cpp"),
                "junction 실제 경로 조회(없는 폴더·링크 아닌 폴더 제외): " + system.ToGiven(Path.Combine(target, "App", "Main.cpp")));

            File.WriteAllText(Path.Combine(target, "App.sln"), string.Empty);
            var source = Path.Combine(link, "App", "Main.cpp").Replace('\\', '/');
            File.WriteAllText(Path.Combine(target, CompileCommandDatabase.FileName),
                new CompileCommand(Path.Combine(link, "App").Replace('\\', '/'), source, new[] { "clang-cl.exe", "/c", source }).ToJson().ToJson().Insert(0, "[") + "]");
            var context = CompileContextBuilder.Prepare(Path.Combine(link, "App.sln"), Path.Combine(root, "cache"), null, "clang-cl.exe");
            var written = CompileCommandDatabase.Read(Path.Combine(context.Directory, CompileCommandDatabase.FileName));
            var realSource = Path.Combine(target, "App", "Main.cpp").Replace('\\', '/');
            Check(context.Paths.Count == 1 && context.Commands[0].File == source && written.Count == 2 && written[0].File == realSource &&
                  written[0].Arguments[2] == source, "링크를 거쳐 연 Solution의 compile_commands.json은 실제 파일 경로: " + written[0].File);
            // 색인 시작용 빈 문서는 자기 명령을 가져 가까운 TU의 강제 include를 빌리지 않습니다.
            var probe = context.IndexStartPath.Replace('\\', '/');
            Check(context.Commands.Count == 1 && written[1].File == probe && written[1].Arguments.SequenceEqual(new[] { "clang-cl.exe", probe }) &&
                  File.Exists(probe) && new FileInfo(probe).Length == 0, "색인 시작 문서의 자기 명령과 빈 파일: " + string.Join(" ", written[1].Arguments));
        }
        finally
        {
            // junction은 링크만 지우고(대상 유지) 나머지를 지웁니다.
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
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
            Write(pch, "#pragma once\n");
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

            // 공유 PCH 응답 파일만 있는 프로젝트 대상 폴더, 데이터베이스 생성 모드 폴더, 더 최근의 게임 빌드
            var sharedPch = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "GameEditor", "Development", "UnrealEd", "SharedPCH.UnrealEd.h.obj.rsp");
            Write(sharedPch, "\"x.h\"\n");
            var gcd = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "UnrealEditorGCD", "Development", "Game", "A.cpp.obj.rsp");
            Write(gcd, Rsp(Path.Combine(source, "A.cpp")));
            File.SetLastWriteTimeUtc(sharedPch, DateTime.UtcNow.AddMinutes(10));
            File.SetLastWriteTimeUtc(gcd, DateTime.UtcNow.AddMinutes(10));
            Check(UnrealCompileCommands.DetectVariant(project) is { Target: "UnrealEditor", Configuration: "Development" }, "가장 최근 일반 빌드 구성(공유 PCH·GCD 폴더 제외)");
            var game = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "UnrealGame", "Development", "Game", "A.cpp.obj.rsp");
            Write(game, Rsp(Path.Combine(source, "A.cpp")));
            File.SetLastWriteTimeUtc(game, DateTime.UtcNow.AddMinutes(5));
            Check(UnrealCompileCommands.DetectVariant(project) is { Target: "UnrealGame" }, "구성 정보가 없으면 가장 최근 빌드");
            var variant = UnrealCompileCommands.DetectVariant(project, "Development Editor");
            Check(variant is { Target: "UnrealEditor", Configuration: "Development" }, "활성 Solution 구성 우선: " + variant);
            Check(UnrealCompileCommands.DetectVariant(project, "DebugGame Editor") is { Target: "UnrealEditor", Configuration: "DebugGame" } &&
                  UnrealCompileCommands.DetectVariant(project, "Development") is { Target: "UnrealGame" } &&
                  UnrealCompileCommands.DetectVariant(project, "Shipping") is { Target: "UnrealGame" }, "구성 이름 대응과 없는 구성의 대체");
            var result = UnrealCompileCommands.Build(project, engine, variant!, "cl.exe");
            var files = result.Commands.Select(c => Path.GetFileName(c.File)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Check(files.SequenceEqual(new[] { "A.cpp", "B.cpp", "F.cpp" }), "파일별·unity·플러그인 TU: " + string.Join(",", files));
            Check(result.SkippedGenerated == 1 && result.MissingSources == 1 && result.UnityMembers == 4, "생성·누락 파일 집계");
            var a = result.Commands.Single(c => c.File.EndsWith("/A.cpp", StringComparison.Ordinal));
            Check(a.Directory == Path.Combine(engine, "Engine", "Source").Replace('\\', '/'), "작업 경로는 Engine/Source");
            Check(a.Arguments[0] == "cl.exe" && a.Arguments[1] == "--driver-mode=cl" && a.Arguments.Last() == a.File, "컴파일러·source 위치");
            Check(!a.Arguments.Any(x => x.StartsWith("/Yu") || x.StartsWith("/Fp") || x.StartsWith("/Fo") || x.StartsWith("/d2") || x.StartsWith("/errorReport") ||
                                         x == "/experimental:log" || x == "x.sarif" || x == "/sourceDependencies"), "PCH·출력·로그 옵션 제거");
            var include = Array.IndexOf(a.Arguments.ToArray(), definitions);
            var pchInclude = Array.IndexOf(a.Arguments.ToArray(), pch);
            // 공유 PCH에 기대는 프로젝트 소스가 흔하므로 실제 빌드처럼 텍스트로 포함합니다(빌드 순서대로 정의 헤더 앞).
            Check(!a.Arguments.Any(x => x.StartsWith("/FI")) && pchInclude >= 3 && a.Arguments[pchInclude - 2] == "-include" && pchInclude < include,
                "공유 PCH 헤더는 -Xclang -include로 유지");
            Check(include >= 3 && a.Arguments[include - 3] == "-Xclang" && a.Arguments[include - 2] == "-include" && a.Arguments[include - 1] == "-Xclang", "정의 헤더는 -Xclang -include");
            Check(a.Arguments.Contains("Runtime/Core/Public") && a.Arguments.Contains("/DWITH_EDITOR=1") && a.Arguments.Contains("/std:c++20"), "공유 응답 파일 펼침");

            // 응답 파일이 없는 프로젝트 소스: 같은 모듈의 명령을 그대로 쓰고, 이 구성에 빌드하지 않은 모듈은 근사 명령을 씁니다.
            Write(Path.Combine(source, "Game.Build.cs"), "");
            Write(Path.Combine(source, "Private", "D.cpp"), "int D;");
            var tools = Path.Combine(project, "Source", "Tools");
            Write(Path.Combine(tools, "Tools.Build.cs"), "");
            Write(Path.Combine(tools, "Private", "T.cpp"), "int T;");
            Write(Path.Combine(tools, "Private", "T.gen.cpp"), "");
            // 이 대상으로 컴파일하지 않는 소스는 보완하지 않습니다: 다른 플랫폼 폴더, ThirdParty, External 모듈. Windows 폴더는 남깁니다.
            Write(Path.Combine(source, "Private", "Linux", "L.cpp"), "int L;");
            Write(Path.Combine(source, "Private", "Windows", "W.cpp"), "int W;");
            Write(Path.Combine(source, "ThirdParty", "Lib", "X.cpp"), "int X;");
            var externalModule = Path.Combine(project, "Source", "ExtLib");
            Write(Path.Combine(externalModule, "ExtLib.Build.cs"), "public class ExtLib : ModuleRules { public ExtLib(ReadOnlyTargetRules t) : base(t) { Type = ModuleType.External; } }");
            Write(Path.Combine(externalModule, "Src", "E.cpp"), "int E;");
            var supplemented = UnrealCompileCommands.Build(project, engine, variant!, "cl.exe", default, Path.Combine(root, "modules"));
            var all = supplemented.Commands.Select(c => Path.GetFileName(c.File)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Check(all.SequenceEqual(new[] { "A.cpp", "B.cpp", "C.cpp", "D.cpp", "F.cpp", "T.cpp", "W.cpp" }) && supplemented.Supplemented == 4 &&
                  supplemented.UnreadableDirectories == 0,
                "응답 파일 없는 프로젝트 소스 보완(생성 파일·다른 플랫폼·ThirdParty·External 제외): " + string.Join(",", all) + " / " + supplemented.Supplemented);
            var d = supplemented.Commands.Single(c => c.File.EndsWith("/D.cpp", StringComparison.Ordinal));
            Check(d.Arguments.Take(d.Arguments.Count - 1).SequenceEqual(a.Arguments.Take(a.Arguments.Count - 1)) && d.Arguments.Last() == d.File,
                "같은 모듈 명령 재사용");
            var t = supplemented.Commands.Single(c => c.File.EndsWith("/T.cpp", StringComparison.Ordinal));
            Check(t.Arguments.Contains(Path.Combine(tools, "Private").Replace('\\', '/')) && File.Exists(Path.Combine(root, "modules", "Tools.h")),
                "빌드하지 않은 모듈은 근사 명령");
            Check(t.Arguments.Contains(pch), "같은 프로젝트 모듈 근사 명령은 공유 PCH 유지");
            Check(UnrealCompileCommands.Build(project, engine, variant!, "cl.exe").Supplemented == 3, "재정의 폴더가 없으면 같은 모듈 보완만");

            // 상위를 가리키는 junction은 따라가지 않습니다(같은 파일을 다른 경로로 거듭 보완하지 않음).
            var loop = Path.Combine(source, "Private", "Loop");
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{loop}\" \"{source}\"") { CreateNoWindow = true, UseShellExecute = false }))
            {
                mklink!.WaitForExit();
                Check(mklink.ExitCode == 0 && Directory.Exists(Path.Combine(loop, "Private")), "테스트 junction 생성");
            }

            try
            {
                var looped = UnrealCompileCommands.Build(project, engine, variant!, "cl.exe", default, Path.Combine(root, "modules"));
                Check(looped.Supplemented == 4 && looped.Commands.Count == supplemented.Commands.Count, "상위를 가리키는 junction은 따라가지 않음: " + looped.Supplemented);
            }
            finally
            {
                Directory.Delete(loop);
            }

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
            // 공유 PCH가 든 명령을 바탕으로: 엔진 cpp는 PCH를 빼고(include를 스스로 갖춤), 같은 프로젝트 모듈 근사는 남깁니다.
            var withPch = result.Commands.Where(c => c.Arguments.Contains(pch)).ToArray();
            var widget = Path.Combine(engineModule, "Private", "Widget.cpp");
            var engineApprox = UnrealCompileCommands.Synthesize(widget, withPch, Path.Combine(output, "modules"))!;
            var projectApprox = UnrealCompileCommands.Synthesize(widget, withPch, Path.Combine(output, "modules"), sharedPrecompiledHeader: true)!;
            Check(withPch.Length > 0 && !engineApprox.Arguments.Contains(pch) && engineApprox.Arguments.Contains(definitions) && projectApprox.Arguments.Contains(pch),
                "엔진 cpp 근사 명령은 공유 PCH 제외, 프로젝트 모듈 근사는 유지");
            File.Delete(pch);
            Check(!UnrealCompileCommands.Build(project, engine, variant!, "cl.exe").Commands.Single(c => c.File.EndsWith("/A.cpp", StringComparison.Ordinal))
                .Arguments.Contains(pch), "없는 PCH 헤더는 빼서 치명 오류를 피함");
            Check(UnrealCompileCommands.Synthesize(Path.Combine(root, "loose.cpp"), result.Commands, output) is null, "모듈 밖 파일은 근사하지 않음");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>UBT unity 묶음 구성과 캐시 폴더의 합성 TU·database 기록을 확인합니다.</summary>
    public static void RunUnityUnits()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.UnityUnits." + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = Path.Combine(root, "Engine Root");
            var project = Path.Combine(root, "Game");
            Write(Path.Combine(project, "Game.uproject"), "{}");
            var source = Path.Combine(project, "Source", "Game");
            Write(Path.Combine(source, "Game.Build.cs"), "");
            string Source(string name) => Path.Combine(source, name).Replace('\\', '/');
            foreach (var name in new[] { "U1.cpp", "U2.cpp", "U3.cpp", "Solo.cpp", "Adaptive.cpp" }) Write(Source(name), "int " + name.Replace(".cpp", "") + ";");
            var build = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "Development", "Game");
            string Rsp(string file, string flag) => $"\"{file.Replace('\\', '/')}\"\n/I \"Runtime/Core/Public\"\n{flag}\n/TP\n/std:c++20\n";
            string Include(string file) => $"#include \"{file}\"\n";
            var generated = Path.Combine(project, "Intermediate", "Build", "Win64", "UnrealEditor", "Inc", "Game", "UHT", "U1.gen.cpp").Replace('\\', '/');
            // 묶음 1: 생성 파일·따로 컴파일된 파일(Adaptive)·지운 파일(Gone)을 빼면 U1, U2. 묶음 2: 구성원이 하나뿐이라 파일별로 둡니다.
            var unity1 = Path.Combine(build, "Module.Game.1.cpp");
            Write(unity1, "// generated\n" + Include(generated) + Include(Source("U1.cpp")) + Include(Source("Adaptive.cpp")) + Include(Source("U2.cpp")) +
                          Include(Source("Gone.cpp")));
            Write(unity1 + ".obj.rsp", Rsp(unity1, "/DUNIT=1"));
            var unity2 = Path.Combine(build, "Module.Game.2.cpp");
            Write(unity2, Include(Source("U3.cpp")));
            Write(unity2 + ".obj.rsp", Rsp(unity2, "/DUNIT=2"));
            Write(Path.Combine(build, "Adaptive.cpp.obj.rsp"), Rsp(Source("Adaptive.cpp"), "/DADAPTIVE=1"));
            Write(Path.Combine(build, "Solo.cpp.obj.rsp"), Rsp(Source("Solo.cpp"), "/DSOLO=1"));

            var variant = UnrealCompileCommands.DetectVariant(project)!;
            var result = UnrealCompileCommands.Build(project, engine, variant, "clang-cl.exe");
            var names = result.Commands.Select(c => Path.GetFileName(c.File)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Check(names.SequenceEqual(new[] { "Adaptive.cpp", "Solo.cpp", "U1.cpp", "U2.cpp", "U3.cpp" }), "구성원도 파일별 명령: " + string.Join(",", names));
            var units = result.Units.OrderBy(u => u.Source, StringComparer.Ordinal).ToArray();
            Check(units.Length == 2 && units[0].Members.SequenceEqual(new[] { Source("U1.cpp"), Source("U2.cpp") }) &&
                  units[1].Members.SequenceEqual(new[] { Source("U3.cpp") }),
                "묶음 구성원(생성·따로 컴파일·없는 파일 제외, 포함 순서): " + string.Join(" | ", units.Select(u => string.Join(",", u.Members.Select(Path.GetFileName)))));
            var u1 = result.Commands.Single(c => c.File == Source("U1.cpp"));
            Check(units[0].Arguments.SequenceEqual(u1.Arguments.Take(u1.Arguments.Count - 1)) && units[0].Arguments.Contains("/DUNIT=1") &&
                  result.Commands.Single(c => c.File == Source("Adaptive.cpp")).Arguments.Contains("/DADAPTIVE=1"), "묶음 명령은 구성원 명령의 source 앞부분");

            var cache = Path.Combine(root, "cache");
            var context = CompileContextBuilder.Prepare(Path.Combine(project, "Game.sln"), cache, engine, "clang-cl.exe");
            var written = CompileCommandDatabase.Read(Path.Combine(context.Directory, CompileCommandDatabase.FileName));
            var unitFile = Path.Combine(context.Directory, "units", "Module.Game.1.cpp").Replace('\\', '/');
            var writtenNames = written.Select(c => Path.GetFileName(c.File)).ToArray();
            var plan = context.Plan!;
            // 묶이지 않은 파일도 같은 이름 다른 폴더와 겹치지 않게 경로 해시를 붙인 합성 TU로 색인합니다.
            Check(context.Kind == CompileContextKind.Unreal && context.Commands.Count == 5 && plan.GroupedUnitCount == 1 && plan.GroupedMemberCount == 2 &&
                  writtenNames.Length == 5 && writtenNames[0] == "Module.Game.1.cpp" && writtenNames[1].StartsWith("Adaptive-", StringComparison.Ordinal) &&
                  writtenNames[2].StartsWith("Solo-", StringComparison.Ordinal) && writtenNames[3].StartsWith("U3-", StringComparison.Ordinal) &&
                  writtenNames[4] == Path.GetFileName(context.IndexStartPath) &&
                  written.Take(4).All(c => Path.GetDirectoryName(c.File)!.Replace('\\', '/') == Path.GetDirectoryName(unitFile)!.Replace('\\', '/')),
                "database는 색인 단위 합성 TU + 색인 시작 문서: " + string.Join(",", writtenNames));
            Check(written[0].File == unitFile && written[0].Arguments.Last() == unitFile && written[0].Arguments.Contains("/DUNIT=1") &&
                  File.ReadAllText(unitFile).EndsWith(Include(Source("U1.cpp")) + Include(Source("U2.cpp")), StringComparison.Ordinal),
                "합성 TU는 캐시 폴더에 구성원만 포함: " + File.ReadAllText(unitFile));
            var soloUnit = written[2];
            Check(soloUnit.Arguments.Contains("/DSOLO=1") && File.ReadAllText(soloUnit.File).EndsWith(Include(Source("Solo.cpp")), StringComparison.Ordinal),
                "묶이지 않은 파일은 자기 명령으로 감쌈");
            Check(plan.IsMember(Path.Combine(source, "U1.cpp")) && plan.IsMember(Source("U2.cpp").ToUpperInvariant()) && plan.IsMember(Source("Solo.cpp")) &&
                  !plan.IsMember(Source("Gone.cpp")) && context.Summary.Contains("unity 묶음 1개로 2개 색인") && !context.Summary.Contains("PCH"),
                "색인 단위 구성원 판정과 요약: " + context.Summary);
            var u2Document = plan.DocumentCommand(Source("U2.cpp"));
            Check(u2Document is { WithoutPch: false } && u2Document.Command.File == Source("U2.cpp") && u2Document.Command.Arguments.Last() == Source("U2.cpp") &&
                  u2Document.Command.Arguments.Contains("/DUNIT=1"), "구성원 문서는 자기 명령");
            // 헤더는 소속 모듈의 같은 이름 cpp 명령을, 없으면 경로 순서로 첫 cpp 명령을 빌려 C++로 분석합니다.
            var header = Path.Combine(source, "Public", "U1.h");
            var other = Path.Combine(source, "Public", "Other.h");
            var headerDocument = plan.DocumentCommand(header)!.Command;
            var otherDocument = plan.DocumentCommand(other)!.Command;
            Check(headerDocument.File == header.Replace('\\', '/') && headerDocument.Arguments.Last() == headerDocument.File &&
                  headerDocument.Arguments.Contains("/DUNIT=1") && headerDocument.Arguments.Count(a => a == "/TP") == 1 &&
                  otherDocument.Arguments.Contains("/DADAPTIVE=1") && plan.DocumentCommand(Path.Combine(root, "Loose.h")) is null,
                "헤더는 소속 모듈 명령: " + string.Join(" ", headerDocument.Arguments));

            // 같은 구성이면 합성 TU를 다시 쓰지 않고(clangd가 묶음을 새로 색인하지 않게), 쓰지 않는 묶음 파일은 지웁니다.
            var stamp = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(unitFile, stamp);
            var stale = Path.Combine(context.Directory, "units", "Module.Old.1.cpp");
            Write(stale, "#include \"x.cpp\"\n");
            var again = CompileContextBuilder.Prepare(Path.Combine(project, "Game.sln"), cache, engine, "clang-cl.exe");
            Check(!again.Changed && File.GetLastWriteTimeUtc(unitFile) == stamp && !File.Exists(stale), "같은 구성은 그대로 두고 쓰지 않는 묶음은 지움");

            // 구성원이 하나로 줄면 묶지 않고 파일별로 돌아갑니다.
            File.Delete(Source("U2.cpp"));
            var single = CompileContextBuilder.Prepare(Path.Combine(project, "Game.sln"), cache, engine, "clang-cl.exe");
            var singleNames = CompileCommandDatabase.Read(Path.Combine(single.Directory, CompileCommandDatabase.FileName)).Select(c => Path.GetFileName(c.File)).ToArray();
            Check(single.Changed && single.Plan!.GroupedUnitCount == 0 && single.Plan.IsMember(Source("U1.cpp")) && !File.Exists(unitFile) &&
                  singleNames.Any(n => n.StartsWith("U1-", StringComparison.Ordinal)),
                "구성원이 하나뿐인 묶음은 파일별: " + string.Join(",", singleNames));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>실제 clangd로 unity 묶음 색인의 구성원 참조와, 구성원을 열 때 자기 명령으로 분석하는지 확인합니다.</summary>
    public static void RunUnityIntegration()
    {
        var clangd = FindClangd();
        if (clangd is null)
        {
            Console.WriteLine("SKIP: unity 묶음 색인 통합 시험은 VISUALBOOST_TEST_CLANGD 또는 VS의 C++ Clang 도구가 필요합니다.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.UnityIntegration." + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Path.Combine(root, "Game");
            Write(Path.Combine(project, "Game.uproject"), "{}");
            var source = Path.Combine(project, "Source", "Game");
            Write(Path.Combine(source, "Game.Build.cs"), "");
            Write(Path.Combine(source, "Calc.h"), "#pragma once\nint Twice(int Value);\n");
            var u1 = Path.Combine(source, "U1.cpp");
            Write(u1, "#include \"Calc.h\"\nint Twice(int Value) { return Value * 2; }\n");
            // 구성원 명령에만 있는 정의(GAME_UNIT)가 없으면 오류가 나도록 해, 열 때 자기 명령을 받는지 확인합니다.
            // 같은 폴더의 Solo.cpp 명령에는 이 정의가 없어 clangd가 명령을 추정하면 오류가 납니다.
            var u2 = Path.Combine(source, "U2.cpp");
            var u2Text = "#include \"Calc.h\"\n#if !GAME_UNIT\n#error member command missing\n#endif\nint UseTwice() { return Twice(3); }\n";
            Write(u2, u2Text);
            var solo = Path.Combine(source, "Solo.cpp");
            var soloText = "#include \"Calc.h\"\nint SoloUse() { return Twice(4); }\n";
            Write(solo, soloText);
            var build = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "Development", "Game");
            var unity = Path.Combine(build, "Module.Game.1.cpp");
            Write(unity, $"#include \"{u1.Replace('\\', '/')}\"\n#include \"{u2.Replace('\\', '/')}\"\n");
            Write(unity + ".obj.rsp", $"\"{unity.Replace('\\', '/')}\"\n/DGAME_UNIT=1\n/TP\n/std:c++17\n/c\n");
            Write(Path.Combine(build, "Solo.cpp.obj.rsp"), $"\"{solo.Replace('\\', '/')}\"\n/TP\n/std:c++17\n/c\n");

            using var navigator = ClangdNavigator.StartAsync(new ClangdNavigatorOptions
            {
                ClangdPath = clangd, CacheRoot = Path.Combine(root, "cache"), SolutionPath = Path.Combine(project, "Game.sln"),
                EngineRoot = Path.Combine(root, "Engine Root"), WorkerCount = 1
            }, CancellationToken.None).Result;
            Check(navigator.Context.Plan is { GroupedUnitCount: 1 } plan && plan.IsMember(u2) && navigator.Context.Commands.Count == 3, "묶음 색인 준비");
            Check(SpinUntil(() => navigator.Progress.Completed, 60000), "묶음 색인 완료");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var soloLine = soloText.Split('\n')[1];
            var references = navigator.ReferencesAsync(new NavigationQuery(new DocumentText(solo, soloText, 1), 1, soloLine.IndexOf("Twice", StringComparison.Ordinal)),
                timeout.Token).Result;
            Check(references.Locations.Any(l => l.Path == u1) && references.Locations.Any(l => l.Path == u2),
                "묶음으로 색인한 구성원의 정의·참조: " + string.Join(",", references.Locations.Select(l => Path.GetFileName(l.Path))));

            var u2Line = u2Text.Split('\n')[4];
            var definition = navigator.DefinitionAsync(new NavigationQuery(new DocumentText(u2, u2Text, 1), 4, u2Line.IndexOf("Twice(3", StringComparison.Ordinal)),
                null, timeout.Token).Result;
            Check(definition.Locations.Any(l => l.Path == u1) && navigator.ErrorsOf(u2) is null,
                "구성원을 열면 자기 명령으로 분석: " + string.Join(",", definition.Locations) + " / " + navigator.ErrorsOf(u2)?.FirstMessage);
            navigator.ShutdownAsync(TimeSpan.FromSeconds(10)).Wait();
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// 공유 PCH가 든 Unreal 테스트 프로젝트를 만듭니다. 묶음 1(U1, U2)은 U2가 PCH에 기대 PCH 없이 분석하면 실패하고,
    /// 묶음 2(U4, U5)는 앞 구성원 U4가 Shared.h를 포함해 묶음은 통과하지만 U5 혼자서는 PCH 없이 실패합니다. Solo는 스스로 포함을 갖춥니다.
    /// </summary>
    private static (string Project, string Source, string Build) WritePchProject(string root, string extraFlag = "")
    {
        var project = Path.Combine(root, "Game");
        Write(Path.Combine(project, "Game.uproject"), "{}");
        var source = Path.Combine(project, "Source", "Game");
        Write(Path.Combine(source, "Game.Build.cs"), "");
        string Unix(string path) => path.Replace('\\', '/');
        Write(Path.Combine(source, "Shared.h"), "#pragma once\nstruct FShared { int Value() const; };\n");
        Write(Path.Combine(source, "Calc.h"), "#pragma once\nint Twice(int Value);\n");
        Write(Path.Combine(source, "U1.cpp"), "#include \"Calc.h\"\nint Twice(int Value) { return Value * 2; }\n");
        Write(Path.Combine(source, "U2.cpp"), "#include \"Calc.h\"\nint UseShared(const FShared& S) { return S.Value() + Twice(1); }\n");
        Write(Path.Combine(source, "U4.cpp"), "#include \"Shared.h\"\nint FShared::Value() const { return 1; }\n");
        Write(Path.Combine(source, "U5.cpp"), "int UseAgain(const FShared& S) { return S.Value(); }\n");
        Write(Path.Combine(source, "Solo.cpp"), "#include \"Shared.h\"\nint SoloUse(const FShared& S) { return S.Value(); }\n");
        var build = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "Development", "Game");
        var pch = Unix(Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "GameEditor", "Development", "UnrealEd", "SharedPCH.UnrealEd.h"));
        Write(pch, $"#pragma once\n#include \"{Unix(Path.Combine(source, "Shared.h"))}\"\n");
        string Rsp(string file) => $"\"{Unix(file)}\"\n/I \"{Unix(source)}\"\n/FI\"{pch}\"\n/Yu\"{pch}\"\n{extraFlag}\n/TP\n/std:c++17\n/c\n";
        string Include(string name) => $"#include \"{Unix(Path.Combine(source, name))}\"\n";
        var unity1 = Path.Combine(build, "Module.Game.1.cpp");
        Write(unity1, Include("U1.cpp") + Include("U2.cpp"));
        Write(unity1 + ".obj.rsp", Rsp(unity1));
        var unity2 = Path.Combine(build, "Module.Game.2.cpp");
        Write(unity2, Include("U4.cpp") + Include("U5.cpp"));
        Write(unity2 + ".obj.rsp", Rsp(unity2));
        Write(Path.Combine(build, "Solo.cpp.obj.rsp"), Rsp(Path.Combine(source, "Solo.cpp")));
        return (project, source, build);
    }

    /// <summary>공유 PCH 사용 방식별 database, 실패 단위의 PCH 전환과 판단 기록, clangd 로그 줄 해석을 확인합니다.</summary>
    public static void RunUnrealPchPlan()
    {
        Check(ClangdSession.ParseIndexFailure("I[03:16:30.489] Failed to compile C:/a b/units/Module.G.1.cpp, index may be incomplete") == "C:/a b/units/Module.G.1.cpp" &&
              ClangdSession.ParseIndexFailure("I[03:16:30.489] Indexed C:/a.cpp (1 symbols, 2 refs, 3 files)") is null &&
              ClangdSession.ParseIndexFailure("E[03:16:30.489] something, index may be incomplete") is null, "색인 실패 줄 해석");
        Check(ClangdSession.ParseIndexedTranslationUnit("I[03:16:30.489] Indexed C:/Program Files (x86)/x.cpp (30902 symbols, 198217 refs, 498 files)") ==
              "C:/Program Files (x86)/x.cpp" &&
              ClangdSession.ParseIndexedTranslationUnit("I[03:16:29.278] Indexed c++14 standard library (incomplete due to errors): 11513 symbols, 9185 filtered") is null,
            "색인 완료 줄 해석(경로의 괄호 유지, 표준 라이브러리 요약 제외)");

        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.PchPlan." + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = Path.Combine(root, "Engine Root");
            var (project, source, build) = WritePchProject(root);
            File.AppendAllText(Path.Combine(source, "U2.cpp"), "#if WITH_GAME_FLAG\n#endif\n");
            var solution = Path.Combine(project, "Game.sln");
            var cache = Path.Combine(root, "cache");
            string Source(string name) => Path.Combine(source, name).Replace('\\', '/');
            IReadOnlyList<CompileCommand> Database(CompileContext context) => CompileCommandDatabase.Read(Path.Combine(context.Directory, CompileCommandDatabase.FileName));
            bool HasPch(IEnumerable<string> arguments) => arguments.Any(a => a.Contains("SharedPCH."));

            var auto = CompileContextBuilder.Prepare(solution, cache, engine, "clang-cl.exe");
            var plan = auto.Plan!;
            var units = Database(auto).Take(3).ToArray();
            Check(plan.Mode == UnrealPchMode.Auto && plan.SwitchableUnitCount == 3 && plan.PchUnitCount == 0 && units.All(c => !HasPch(c.Arguments)) &&
                  units.All(c => c.Arguments.Contains("/I")) && !auto.Summary.Contains("PCH"), "자동은 PCH 없이 색인: " + auto.Summary);
            var u1 = plan.DocumentCommand(Source("U1.cpp"))!;
            Check(u1.WithoutPch && !HasPch(u1.Command.Arguments) && HasPch(plan.DocumentCommand(Source("U1.cpp"), pch: true)!.Command.Arguments),
                "문서 명령도 PCH 없이, 요청하면 PCH 포함");

            var wrapper = units[0].File;
            Check(wrapper.EndsWith("/units/Module.Game.1.cpp", StringComparison.Ordinal) && plan.MarkNeedsPch(new[] { Source("Solo.cpp") }).Count == 0,
                "합성 TU가 아닌 경로는 무시");
            // PCH 없는 합성 TU는 구성원 조건식의 매크로가 정의되지 않으면 분석 오류를 내고, PCH 없이 여는 문서는 그 경고를 받습니다.
            Check(File.ReadAllText(wrapper).Contains("#if !defined(WITH_GAME_FLAG)\n#error ") && !File.ReadAllText(Database(auto)[1].File).Contains("#error") &&
                  u1.Command.Arguments.Contains("-Wundef") && !plan.DocumentCommand(Source("U1.cpp"), pch: true)!.Command.Arguments.Contains("-Wundef"),
                "조건식 매크로 확인(PCH 없는 단위·문서만)");
            var switched = plan.MarkNeedsPch(new[] { wrapper.Replace('/', '\\').ToUpperInvariant() });
            var pchWrapper = Path.Combine(auto.Directory, "units", "Module.Game.1.pch.cpp").Replace('\\', '/');
            var rewritten = Database(auto);
            Check(switched.Count == 1 && switched[0].File == pchWrapper && HasPch(switched[0].Arguments) && switched[0].Arguments.Last() == pchWrapper &&
                  File.Exists(pchWrapper) && !File.Exists(wrapper) && rewritten.Any(c => c.File == pchWrapper && HasPch(c.Arguments)) &&
                  rewritten.Count(c => HasPch(c.Arguments)) == 1 && File.Exists(Path.Combine(auto.Directory, "pch-units.json")) &&
                  !File.ReadAllText(pchWrapper).Contains("#error"),
                "실패한 단위만 새 경로의 PCH 합성 TU로(조건식 확인 없음): " + string.Join(",", rewritten.Select(c => Path.GetFileName(c.File))));
            // 문서는 단위 판단과 상관없이 PCH 없이 시작합니다(혼자 실패하면 진단을 보고 다시 분석).
            Check(plan.UnitUsesPch(Source("U2.cpp")) && plan.DocumentCommand(Source("U1.cpp"))!.WithoutPch && !plan.UnitUsesPch(Source("U4.cpp")) &&
                  plan.MarkNeedsPch(new[] { pchWrapper }).Count == 0 && plan.PchUnitCount == 1, "전환한 단위만 PCH, 문서는 PCH 없이 시작, 다시 알려도 그대로");

            // 판단은 다음 시작에도 남고, 구성(인자)이 바뀌면 버립니다.
            var restarted = CompileContextBuilder.Prepare(solution, cache, engine, "clang-cl.exe");
            Check(restarted.Plan!.PchUnitCount == 1 && Database(restarted).Any(c => c.File == pchWrapper) && restarted.Summary.Contains("공유 PCH 포함 단위 1개"),
                "판단 기록으로 다시 시작: " + restarted.Summary);
            var unity1Rsp = Path.Combine(build, "Module.Game.1.cpp.obj.rsp");
            File.WriteAllText(unity1Rsp, File.ReadAllText(unity1Rsp).Replace("/TP", "/DCHANGED=1\n/TP"));
            var changed = CompileContextBuilder.Prepare(solution, cache, engine, "clang-cl.exe");
            Check(changed.Plan!.PchUnitCount == 0 && File.Exists(wrapper) && !File.Exists(pchWrapper), "구성이 바뀌면 PCH 없이 다시 판단");

            var always = CompileContextBuilder.Prepare(solution, cache, engine, "clang-cl.exe", pchMode: UnrealPchMode.Always);
            Check(always.Plan!.PchUnitCount == 3 && Database(always).Take(3).All(c => HasPch(c.Arguments) && c.File.EndsWith(".pch.cpp", StringComparison.Ordinal)) &&
                  !always.Plan.DocumentCommand(Source("U5.cpp"))!.WithoutPch && always.Plan.MarkNeedsPch(new[] { wrapper }).Count == 0 && always.Summary.Contains("공유 PCH 포함"),
                "항상은 모든 단위에 PCH");
            var never = CompileContextBuilder.Prepare(solution, cache, engine, "clang-cl.exe", pchMode: UnrealPchMode.Never);
            var neverWrapper = Path.Combine(never.Directory, "units", "Module.Game.2.cpp");
            var neverText = File.ReadAllText(neverWrapper);
            never.Plan!.RecordFailure(neverWrapper);
            Check(never.Plan!.PchUnitCount == 0 && Database(never).All(c => !HasPch(c.Arguments)) && never.Plan.MarkNeedsPch(new[] { wrapper }).Count == 0,
                "넣지 않음은 실패해도 바꾸지 않음");
            // 넣지 않음으로 색인한 합성 TU는 내용을 달리해, 자동으로 바꾸면 clangd가 다시 색인하고 실패를 다시 알리게 합니다.
            var backToAuto = CompileContextBuilder.Prepare(solution, cache, engine, "clang-cl.exe");
            Check(neverText.Contains("공유 PCH 넣지 않음") && !File.ReadAllText(neverWrapper).Contains("공유 PCH 넣지 않음") && backToAuto.Plan!.PchUnitCount == 0,
                "넣지 않음 합성 TU 구분");

            // 판단은 실패를 받은 즉시 기록되어, 전환 전에 다시 시작해도 다음 세션이 PCH로 색인합니다(피드백 검토 34).
            var fresh = Path.Combine(root, "cache-record");
            var recorded = CompileContextBuilder.Prepare(solution, fresh, engine, "clang-cl.exe");
            var unit2 = Path.Combine(recorded.Directory, "units", "Module.Game.2.cpp");
            var solo = Database(recorded).First(c => Path.GetFileName(c.File).StartsWith("Solo-", StringComparison.Ordinal)).File;
            recorded.Plan!.RecordFailure(unit2);
            recorded.Plan.RecordFailure(solo);
            recorded.Plan.RecordFailure(Source("Solo.cpp"));
            Check(recorded.Plan.PchUnitCount == 0, "기록만으로는 이번 세션의 단위를 바꾸지 않음");
            var next = CompileContextBuilder.Prepare(solution, fresh, engine, "clang-cl.exe");
            var unit2Pch = Path.Combine(next.Directory, "units", "Module.Game.2.pch.cpp");
            Check(next.Plan!.PchUnitCount == 2 && next.Plan.UnitUsesPch(Source("U4.cpp")) && File.Exists(unit2Pch),
                "기록한 판단으로 다음 세션은 PCH 단위: " + next.Summary);
            // 파일 하나짜리 PCH 단위의 문서는 처음부터 PCH로, 묶음 구성원과 헤더는 PCH 없이 엽니다(피드백 검토 38).
            Check(!next.Plan.DocumentCommand(Source("Solo.cpp"))!.WithoutPch && next.Plan.DocumentCommand(Source("U5.cpp"))!.WithoutPch &&
                  next.Plan.DocumentCommand(Source("Shared.h"))!.WithoutPch, "파일 하나짜리 PCH 단위 문서만 PCH로 시작");

            // PCH를 넣고도 실패한 단위는 구성원이 그대로면 PCH를 유지하고, 바뀌면 PCH 없이 다시 판단합니다(피드백 검토 35).
            next.Plan.RecordFailure(unit2Pch);
            Check(CompileContextBuilder.Prepare(solution, fresh, engine, "clang-cl.exe").Plan!.UnitUsesPch(Source("U4.cpp")), "PCH로도 실패, 구성원 그대로면 유지");
            File.SetLastWriteTimeUtc(Source("U5.cpp"), DateTime.UtcNow.AddMinutes(1));
            var edited = CompileContextBuilder.Prepare(solution, fresh, engine, "clang-cl.exe");
            Check(!edited.Plan!.UnitUsesPch(Source("U4.cpp")) && edited.Plan.UnitUsesPch(Source("Solo.cpp")) &&
                  File.Exists(Path.Combine(edited.Directory, "units", "Module.Game.2.cpp")), "PCH로도 실패한 단위는 구성원이 바뀌면 다시 판단");
            Check(!CompileContextBuilder.Prepare(solution, fresh, engine, "clang-cl.exe").Plan!.UnitUsesPch(Source("U4.cpp")), "다시 준비해도 풀린 판단 유지");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// 실제 clangd로 자동 PCH를 확인합니다. PCH 없이 색인해 실패한 묶음은 PCH로 다시 색인되어 참조가 채워지고, 묶음은 통과하지만 혼자 열면
    /// 실패하는 구성원 문서는 PCH를 넣어 다시 분석해 정의를 찾습니다.
    /// </summary>
    public static void RunPchAutoIntegration()
    {
        var clangd = FindClangd();
        if (clangd is null)
        {
            Console.WriteLine("SKIP: 자동 PCH 통합 시험은 VISUALBOOST_TEST_CLANGD 또는 VS의 C++ Clang 도구가 필요합니다.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.PchAuto." + Guid.NewGuid().ToString("N"));
        try
        {
            var (project, source, _) = WritePchProject(root);
            // 공유 PCH가 정의하는 매크로로 감싼 헤더: PCH 없이 열면 오류 없이 그 구역이 비활성이 되므로 정의되지 않은 조건 매크로 경고로 PCH 전환합니다.
            File.AppendAllText(Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "GameEditor", "Development", "UnrealEd", "SharedPCH.UnrealEd.h"),
                "#define WITH_SHARED_FLAG 1\n");
            var flag = Path.Combine(source, "Flag.h");
            Write(flag, "#pragma once\n#include \"Calc.h\"\n#if WITH_SHARED_FLAG\ninline int FlagInline() { return Twice(7); }\n#endif\n");
            var options = new ClangdNavigatorOptions
            {
                ClangdPath = clangd, CacheRoot = Path.Combine(root, "cache"), SolutionPath = Path.Combine(project, "Game.sln"),
                EngineRoot = Path.Combine(root, "Engine Root"), WorkerCount = 1, PchSwitchDelay = TimeSpan.FromMilliseconds(200)
            };
            var first = ClangdNavigator.StartAsync(options, CancellationToken.None).Result;
            try
            {
                var units = Path.Combine(first.Context.Directory, "units");
                Check(SpinUntil(() => File.Exists(Path.Combine(units, "Module.Game.1.pch.cpp")), 60000), "실패한 묶음을 PCH로 전환");
                Check(File.Exists(Path.Combine(units, "Module.Game.2.cpp")) && !File.Exists(Path.Combine(units, "Module.Game.2.pch.cpp")) &&
                      first.Context.Plan!.PchUnitCount == 1, "통과한 묶음은 PCH 없이 유지");
                // clangd는 같은 내용의 파일을 다시 색인해도 메모리의 이전(오류) 결과를 그대로 쓰므로 다시 시작이 필요하다고 알립니다.
                Check(SpinUntil(() => first.NeedsReload, 60000) && first.IndexedUnits >= 4, "PCH 단위 색인 뒤 다시 시작 필요: 색인 " + first.IndexedUnits);
                first.ShutdownAsync(TimeSpan.FromSeconds(10)).Wait();
            }
            finally
            {
                first.Dispose();
            }

            using var navigator = ClangdNavigator.StartAsync(options, CancellationToken.None).Result;
            Check(navigator.Context.Plan!.PchUnitCount == 1 && SpinUntil(() => navigator.Progress.Completed, 30000), "판단 기록으로 다시 시작");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var solo = Path.Combine(source, "Solo.cpp");
            var soloText = File.ReadAllText(solo);
            var soloLine = soloText.Split('\n')[1];
            var query = new NavigationQuery(new DocumentText(solo, soloText, 1), 1, soloLine.LastIndexOf("Value", StringComparison.Ordinal));
            var u2 = Path.Combine(source, "U2.cpp");
            var references = navigator.ReferencesAsync(query, timeout.Token).Result;
            Check(references.Locations.Any(l => l.Path == u2) && !navigator.NeedsReload && navigator.IndexedUnits == 0,
                "다시 시작하면 PCH로 다시 색인한 구성원의 참조가 보이고 더 다시 시작하지 않음: " +
                string.Join(",", references.Locations.Select(l => Path.GetFileName(l.Path))));

            var u5 = Path.Combine(source, "U5.cpp");
            var u5Text = File.ReadAllText(u5);
            var reports = new List<string>();
            var definition = navigator.DefinitionAsync(new NavigationQuery(new DocumentText(u5, u5Text, 1), 0, u5Text.IndexOf("Value", StringComparison.Ordinal)),
                new CollectProgress(reports), timeout.Token).Result;
            Check(definition.Locations.Any(l => Path.GetFileName(l.Path) == "U4.cpp" || Path.GetFileName(l.Path) == "Shared.h") &&
                  SpinUntil(() => navigator.ErrorsOf(u5) is null, 10000) && reports.Contains("공유 PCH를 넣어 다시 분석하는 중…"),
                "혼자 실패하는 구성원 문서는 PCH로 다시 분석: " + string.Join(",", definition.Locations) + " / " + navigator.ErrorsOf(u5)?.FirstMessage + " / " +
                string.Join("|", reports));

            var flagText = File.ReadAllText(flag);
            var flagLine = flagText.Split('\n')[3];
            var flagReports = new List<string>();
            var twice = navigator.DefinitionAsync(new NavigationQuery(new DocumentText(flag, flagText, 1), 3, flagLine.IndexOf("Twice", StringComparison.Ordinal)),
                new CollectProgress(flagReports), timeout.Token).Result;
            Check(twice.Locations.Any(l => Path.GetFileName(l.Path) == "U1.cpp" || Path.GetFileName(l.Path) == "Calc.h") && flagReports.Contains("공유 PCH를 넣어 다시 분석하는 중…"),
                "PCH가 정의하는 조건 매크로에 기대는 문서는 PCH로 다시 분석: " + string.Join(",", twice.Locations) + " / " + string.Join("|", flagReports));
            navigator.ShutdownAsync(TimeSpan.FromSeconds(10)).Wait();
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>진행 문구를 호출 스레드에서 바로 모읍니다(<see cref="Progress{T}"/>는 다른 스레드로 넘겨 순서가 늦을 수 있음).</summary>
    private sealed class CollectProgress : IProgress<string>
    {
        private readonly List<string> reports;

        public CollectProgress(List<string> reports) => this.reports = reports;

        public void Report(string value)
        {
            lock (reports) reports.Add(value);
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

            // 색인 형식 번호가 없거나 다르면 이전 clangd 색인 파일을 지우고, 같으면 둡니다.
            var shard = Path.Combine(context.Directory, ".cache", "clangd", "index", "main.cpp.0123456789ABCDEF.idx");
            var stamp = Path.Combine(context.Directory, CompileContextBuilder.IndexFormatFileName);
            Write(shard, "x");
            CompileContextBuilder.Prepare(Path.Combine(cmake, "App.sln"), cache, null, "clang-cl.exe");
            Check(File.Exists(shard), "같은 색인 형식이면 색인 파일 유지");
            File.WriteAllText(stamp, "1");
            CompileContextBuilder.Prepare(Path.Combine(cmake, "App.sln"), cache, null, "clang-cl.exe");
            Check(!File.Exists(shard) && File.ReadAllText(stamp) == CompileContextBuilder.IndexFormat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "색인 형식이 바뀌면 이전 색인 파일 삭제");

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
            var bodies = "class A\n{\n    virtual void Pure() = 0;\n    A(const A&) = delete;\n    UFUNCTION(BlueprintImplementableEvent, Category = \"X\")\n" +
                         "    void OnHit();\n    UFUNCTION(\n        BlueprintNativeEvent)\n    void OnUse();\n    void Normal();\n    UFUNCTION(BlueprintCallable)\n" +
                         "    void Callable();\n};\n";
            Check(DefinitionCandidates.LooksLikeNoSourceBody(bodies, 2) && DefinitionCandidates.LooksLikeNoSourceBody(bodies, 3) &&
                  DefinitionCandidates.LooksLikeNoSourceBody(bodies, 5) && DefinitionCandidates.LooksLikeNoSourceBody(bodies, 8) &&
                  !DefinitionCandidates.LooksLikeNoSourceBody(bodies, 9) && !DefinitionCandidates.LooksLikeNoSourceBody(bodies, 11) &&
                  !DefinitionCandidates.LooksLikeNoSourceBody(bodies, 99), "소스에 본문이 없는 선언(순수 가상·삭제·생성 코드 이벤트) 판별");

            var engine = Path.Combine(root, "Engine", "Source", "Runtime", "CoreUObject");
            var header = Path.Combine(engine, "Public", "Package.h");
            var symbols = new[]
            {
                new SourceSymbolLocation("SavePackage", Path.Combine(root, "Elsewhere", "Other.cpp"), 1, 1, SourceSymbolKind.Function, "UOther"),
                new SourceSymbolLocation("SavePackage", Path.Combine(root, "Elsewhere", "Free.cpp"), 1, 1, SourceSymbolKind.Function),
                new SourceSymbolLocation("SavePackage", Path.Combine(engine, "Private", "SavePackage2.cpp"), 1, 1, SourceSymbolKind.Function, "UPackage"),
                new SourceSymbolLocation("SavePackage", header, 1, 1, SourceSymbolKind.Function, "UPackage"),
                new SourceSymbolLocation("SavePackage", Path.Combine(engine, "Private", "Var.cpp"), 1, 1, Analysis.SourceSymbolKind.Variable, "UPackage")
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
            // 프로젝트 함수의 정의 파일이 database에 없는 경우(모듈 밖 폴더): 색인되지 않으므로 요청 시점에 열어 확정해야 합니다.
            Write(Path.Combine(gameSource, "Calc.h"), "#pragma once\nint CalcTotal(int Value);\nint CalcOther(int Value);\nint CalcMissing(int Value);\n");
            var calcImpl = Path.Combine(project, "Source", "Shared", "CalcImpl.cpp");
            Write(calcImpl, "#include \"../Game/Calc.h\"\nint CalcTotal(int Value) { return Value + 1; }\n");
            var otherImpl = Path.Combine(project, "Source", "Shared", "OtherImpl.cpp");
            Write(otherImpl, "#include \"../Game/Calc.h\"\nint CalcOther(int Value) { return Value + 2; }\n");
            var caller = Path.Combine(gameSource, "Caller.cpp");
            var callerText = "#include \"Calc.h\"\nint Caller() { return CalcTotal(2) + CalcOther(3) + CalcMissing(4); }\n";
            Write(caller, callerText);
            var build = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "Development", "Game");
            foreach (var file in new[] { use, other, caller })
            {
                // 프로젝트 쪽 정의는 의존 모듈 API를 dllimport로 둡니다. 엔진 cpp 근사 명령은 이를 다시 비워야 합니다.
                Write(Path.Combine(build, Path.GetFileName(file) + ".obj.rsp"),
                    $"\"{file.Replace('\\', '/')}\"\n/I \"{Path.Combine(module, "Public").Replace('\\', '/')}\"\n/DMOD_API=__declspec(dllimport)\n/TP\n/std:c++17\n/c\n");
            }

            var cacheRoot = Path.Combine(root, "cache");
            using var navigator = ClangdNavigator.StartAsync(new ClangdNavigatorOptions
            {
                // 엔진 함수는 이름 인덱스가 비어 있는 상황: 소속 모듈 폴더의 같은 이름 cpp만으로 후보를 찾아야 합니다.
                // 프로젝트 함수는 이름 인덱스가 정의 파일을 압니다(파일 이름이 헤더와 달라 이름으로만 찾을 수 있음).
                ClangdPath = clangd, CacheRoot = cacheRoot, SolutionPath = Path.Combine(project, "Game.sln"), EngineRoot = engineRoot, WorkerCount = 1,
                FindSymbols = name => name switch
                {
                    "CalcTotal" => new[] { new SourceSymbolLocation("CalcTotal", calcImpl, 2, 5, SourceSymbolKind.Function, "") },
                    "CalcOther" => new[] { new SourceSymbolLocation("CalcOther", otherImpl, 2, 5, SourceSymbolKind.Function, "") },
                    // 정의가 어디에도 없는 함수: 이름 인덱스가 잘못 가리킨 후보도 이미 색인된 파일이면 열지 않아야 합니다.
                    "CalcMissing" => new[] { new SourceSymbolLocation("CalcMissing", caller, 2, 5, SourceSymbolKind.Function, "") },
                    _ => Array.Empty<SourceSymbolLocation>()
                }
            }, CancellationToken.None).Result;
            Check(navigator.Context.Kind == CompileContextKind.Unreal && navigator.Context.Commands.Count == 3, "응답 파일에서 프로젝트 명령 준비");
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
            Check(references.SymbolKind == SourceSymbolKind.Function, "참조 결과의 심볼 종류(정적 멤버 함수): " + references.SymbolKind);
            Check(ClangdSession.SymbolKindOf(23) == SourceSymbolKind.Struct && ClangdSession.SymbolKindOf(15) == SourceSymbolKind.Macro &&
                  ClangdSession.SymbolKindOf(8) == SourceSymbolKind.Variable && ClangdSession.SymbolKindOf(1) is null && ClangdSession.SymbolKindOf(null) is null,
                "LSP 심볼 종류 대응");
            Check(references.Locations.Any(l => l.Path == other && l.Container == "Other") && references.Locations.Any(l => l.Path == use && l.Container == "Use"),
                "참조마다 포함 함수 이름: " + string.Join(",", references.Locations.Select(l => Path.GetFileName(l.Path) + "=" + l.Container)));
            // 헤더의 선언은 선언, 요청 시점에 확정한 엔진 정의가 결과에 있으면 정의이고, 프로젝트의 호출에는 역할이 없습니다.
            var roles = string.Join(",", references.Locations.Select(l => Path.GetFileName(l.Path) + ":" + (l.Line + 1) + "=" + references.RoleOf(l)));
            Check(references.Locations.Where(l => l.Path == header).Any(l => references.RoleOf(l) == NavigationRole.Declaration) &&
                  references.Locations.Where(l => l.Path == use || l.Path == other).All(l => references.RoleOf(l) == NavigationRole.None) &&
                  references.Locations.Where(l => l.Path == engineSource).All(l => references.RoleOf(l) == NavigationRole.Definition), "참조 위치의 역할: " + roles);

            // 편집기에서 열지 않은 파일을 저장한 경우: 저장 내용으로 잠시 열어 색인에 반영합니다.
            var otherText = "#include \"Mod.h\"\nint Other() { return FMod::Compute(4); }\nint Again() { return FMod::Compute(5); }\n";
            File.WriteAllText(other, otherText, new UTF8Encoding(false));
            navigator.Saved(new DocumentText(other, otherText, 1));
            Check(SpinUntil(() => navigator.ReferencesAsync(query, timeout.Token).Result.Locations.Count(l => l.Path == other) == 2, 30000),
                "저장한 파일의 새 참조 반영");
            Check(SpinUntil(() => !navigator.IsOpen(other), 10000), "저장 반영 뒤 문서 닫기");

            // 편집기 밖에서 바뀐 파일: 디스크 내용으로 다시 분석합니다.
            File.WriteAllText(other, otherText + "int Third() { return FMod::Compute(6); }\n", new UTF8Encoding(false));
            navigator.Reload(other);
            Check(SpinUntil(() => navigator.ReferencesAsync(query, timeout.Token).Result.Locations.Count(l => l.Path == other) == 3, 30000),
                "편집기 밖 변경의 새 참조 반영");

            // 미저장 편집: 맨 위에 빈 줄을 넣으면 같은 호출이 한 줄 아래에서 찾아져야 합니다.
            var edited = "\n" + useText;
            var moved = navigator.DefinitionAsync(new NavigationQuery(new DocumentText(use, edited, 2), 2, position), null, timeout.Token).Result;
            Check(moved.Locations.Any(l => l.Path == engineSource), "미저장 편집 위치로 정의 요청");
            Check(File.ReadAllText(use) == useText && File.ReadAllText(engineSource).Contains("Value * 2"), "원본 파일 보존");
            Check(!Directory.EnumerateFiles(project, "compile_commands.json", SearchOption.AllDirectories).Any(), "프로젝트 폴더에 database를 쓰지 않음");

            // 색인에 없는 프로젝트 정의: 정의 이동은 후보 cpp를 열어 확정하고, 참조는 확정한 뒤 다시 찾아 정의를 더합니다.
            var callerLine = callerText.Split('\n')[1];
            var total = navigator.DefinitionAsync(new NavigationQuery(new DocumentText(caller, callerText, 1), 1, callerLine.IndexOf("CalcTotal", StringComparison.Ordinal)),
                null, timeout.Token).Result;
            Check(total.ResolvedOnDemand && total.Locations.Single().Path == calcImpl, "색인에 없는 프로젝트 정의 파일을 요청 시점에 확정: " +
                  string.Join(",", total.Locations));
            var otherReferences = navigator.ReferencesAsync(new NavigationQuery(new DocumentText(caller, callerText, 1), 1,
                callerLine.IndexOf("CalcOther", StringComparison.Ordinal)), timeout.Token).Result;
            Check(otherReferences.ResolvedOnDemand && otherReferences.Locations.Any(l => l.Path == otherImpl) && otherReferences.Locations.Any(l => l.Path == caller),
                "참조에서 색인에 없는 정의 파일을 확정해 더함: " + string.Join(",", otherReferences.Locations.Select(l => Path.GetFileName(l.Path))));
            Check(SpinUntil(() => navigator.Progress.Completed, 60000), "보완 파일 색인 뒤 색인 완료");
            var missingReports = new List<string>();
            var missing = navigator.DefinitionAsync(new NavigationQuery(new DocumentText(caller, callerText, 1), 1,
                callerLine.IndexOf("CalcMissing", StringComparison.Ordinal)), new SyncProgress(missingReports.Add), timeout.Token).Result;
            Check(!missing.ResolvedOnDemand && missing.Locations.Single().Path.EndsWith("Calc.h", StringComparison.OrdinalIgnoreCase) && missingReports.Count == 0,
                "색인을 마친 명령 있는 후보는 다시 열지 않음: " + string.Join(",", missing.Locations) + " / " + string.Join(",", missingReports));
            Check(!navigator.IsBusy && navigator.LastRequestUtc >= navigator.StartedUtc, "요청이 끝나면 유휴 상태와 마지막 요청 시각");

            // 분석 오류가 있는 문서: 결과가 비면 명령이 첫 오류를 함께 알리도록 오류 요약을 남깁니다.
            Check(navigator.ErrorsOf(caller) is null, "오류 없는 문서는 오류 요약 없음");
            var brokenText = "#include \"Mod.h\"\nint Use() { return Missing(3); }\n";
            var broken = navigator.DefinitionAsync(new NavigationQuery(new DocumentText(use, brokenText, 3), 1,
                brokenText.Split('\n')[1].IndexOf("Missing", StringComparison.Ordinal)), null, timeout.Token).Result;
            var useErrors = default(DocumentErrors);
            Check(broken.Locations.Count == 0 && SpinUntil(() => (useErrors = navigator.ErrorsOf(use)) is not null, 10000) &&
                  useErrors!.Count >= 1 && useErrors.FirstLine == 1 && useErrors.FirstMessage.Contains("Missing"),
                "분석 오류 요약(첫 오류 줄·메시지): " + useErrors?.FirstLine + " " + useErrors?.FirstMessage);
            navigator.ShutdownAsync(TimeSpan.FromSeconds(10)).Wait();
            Check(navigator.HasExited, "정상 종료");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// 가상 함수 참조는 찾은 함수만 남는지 실제 clangd로 확인합니다. clangd는 재정의 함수를 찾으면 기반 함수의 참조를, 기반 함수를 찾으면
    /// 재정의 함수의 선언·정의를 더해 돌려주므로 요청 문서는 symbolInfo로, 다른 파일은 색인 파일로 걸러야 합니다.
    /// </summary>
    public static void RunVirtualReferencesIntegration()
    {
        var clangd = FindClangd();
        if (clangd is null)
        {
            Console.WriteLine("SKIP: 가상 함수 참조 통합 시험은 VISUALBOOST_TEST_CLANGD 또는 VS의 C++ Clang 도구가 필요합니다.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.Virtual." + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Path.Combine(root, "Game");
            Write(Path.Combine(project, "Game.uproject"), "{}");
            var source = Path.Combine(project, "Source", "Game");
            Write(Path.Combine(source, "Game.Build.cs"), "");
            var headerLines = new[]
            {
                "#pragma once", "struct FBase", "{", "    virtual ~FBase() {}", "    virtual int Tick(int Value);", "};",
                "struct FDerived : FBase", "{", "    int Tick(int Value) override;", "};",
                "namespace ns { template <class T> struct TBox { static int Value; }; }"
            };
            var bodyLines = new[] { "#include \"Base.h\"", "int FBase::Tick(int Value) { return Value; }", "int FDerived::Tick(int Value) { return FBase::Tick(Value) + 1; }" };
            var callerLines = new[]
            {
                "#include \"Base.h\"", "int CallBase(FBase& B) { return B.Tick(1); }", "int CallDerived(FDerived& D) { return D.Tick(2); }",
                "int UseBox() { return ns::TBox<int>::Value + int(sizeof(ns::TBox<char>)); }"
            };
            var header = Path.Combine(source, "Base.h");
            var body = Path.Combine(source, "Base.cpp");
            var caller = Path.Combine(source, "Caller.cpp");
            Write(header, string.Join("\n", headerLines) + "\n");
            Write(body, string.Join("\n", bodyLines) + "\n");
            var callerText = string.Join("\n", callerLines) + "\n";
            Write(caller, callerText);
            var build = Path.Combine(project, "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "Development", "Game");
            foreach (var file in new[] { body, caller })
            {
                Write(Path.Combine(build, Path.GetFileName(file) + ".obj.rsp"), $"\"{file.Replace('\\', '/')}\"\n/I \"{source.Replace('\\', '/')}\"\n/TP\n/std:c++17\n/c\n");
            }

            using var navigator = ClangdNavigator.StartAsync(new ClangdNavigatorOptions
            {
                ClangdPath = clangd, CacheRoot = Path.Combine(root, "cache"), SolutionPath = Path.Combine(project, "Game.sln"),
                EngineRoot = Path.Combine(root, "Engine Root"), WorkerCount = 1
            }, CancellationToken.None).Result;
            Check(SpinUntil(() => navigator.Progress.Completed, 60000), "색인 완료");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            string Key(string file, int line, int character) => file + ":" + line + ":" + character;
            string[] Found(int line) => navigator.ReferencesAsync(new NavigationQuery(new DocumentText(caller, callerText, 1), line, callerLines[line].IndexOf(".Tick", StringComparison.Ordinal) + 1),
                timeout.Token).Result.Locations.Select(l => Key(Path.GetFileName(l.Path), l.Line, l.Character)).OrderBy(k => k, StringComparer.Ordinal).ToArray();

            var derived = Found(2);
            var expectedDerived = new[]
            {
                Key("Base.h", 8, headerLines[8].IndexOf("Tick", StringComparison.Ordinal)), Key("Base.cpp", 2, bodyLines[2].IndexOf("Tick", StringComparison.Ordinal)),
                Key("Caller.cpp", 2, callerLines[2].IndexOf("Tick", StringComparison.Ordinal))
            }.OrderBy(k => k, StringComparer.Ordinal);
            Check(derived.SequenceEqual(expectedDerived), "재정의 함수 참조에 기반 함수 위치 없음: " + string.Join(",", derived));
            var baseReferences = Found(1);
            var expectedBase = new[]
            {
                Key("Base.h", 4, headerLines[4].IndexOf("Tick", StringComparison.Ordinal)), Key("Base.cpp", 1, bodyLines[1].IndexOf("Tick", StringComparison.Ordinal)),
                Key("Base.cpp", 2, bodyLines[2].LastIndexOf("Tick", StringComparison.Ordinal)), Key("Caller.cpp", 1, callerLines[1].IndexOf("Tick", StringComparison.Ordinal))
            }.OrderBy(k => k, StringComparer.Ordinal);
            Check(baseReferences.SequenceEqual(expectedBase), "기반 함수 참조에 재정의 함수 선언·정의 없음: " + string.Join(",", baseReferences));

            // 템플릿 인수가 붙은 이름 앞의 한정자는 clang 색인이 빠뜨리므로 요청 문서에서 AST로 확인해 더합니다.
            var namespaceReferences = navigator.ReferencesAsync(new NavigationQuery(new DocumentText(caller, callerText, 1), 3, callerLines[3].IndexOf("ns::", StringComparison.Ordinal)),
                timeout.Token).Result.Locations.Where(l => l.Path == caller).Select(l => (l.Line, l.Character)).ToArray();
            Check(namespaceReferences.Contains((3, callerLines[3].IndexOf("ns::", StringComparison.Ordinal))) &&
                  namespaceReferences.Contains((3, callerLines[3].LastIndexOf("ns::", StringComparison.Ordinal))),
                "템플릿 앞 한정자 보완: " + string.Join(",", namespaceReferences));
            navigator.ShutdownAsync(TimeSpan.FromSeconds(10)).Wait();
        }
        finally
        {
            TryDelete(root);
        }
    }

    public static void RunMsBuildCommands()
    {
        var lines = new[]
        {
            "GetProjectDirectories\tC:\\p\\App.vcxproj\t\t\tC:\\VC\\include;;C:\\SDK\\ucrt;relative\tC:\\VC\\include\tdummy",
            "GetClCommandLines\tC:\\p\\App.vcxproj\tC:\\p\tC:\\p\\Main.cpp;Extra.cpp\t\t\t" +
                "/c /Iinc /D APP=3 /D \"B=a b\" /EHsc /std:c++20 /Yu\"pch.h\" /Fp\"x64\\Debug\\App.pch\" /Fo\"x64\\Debug\\\\\" /FI\"pch.h\" /FI forced.h /clr /errorReport:queue /TP",
            "GetClCommandLines\tC:\\p\\App.vcxproj\tC:\\p\tC:\\VC\\modules\\std.ixx\t\t\t/scanModules /c /interface /ifcOutput \"x64\\\\\" /TP",
            "GetClCommandLines\tC:\\p\\Other.vcxproj\tC:\\q\tC:\\p\\Main.cpp;C:\\q\\Q.c\t\t\t/c /TC",
            "broken line"
        };
        var commands = MsBuildCompileCommands.Parse(lines, "clang-cl.exe", out var answered);
        Check(commands.Select(c => c.File).SequenceEqual(new[] { "C:/p/Extra.cpp", "C:/p/Main.cpp", "C:/q/Q.c" }), "소스만, 상대 경로 해석, 중복 파일은 처음 것: " +
            string.Join(",", commands.Select(c => c.File)));
        Check(answered == 2, "명령을 준 프로젝트 수");
        var main = commands.Single(c => c.File == "C:/p/Main.cpp");
        Check(main.Directory == "C:/p" && main.Arguments[0] == "clang-cl.exe" && main.Arguments[1] == "--driver-mode=cl" && main.Arguments.Last() == main.File,
            "작업 폴더·컴파일러·source 위치");
        var args = main.Arguments.ToList();
        Check(args.Contains("/Iinc") && args.Contains("APP=3") && args.Contains("B=a b") && args.Contains("/std:c++20"), "일반 옵션 유지: " + string.Join(" ", args));
        Check(!args.Any(a => a.StartsWith("/Yu") || a.StartsWith("/Fp") || a.StartsWith("/Fo") || a.StartsWith("/clr") || a.StartsWith("/errorReport")),
            "PCH·출력·C++/CLI 옵션 제거");
        Check(string.Join(" ", args).Contains("-Xclang -include -Xclang pch.h -Xclang -include -Xclang forced.h"), "강제 include는 PCH 헤더도 유지");
        var imsvc = args.Select((a, i) => (a, i)).Where(x => x.a == "/imsvc").Select(x => args[x.i + 1]).ToArray();
        Check(imsvc.SequenceEqual(new[] { "C:/VC/include", "C:/SDK/ucrt" }), "시스템 include는 절대 경로만 중복 없이: " + string.Join(",", imsvc));
        Check(commands.Single(c => c.File == "C:/q/Q.c").Arguments.All(a => a != "/imsvc"), "디렉터리 정보 없는 프로젝트");

        Check(MsBuildCompileCommands.Escape("C:\\a;b\\$x@y%z'*?.vcxproj") == "C:\\a%3Bb\\%24x%40y%25z%27%2A%3F.vcxproj", "MSBuild 특수 문자 이스케이프");
        var wrapper = MsBuildCompileCommands.CreateWrapper("C:\\s & t\\Game.sln", new[] { new MsBuildProjectConfiguration("C:\\s & t\\A;B.vcxproj", "Debug", "Win32") });
        Check(wrapper.Contains("Include=\"C:\\s &amp; t\\A%3BB.vcxproj\"") && wrapper.Contains("SolutionDir=C:\\s &amp; t\\;") &&
              wrapper.Contains("Configuration=Debug;Platform=Win32;DesignTimeBuild=true"), "래퍼 프로젝트 속성·XML 이스케이프");
    }

    public static void RunMsBuildIntegration()
    {
        var clangd = FindClangd();
        var msbuild = FindMsBuild(clangd);
        if (clangd is null || msbuild is null)
        {
            Console.WriteLine("SKIP: MSBuild 명령 통합 시험은 VS의 MSBuild·C++ 도구와 clangd가 필요합니다(VISUALBOOST_TEST_MSBUILD로 지정 가능).");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.MsBuild 한글." + Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(Path.GetTempPath(), "VisualBoost.MsBuildCache." + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Path.Combine(root, "App Dir");
            Write(Path.Combine(project, "inc", "Lib.h"), "#pragma once\nint Lib(int v);\n");
            Write(Path.Combine(project, "pch.h"), "#pragma once\n#include <vector>\n");
            Write(Path.Combine(project, "forced.h"), "#pragma once\nint Forced();\n");
            Write(Path.Combine(project, "pch.cpp"), "#include \"pch.h\"\n");
            var mainText = "#include \"pch.h\"\n#include \"Lib.h\"\n#include \"forced.h\"\nint main() { std::vector<int> v{1}; return Lib(APP_VALUE) + Forced() + (int)v.size(); }\n";
            var main = Path.Combine(project, "Main.cpp");
            Write(main, mainText);
            var lib = Path.Combine(project, "Lib.cpp");
            Write(lib, "#include \"pch.h\"\n#include \"Lib.h\"\n#ifdef LIB_ONLY\nint Lib(int v) { return v; }\n#endif\nint Forced() { return 2; }\n");
            var extra = Path.Combine(project, "Extra.cpp");
            Write(extra, "#include \"pch.h\"\nint Extra() { return Forced(); }\n");
            // $(SolutionDir)을 쓰는 include 경로는 Solution 속성을 넘겨야 맞습니다.
            Write(Path.Combine(root, "shared", "Shared.h"), "#pragma once\nint Shared();\n");
            var vcxproj = Path.Combine(project, "App.vcxproj");
            Write(vcxproj, string.Join("\n",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">",
                "  <ItemGroup Label=\"ProjectConfigurations\">",
                "    <ProjectConfiguration Include=\"Debug|x64\"><Configuration>Debug</Configuration><Platform>x64</Platform></ProjectConfiguration>",
                "  </ItemGroup>",
                "  <PropertyGroup Label=\"Globals\"><ProjectGuid>{0B6C9F4E-2C7A-4E1B-9D35-6A1E2F3B4C51}</ProjectGuid></PropertyGroup>",
                "  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.Default.props\" />",
                "  <PropertyGroup Label=\"Configuration\">",
                "    <ConfigurationType>Application</ConfigurationType>",
                "    <PlatformToolset>$(DefaultPlatformToolset)</PlatformToolset>",
                "  </PropertyGroup>",
                "  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.props\" />",
                "  <ItemDefinitionGroup>",
                "    <ClCompile>",
                "      <AdditionalIncludeDirectories>inc;$(SolutionDir)shared;%(AdditionalIncludeDirectories)</AdditionalIncludeDirectories>",
                "      <PreprocessorDefinitions>APP_VALUE=3;%(PreprocessorDefinitions)</PreprocessorDefinitions>",
                "      <LanguageStandard>stdcpp17</LanguageStandard>",
                "      <PrecompiledHeader>Use</PrecompiledHeader>",
                "      <PrecompiledHeaderFile>pch.h</PrecompiledHeaderFile>",
                "    </ClCompile>",
                "  </ItemDefinitionGroup>",
                "  <ItemGroup>",
                "    <ClCompile Include=\"pch.cpp\"><PrecompiledHeader>Create</PrecompiledHeader></ClCompile>",
                "    <ClCompile Include=\"Main.cpp\" />",
                "    <ClCompile Include=\"Lib.cpp\"><PreprocessorDefinitions>LIB_ONLY;%(PreprocessorDefinitions)</PreprocessorDefinitions></ClCompile>",
                "    <ClCompile Include=\"Extra.cpp\"><ForcedIncludeFiles>forced.h</ForcedIncludeFiles></ClCompile>",
                "  </ItemGroup>",
                "  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.targets\" />",
                "</Project>",
                ""));
            var solution = Path.Combine(root, "App.sln");
            var projects = new[] { new MsBuildProjectConfiguration(vcxproj, "Debug", "x64"), new MsBuildProjectConfiguration(Path.Combine(root, "Missing.vcxproj"), "Debug", "x64") };
            var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToArray();

            var stopwatch = Stopwatch.StartNew();
            var context = CompileContextBuilder.Prepare(solution, cacheRoot, null, "clang-cl.exe", CancellationToken.None,
                new CompileCommandSources { MsBuildPath = msbuild, Projects = projects });
            Console.WriteLine($"  MSBuild 설계 시점 명령 {stopwatch.ElapsedMilliseconds}ms: {context.Summary}");
            Check(context.Kind == CompileContextKind.MsBuild && context.Commands.Count == 4, "설계 시점 명령 4개: " + context.Kind + " " + context.Reason);
            var libCommand = context.Commands.Single(c => c.File.EndsWith("/Lib.cpp", StringComparison.Ordinal));
            Check(libCommand.Arguments.Contains("LIB_ONLY") && libCommand.Arguments.Any(a => a.Replace('\\', '/').EndsWith("/shared", StringComparison.OrdinalIgnoreCase)),
                "파일별 정의와 $(SolutionDir) 경로: " + string.Join(" ", libCommand.Arguments));
            Check(libCommand.Arguments.Contains("/imsvc"), "시스템 include 추가");
            Check(context.Summary.Contains("1/2"), "없는 프로젝트는 건너뜀: " + context.Summary);
            Check(Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(before),
                "프로젝트 폴더에 아무것도 쓰지 않음");

            using var navigator = ClangdNavigator.StartAsync(new ClangdNavigatorOptions
            {
                ClangdPath = clangd, CacheRoot = cacheRoot, SolutionPath = solution, WorkerCount = 1,
                Sources = new CompileCommandSources { MsBuildPath = msbuild, Projects = projects }
            }, CancellationToken.None).Result;
            Check(navigator.Context.Kind == CompileContextKind.MsBuild, "탐색기가 MSBuild 명령으로 시작");
            Check(SpinUntil(() => navigator.Progress.Completed, 60000), "프로젝트 색인 완료");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var line = mainText.Split('\n')[3];
            var definition = navigator.DefinitionAsync(new NavigationQuery(new DocumentText(main, mainText, 1), 3, line.IndexOf("Lib(", StringComparison.Ordinal)), null, timeout.Token).Result;
            Check(definition.Locations.Any(l => l.Path == lib && l.Line == 3), "파일별 정의로만 보이는 정의: " + string.Join(",", definition.Locations));
            var references = navigator.ReferencesAsync(new NavigationQuery(new DocumentText(main, mainText, 1), 3, line.IndexOf("Forced(", StringComparison.Ordinal)), timeout.Token).Result;
            Check(references.Locations.Any(l => l.Path == extra), "강제 include로만 선언되는 파일의 참조: " + string.Join(",", references.Locations));
            navigator.ShutdownAsync(TimeSpan.FromSeconds(10)).Wait();
        }
        finally
        {
            TryDelete(cacheRoot);
            TryDelete(root);
        }
    }

    private static string? FindMsBuild(string? clangd)
    {
        var configured = Environment.GetEnvironmentVariable("VISUALBOOST_TEST_MSBUILD");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        // clangd와 같은 VS 설치(VC/Tools/Llvm/x64/bin의 여섯 단계 위)를 우선합니다.
        var install = clangd;
        for (var i = 0; i < 6 && install is not null; i++) install = Path.GetDirectoryName(install);
        return install is null ? null : MsBuildCompileCommands.FindMsBuild(install);
    }

    public static void RunNinjaCommands()
    {
        var entries = JsonValue.Parse("[" +
            "{\"directory\":\"C:\\\\w\\\\out\\\\build\\\\x64-Debug\",\"command\":\"C:\\\\PROGRA~1\\\\VC\\\\bin\\\\cl.exe  /nologo /TP -DX=1 /DWIN32 -MDd /showIncludes /FoCMakeFiles\\\\a.cpp.obj /FdCMakeFiles\\\\ /FS -c \\\"C:\\\\w\\\\src\\\\a b.cpp\\\"\",\"file\":\"C:\\\\w\\\\src\\\\a b.cpp\",\"output\":\"a.obj\"}," +
            "{\"directory\":\"C:\\\\w\\\\out\\\\build\\\\x64-Debug\",\"command\":\"C:\\\\PROGRA~1\\\\VC\\\\bin\\\\cl.exe /nologo -c C:\\\\w\\\\src\\\\a b.cpp\",\"file\":\"C:\\\\w\\\\src\\\\a b.cpp\"}," +
            "{\"directory\":\"C:\\\\w\\\\out\\\\build\\\\x64-Debug\",\"command\":\"cmd.exe /C link.exe a.obj\",\"file\":\"a.cpp.obj\"}," +
            "{\"directory\":\"C:\\\\w\\\\out\\\\build\\\\x64-Debug\",\"command\":\"\",\"file\":\"edit_cache.util\"}," +
            "{\"directory\":\"/w/b\",\"arguments\":[\"/usr/bin/clang++\",\"-DY=2\",\"-c\",\"../src/c.cpp\"],\"file\":\"../src/c.cpp\"}" +
            "]");
        var commands = NinjaCompileCommands.Convert(entries, "clang-cl.exe");
        Check(commands.Count == 2, "소스 컴파일만, 같은 파일은 처음 것: " + string.Join(",", commands.Select(c => c.File)));
        var a = commands.Single(c => c.File.EndsWith("a b.cpp", StringComparison.Ordinal));
        Check(a.Directory == "C:/w/out/build/x64-Debug" && a.Arguments[0] == "clang-cl.exe" && a.Arguments[1] == "--driver-mode=cl", "cl은 clang-cl 드라이버로");
        Check(a.Arguments.Contains("-DX=1") && a.Arguments.Contains("-MDd") && a.Arguments.Contains("/FS") &&
              !a.Arguments.Any(x => x.StartsWith("/showIncludes") || x.StartsWith("/Fo") || x.StartsWith("/Fd")), "출력·의존성 옵션 제거: " + string.Join(" ", a.Arguments));
        var c = commands.Single(x => x.File.EndsWith("c.cpp", StringComparison.Ordinal));
        Check(c.Arguments[0] == "/usr/bin/clang++" && c.Arguments.Contains("-DY=2"), "gcc 형식 드라이버는 그대로");

        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.Ninja 한글." + Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(Path.GetTempPath(), "VisualBoost.NinjaCache." + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = Path.Combine(root, "Folder Space");
            Write(Path.Combine(workspace, "src", "a.cpp"), "int A() { return X; }\n");
            Write(Path.Combine(workspace, "src", "b.cpp"), "int B() { return Y; }\n");
            Check(CompileContextBuilder.WorkspaceDirectory(workspace) == workspace && CompileContextBuilder.WorkspaceDirectory(workspace + "\\") == workspace &&
                  CompileContextBuilder.WorkspaceDirectory(Path.Combine(workspace, "App.sln")) == workspace, "폴더 작업 영역과 Solution 파일의 루트");
            var release = Path.Combine(workspace, "out", "build", "x64-Release");
            Write(Path.Combine(release, "build.ninja"), "rule cxx\n  command = cl.exe /DREL -c $in\nbuild a.obj: cxx ../../../src/a.cpp\n");
            File.SetLastWriteTimeUtc(Path.Combine(release, "build.ninja"), DateTime.UtcNow.AddHours(-1));
            var debug = Path.Combine(workspace, "out", "build", "x64-Debug");
            Write(Path.Combine(debug, "build.ninja"),
                "rule cxx\n  command = C:\\fake\\cl.exe /nologo /TP $defines /showIncludes /Fo$out /FS -c $in\n  deps = msvc\n" +
                "rule link\n  command = link.exe $in /out:$out\n" +
                "build a.obj: cxx ../../../src/a.cpp\n  defines = -DX=1\n" +
                "build b.obj: cxx ../../../src/b.cpp\n  defines = -DY=2\n" +
                "build app.exe: link a.obj b.obj\n");
            Check(NinjaCompileCommands.FindBuildDirectory(workspace) == debug, "가장 최근 Ninja 빌드 폴더");

            var ninja = FindNinja();
            if (ninja is null)
            {
                Console.WriteLine("SKIP: ninja 통합 확인은 VS의 C++ CMake 도구가 필요합니다(VISUALBOOST_TEST_NINJA로 지정 가능).");
                return;
            }

            var context = CompileContextBuilder.Prepare(workspace, cacheRoot, null, "clang-cl.exe", CancellationToken.None, new CompileCommandSources { NinjaPath = ninja });
            Check(context.Kind == CompileContextKind.Ninja && context.Commands.Count == 2, "폴더 작업 영역의 Ninja 명령: " + context.Kind + " " + context.Reason);
            var b = context.Commands.Single(x => x.File.EndsWith("/src/b.cpp", StringComparison.Ordinal));
            Check(b.Arguments.Contains("-DY=2") && !b.Arguments.Contains("-DX=1") && b.Arguments.Last().EndsWith("b.cpp", StringComparison.Ordinal) &&
                  b.Directory == UnrealCompileCommandsNormalize(debug), "파일별 정의와 작업 폴더: " + string.Join(" ", b.Arguments));
            Check(context.Directory.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(context.Directory, CompileCommandDatabase.FileName)) &&
                  !Directory.EnumerateFiles(workspace, CompileCommandDatabase.FileName, SearchOption.AllDirectories).Any(), "database는 캐시에만 기록");
        }
        finally
        {
            TryDelete(cacheRoot);
            TryDelete(root);
        }
    }

    private static string UnrealCompileCommandsNormalize(string path) => path.Replace('\\', '/');

    private static string? FindNinja()
    {
        var configured = Environment.GetEnvironmentVariable("VISUALBOOST_TEST_NINJA");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var install = FindClangd();
        for (var i = 0; i < 6 && install is not null; i++) install = Path.GetDirectoryName(install);
        return install is null ? null : NinjaCompileCommands.FindNinja(install);
    }

    public static void RunSourceChangeMonitor()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.Watch." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check(SourceChangeMonitor.IsSourcePath(@"C:\p\Source\A.cpp") && SourceChangeMonitor.IsSourcePath(@"C:\p\Source\A.INL") &&
                  !SourceChangeMonitor.IsSourcePath(@"C:\p\Intermediate\Build\A.cpp") && !SourceChangeMonitor.IsSourcePath(@"C:\p\.git\A.h") &&
                  !SourceChangeMonitor.IsSourcePath(@"C:\p\Source\A.cs"), "C++ 소스와 무시 폴더 판별");

            var batches = new System.Collections.Concurrent.BlockingCollection<SourceChangeBatch>();
            using var monitor = new SourceChangeMonitor(root, TimeSpan.FromMilliseconds(300), batches.Add);
            SourceChangeBatch Next() => batches.TryTake(out var batch, 10000) ? batch : new SourceChangeBatch(Array.Empty<string>(), false);

            var a = Path.Combine(root, "Source", "A.cpp");
            Write(a, "int a;");
            Write(Path.Combine(root, "Intermediate", "B.cpp"), "int b;");
            Write(Path.Combine(root, "Source", "Notes.txt"), "x");
            var first = Next();
            Check(first.Paths.SequenceEqual(new[] { a }, StringComparer.OrdinalIgnoreCase) && !first.RequiresRestart,
                "소스 변경만 묶어 알림: " + string.Join(",", first.Paths));

            // 임시 파일에 쓰고 바꿔치기하는 저장은 삭제가 아니라 변경입니다.
            var backup = a + "~RF1.TMP";
            File.Move(a, backup);
            Write(a, "int a2;");
            File.Delete(backup);
            var replaced = Next();
            Check(replaced.Paths.Contains(a, StringComparer.OrdinalIgnoreCase) && !replaced.RequiresRestart, "교체 저장은 변경으로 처리");

            File.Delete(a);
            Check(Next().RequiresRestart, "소스 삭제는 재시작 요구");

            var nested = Path.Combine(root, "Source", "Sub", "C.h");
            Write(nested, "int c;");
            Check(Next().Paths.Contains(nested, StringComparer.OrdinalIgnoreCase), "하위 폴더 소스");
            Directory.Delete(Path.GetDirectoryName(nested)!, true);
            Check(Next().RequiresRestart, "폴더 삭제는 재시작 요구");
            Write(Path.Combine(root, "out", "build", "x64-Debug", "build.ninja"), "rule x\n");
            var regenerated = Next();
            Check(regenerated.CommandsChanged && regenerated.Paths.Count == 0 && !regenerated.RequiresRestart, "Ninja 빌드 파일 변경은 명령 갱신 요구");
            Check(!batches.TryTake(out _, 1000), "남은 알림 없음");
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
