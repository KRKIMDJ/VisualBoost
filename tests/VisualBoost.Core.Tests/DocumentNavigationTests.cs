using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualBoost.Core.DocumentNavigation;

namespace VisualBoost.Core.Tests;

internal static class DocumentNavigationTests
{
    public static void Run()
    {
        var source = """
            #define BODY() { bogus(); }
            namespace N {
            class WIDGET_API Widget {
            public:
                Widget() : value{1} {}
                ~Widget() {}
                UFUNCTION(BlueprintCallable)
                virtual void Update(
                    int count = 2) const;
                void Update(float count) { if (count) { Call(); } auto f = [] { Call(); }; }
                int operator()(int x) { return x; }
                bool operator==(const Widget& x) const { return true; }
                operator bool() const { return true; }
                int value;
            };
            void Widget::Update(int count) const { const char* s = "{ Fake(); }"; /* } */ Call(); }
            }
            auto lambda = [] { Fake(); };
            // void Fake() {}
            """;
        var provider = new CppDocumentMemberProvider();
        var snapshot = provider.Analyze(source);
        Check(string.Join(",", snapshot.Members.Select(m => m.Name)) == "Widget,~Widget,Update,Update,operator(),operator==,operator bool,Update", "함수 목록: " + string.Join(",", snapshot.Members.Select(m => m.Name)));
        foreach (var member in snapshot.Members)
        {
            Check(snapshot.FindContaining(member.NameOffset) == member, "이름 위치");
            Check(snapshot.FindContaining(member.End - 1) == member, "닫는 괄호 위치");
            Check(source.Substring(member.NameOffset).StartsWith(member.Name.Split('(')[0]), "원문 오프셋");
        }
        Check(snapshot.FindContaining(source.IndexOf("Call();"))?.Name == "Update", "중첩 블록은 바깥 함수");
        Check(snapshot.FindContaining(source.IndexOf("auto lambda")) is null, "함수 외부");
        Check(snapshot.Search("Upd ate", false).Count == 3, "이름 AND 검색");
        Check(snapshot.Search("WIDGET_API", false).Count == 0, "경로·소속·인자 제외");
        Check(provider.Analyze("void Pending() { int x;").FindContaining(20)?.Name == "Pending", "미완성 본문");
        Check(provider.Analyze("void Old();").Members[0].Name != provider.Analyze("void New();").Members[0].Name, "미저장 변경");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { provider.Analyze(source, cancelled.Token); throw new Exception("취소 누락"); }
        catch (OperationCanceledException) { }
        var large = string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"void UpdateMovement{i}() {{ if(true) {{ Call(); }} }}"));
        var watch = Stopwatch.StartNew();
        var largeSnapshot = provider.Analyze(large);
        Console.WriteLine($"문서 함수 5,000개 분석: {watch.Elapsed.TotalMilliseconds:F1}ms");
        Check(largeSnapshot.Members.Count == 5000, "대규모 목록 누락");
        var times = Enumerable.Range(0, 30).Select(i => { watch.Restart(); largeSnapshot.Search("Upd Move 49", false); return watch.Elapsed.TotalMilliseconds; }).OrderBy(x => x).ToArray();
        Console.WriteLine($"문서 함수 검색 p95: {times[28]:F1}ms");
        Check(times[28] < 100, "문서 검색 성능 회귀");
        SessionLifetimeAsync().GetAwaiter().GetResult();
    }
    private static async Task SessionLifetimeAsync()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new DelayedProvider(started, release);
        using var session = new DocumentAnalysisSession(provider);
        long published = -1;
        session.Completed += (_, result) => Interlocked.Exchange(ref published, result.Version);
        var old = session.RequestAsync(1, () => "old", 0);
        Check(started.Wait(2000), "이전 분석 시작");
        await session.RequestAsync(2, () => "new", 0);
        release.Set();
        await old;
        Check(published == 2, "늦게 끝난 이전 버전 폐기");
        var read = false;
        var pending = session.RequestAsync(3, () => { read = true; return "new"; }, 200);
        session.Dispose();
        await pending;
        Check(!read && published == 2, "종료 후 버퍼 읽기·결과 공개 방지");
    }
    private sealed class DelayedProvider(ManualResetEventSlim started, ManualResetEventSlim release) : IDocumentMemberProvider
    {
        public DocumentMemberSnapshot Analyze(string source, CancellationToken cancellationToken = default)
        {
            // 취소에 즉시 응답하지 않는 공급자도 오래된 결과를 공개할 수 없어야 합니다.
            if (source == "old") { started.Set(); if (!release.Wait(3000)) throw new Exception("대기 시간 초과"); }
            return new CppDocumentMemberProvider().Analyze("void " + source + "();");
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
