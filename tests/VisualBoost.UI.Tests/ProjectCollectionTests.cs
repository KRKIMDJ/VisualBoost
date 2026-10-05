using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using VisualBoost.Services;

internal static class ProjectCollectionTests
{
    public static void Run()
    {
        var items = new ProjectItems();
        for (var i = 0; i < 1024; i++) items.Values.Add(new ProjectItem { FileNames = new[] { "", @"C:\Fixture\Src\Unit" + i + ".cpp" } });
        items.FailedIndex = 14;
        var child = new Project { ProjectItems = items };
        var folderItems = new ProjectItems();
        var folder = new Project { Kind = ProjectKinds.vsProjectKindSolutionFolder, ProjectItems = folderItems };
        folderItems.Values.Add(new ProjectItem { SubProject = child });
        folderItems.Values.Add(new ProjectItem { SubProject = child });
        folderItems.Values.Add(new ProjectItem { SubProject = folder });
        folderItems.Values.Add(new ProjectItem { FileNames = new[] { "", @"C:\Fixture\Readme.md" } });
        var dte = new DTE2();
        dte.Solution.Projects.Values.Add(folder);
        var published = new List<string>();
        var batches = 0;
        var result = Task.Run(() => SolutionSearchRootCollector.CollectAsync(dte, CancellationToken.None, batch =>
        { batches++; published.AddRange(batch); })).GetAwaiter().GetResult();
        Check(result.ExplicitFiles.Count == 1024 && published.Distinct().Count() == 1024,
            "실제 수집 코드의 중첩 폴더·중복 프로젝트·순환 방어·COM 실패 항목 격리");
        Check(batches >= 8 && result.SearchRoots.Contains(@"C:\Fixture"), "1,024개 등록 파일의 분할 공개와 소스 루트 수집");
        using var token = new CancellationTokenSource();
        var calls = 0;
        try
        {
            Task.Run(() => SolutionSearchRootCollector.CollectAsync(dte, token.Token, _ => { calls++; token.Cancel(); }))
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("수집 중 취소가 무시되었습니다.");
        }
        catch (OperationCanceledException) { }
        Check(calls == 1, "수집 도중 취소 뒤 추가 파일 공개 차단");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message + " (DTE 경계 대역)");
    }
}

namespace EnvDTE
{
    internal sealed class ProjectItems
    {
        public List<ProjectItem> Values { get; } = new();
        public int Count => Values.Count;
        public int FailedIndex { get; set; } = -1;
        public ProjectItem Item(int index) => index == FailedIndex ? throw new COMException("Unloaded item") : Values[index - 1];
    }
    internal sealed class Projects
    {
        public List<Project> Values { get; } = new();
        public int Count => Values.Count;
        public Project Item(int index) => Values[index - 1];
    }
    internal sealed class Solution
    {
        public string FullName { get; set; } = @"C:\Fixture\Sample.sln";
        public Projects Projects { get; } = new();
    }
}
namespace EnvDTE80
{
    internal static class ProjectKinds { public const string vsProjectKindSolutionFolder = "SolutionFolder"; }
}
namespace Microsoft.VisualStudio.Shell
{
    // 스케줄링 동작 자체가 아니라 실제 수집 코드의 분할·취소·계층 처리를 검증하는 경계입니다.
    internal sealed class TestThreadFactory
    {
        public Task SwitchToMainThreadAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
