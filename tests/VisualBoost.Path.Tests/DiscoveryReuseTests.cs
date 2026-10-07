using System;
using System.IO;
using System.Linq;
using VisualBoost.Core.Analysis;
using VisualBoost.Services;

/// <summary>다시 연 Solution의 프로젝트 항목 재사용과 상태 표시줄 문구 규칙을 확인합니다.</summary>
internal static class DiscoveryReuseTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost-Discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifyMembership(root);
            VerifyStatusText();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void VerifyMembership(string root)
    {
        Check(ProjectMembershipCache.IsCacheable(@"C:\p\Game.vcxproj") && ProjectMembershipCache.IsCacheable(@"C:\p\Shared.VCXITEMS") &&
              !ProjectMembershipCache.IsCacheable(@"C:\p\Tool.csproj") && !ProjectMembershipCache.IsCacheable(null),
            "항목을 직접 적는 C++ 프로젝트 파일만 저장 대상");
        var project = Path.Combine(root, "Game.vcxproj");
        File.WriteAllText(project, "<Project />");
        var stamp = ProjectMembershipCache.Stamp(project);
        Check(stamp is not null && ProjectMembershipCache.Stamp(Path.Combine(root, "Missing.vcxproj")) is null, "프로젝트 파일 크기·수정 시각");
        var cache = new ProjectMembershipCache(Path.Combine(root, "cache"));
        var solution = Path.Combine(root, "Game.sln");
        var files = new[] { Path.Combine(root, "A.cpp"), Path.Combine(root, "한글 B.h") };
        cache.Save(solution, new[]
        {
            new ProjectMembership(project, stamp!.Value, files),
            new ProjectMembership(Path.Combine(root, "Tool.csproj"), stamp.Value, files),
        });
        var loaded = cache.Load(solution);
        Check(loaded.Count == 1 && loaded.TryGetValue(project.ToUpperInvariant(), out var entry) && entry.Stamp.Equals(stamp.Value) &&
              entry.Files.SequenceEqual(files), "저장한 항목 목록을 프로젝트 경로(대소문자 무시)로 복원하고 C# 프로젝트는 저장하지 않음");
        Check(cache.Load(Path.Combine(root, "Other.sln")).Count == 0, "다른 Solution의 목록은 쓰지 않음");
        File.AppendAllText(project, " ");
        Check(!ProjectMembershipCache.Stamp(project)!.Value.Equals(stamp.Value), "프로젝트 파일이 바뀌면 다시 열거");
        var stored = Directory.GetFiles(Path.Combine(root, "cache"), "*.bin").Single();
        File.WriteAllBytes(stored, new byte[] { 1, 2, 3 });
        Check(cache.Load(solution).Count == 0, "깨진 캐시는 빈 목록");
        // 문자열 길이 부호가 깨지면 BinaryReader가 FormatException을 냅니다. 수집 실패가 아니라 빈 목록이어야 합니다.
        File.WriteAllBytes(stored, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        Check(cache.Load(solution).Count == 0, "길이 부호가 깨진 캐시도 빈 목록");

        var wildcard = Path.Combine(root, "Wild.vcxproj");
        File.WriteAllText(wildcard, "<Project>\n  <ItemGroup>\n    <ClCompile Include=\"Src\\**\\*.cpp\" />\n  </ItemGroup>\n</Project>");
        Check(ProjectMembershipCache.HasWildcardItems(wildcard) && !ProjectMembershipCache.HasWildcardItems(project) &&
              ProjectMembershipCache.HasWildcardItems(Path.Combine(root, "Missing.vcxproj")), "와일드카드 항목 판정(읽지 못하면 저장 안 함)");
        cache.Save(solution, new[] { new ProjectMembership(wildcard, ProjectMembershipCache.Stamp(wildcard)!.Value, files) });
        Check(cache.Load(solution).Count == 0, "와일드카드 항목을 쓰는 프로젝트는 저장하지 않음");
    }

    private static void VerifyStatusText()
    {
        static SolutionFileIndexSnapshot Snapshot(SolutionFileIndexState state, bool analyzing, SourceAnalysisProgress? progress, bool refreshing) =>
            new(state, 1234, 1, analyzing, 0, 0, TimeSpan.Zero, null, null, progress, refreshing);
        Check(AnalysisStatusText.Format(Snapshot(SolutionFileIndexState.Building, true, null, false)) == "VisualBoost: 소스 파일 수집 중 · 1,234개" &&
              AnalysisStatusText.Format(Snapshot(SolutionFileIndexState.Building, true, null, true)) is null,
            "처음 열 때만 파일 수집을 표시");
        var cold = new SourceAnalysisProgress(SourceAnalysisStage.Linking, 5, 10, @"C:\p\A.cpp");
        Check(AnalysisStatusText.Format(Snapshot(SolutionFileIndexState.Ready, true, cold, false)) == "VisualBoost: include 정리 중 · 5/10 · A.cpp",
            "처음 분석은 모든 단계를 표시");
        // 분석 패스가 진행 값을 낸 뒤에는 분석이 저장된 결과를 재사용하는지로 정합니다. 파일 목록 캐시만 있고 분석 캐시를 쓰지 못한 첫 분석은
        // 모든 단계를 보입니다.
        foreach (var stage in new[] { SourceAnalysisStage.CacheChecking, SourceAnalysisStage.Indexing, SourceAnalysisStage.Linking, SourceAnalysisStage.Saving })
        {
            Check(AnalysisStatusText.Format(Snapshot(SolutionFileIndexState.Ready, true, new SourceAnalysisProgress(stage, 1, 2, null, refreshing: true), false)) is null &&
                  AnalysisStatusText.Format(Snapshot(SolutionFileIndexState.Ready, true, new SourceAnalysisProgress(stage, 1, 2), true)) is not null,
                "다시 열기의 검증·준비 단계는 숨기고 분석 캐시를 쓰지 못한 첫 분석은 표시: " + stage);
        }
        var waiting = new SourceAnalysisProgress(SourceAnalysisStage.Waiting, 0, 0);
        Check(AnalysisStatusText.Format(Snapshot(SolutionFileIndexState.Ready, true, waiting, true)) is null &&
              AnalysisStatusText.Format(Snapshot(SolutionFileIndexState.Ready, true, waiting, false)) == "VisualBoost: 파싱 준비 중",
            "분석 시작 전 대기는 파일 목록 캐시로 판정");
        var parsing = new SourceAnalysisProgress(SourceAnalysisStage.Parsing, 0, 2, @"C:\p\A.cpp", refreshing: true);
        Check(AnalysisStatusText.Format(Snapshot(SolutionFileIndexState.Ready, true, parsing, true), out var refreshing) == "VisualBoost: 바뀐 파일 분석 중 · 0/2 · A.cpp" &&
              refreshing && AnalysisStatusText.Format(Snapshot(SolutionFileIndexState.Ready, false, parsing, true)) is null,
            "다시 열기에서는 바뀐 파일 분석만 증분 문구로 표시");
        var coldParsing = new SourceAnalysisProgress(SourceAnalysisStage.Parsing, 0, 2, @"C:\p\A.cpp");
        Check(AnalysisStatusText.Format(Snapshot(SolutionFileIndexState.Ready, true, coldParsing, false), out refreshing) == "VisualBoost: 파싱 중 · 0/2 · A.cpp" && !refreshing &&
              AnalysisStatusText.Format(Snapshot(SolutionFileIndexState.Ready, true, coldParsing, true), out refreshing) == "VisualBoost: 파싱 중 · 0/2 · A.cpp" && !refreshing,
            "처음 분석(분석 캐시를 쓰지 못한 다시 열기 포함)은 파싱 문구 유지");
        VerifyRefreshDelay();
    }

    private static void VerifyRefreshDelay()
    {
        var start = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
        DateTime? since = null;
        Check(AnalysisStatusText.Delay("처음", false, start, ref since) == "처음" && since is null, "처음 열기 문구는 바로 표시");
        Check(AnalysisStatusText.Delay("다시", true, start, ref since) is null &&
              AnalysisStatusText.Delay("다시 2", true, start.AddMilliseconds(750), ref since) is null &&
              AnalysisStatusText.Delay("다시 3", true, start.AddSeconds(1), ref since) == "다시 3",
            "다시 열기 문구는 처음 나온 뒤 1초가 지나야 표시");
        Check(AnalysisStatusText.Delay(null, true, start.AddSeconds(2), ref since) is null && since is null &&
              AnalysisStatusText.Delay("다시", true, start.AddSeconds(2.5), ref since) is null,
            "문구가 끝나면 기다린 시간을 지우고 다음 다시 열기는 새로 기다림");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
