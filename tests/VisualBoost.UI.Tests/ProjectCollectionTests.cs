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
        VerifyMembershipReuse();
    }

    private static void VerifyMembershipReuse()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VisualBoost-Members-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var project = System.IO.Path.Combine(root, "Game.vcxproj");
            System.IO.File.WriteAllText(project, "<Project />");
            var items = new ProjectItems();
            for (var i = 0; i < 3; i++) items.Values.Add(new ProjectItem { FileNames = new[] { "", System.IO.Path.Combine(root, "Src", "Unit" + i + ".cpp") } });
            var game = new Project { FullName = project, Name = "Game", ProjectItems = items };
            var toolItems = new ProjectItems();
            toolItems.Values.Add(new ProjectItem { FileNames = new[] { "", System.IO.Path.Combine(root, "Tool", "Program.cs") } });
            var tool = new Project { FullName = System.IO.Path.Combine(root, "Tool.csproj"), Name = "Tool", ProjectItems = toolItems };
            var dte = new DTE2();
            dte.Solution.FullName = System.IO.Path.Combine(root, "Game.sln");
            dte.Solution.Projects.Values.Add(game);
            dte.Solution.Projects.Values.Add(tool);
            var cache = new ProjectMembershipCache(System.IO.Path.Combine(root, "cache"));
            IReadOnlyList<string> Collect() =>
                Task.Run(() => SolutionSearchRootCollector.CollectAsync(dte, CancellationToken.None, _ => { }, cache)).GetAwaiter().GetResult().ExplicitFiles;

            var first = Collect();
            var reads = items.Reads;
            var toolReads = toolItems.Reads;
            var second = Collect();
            Check(first.Count == 4 && second.OrderBy(p => p).SequenceEqual(first.OrderBy(p => p)) && items.Reads == reads && toolItems.Reads > toolReads,
                "프로젝트 파일이 그대로인 C++ 프로젝트는 저장한 항목을 쓰고 C# 프로젝트는 다시 열거");
            game.Saved = false;
            Collect();
            Check(items.Reads > reads, "저장하지 않은 변경이 있는 프로젝트는 다시 열거");
            game.Saved = true;
            reads = items.Reads;
            items.Values.Add(new ProjectItem { FileNames = new[] { "", System.IO.Path.Combine(root, "Src", "Added.cpp") } });
            System.IO.File.AppendAllText(project, " ");
            var changed = Collect();
            Check(items.Reads > reads && changed.Count == 5, "프로젝트 파일이 바뀌면 다시 열거해 새 항목 반영");

            // 열거 중 실패한 항목이 있던 수집은 일부만 읽은 목록이므로 저장하지 않습니다.
            items.FailedIndex = 2;
            System.IO.File.AppendAllText(project, " ");
            Collect();
            items.FailedIndex = -1;
            reads = items.Reads;
            Collect();
            Check(items.Reads > reads, "열거 중 실패가 있던 수집은 저장하지 않아 다음 열기에서 다시 열거");

            System.IO.File.WriteAllText(project, "<Project><ItemGroup><ClCompile Include=\"Src\\*.cpp\" /></ItemGroup></Project>");
            Collect();
            reads = items.Reads;
            Collect();
            Check(items.Reads > reads, "와일드카드 항목을 쓰는 C++ 프로젝트는 저장하지 않고 매번 열거");

            // 큰 프로젝트의 저장한 목록은 나눠 넣어도 빠짐없이 씁니다.
            var engine = System.IO.Path.Combine(root, "Engine.vcxproj");
            System.IO.File.WriteAllText(engine, "<Project />");
            var engineItems = new ProjectItems();
            for (var i = 0; i < 9000; i++) engineItems.Values.Add(new ProjectItem { FileNames = new[] { "", System.IO.Path.Combine(root, "Engine", "E" + i + ".cpp") } });
            dte.Solution.Projects.Values.Add(new Project { FullName = engine, Name = "Engine", ProjectItems = engineItems });
            var withEngine = Collect();
            var engineReads = engineItems.Reads;
            var reused = Collect();
            Check(engineItems.Reads == engineReads && reused.Count == withEngine.Count && reused.Count(p => p.Contains(@"\Engine\")) == 9000,
                "큰 프로젝트의 저장한 목록을 나눠 넣어 모두 재사용");
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
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
        public int Reads { get; private set; }
        public ProjectItem Item(int index) { Reads++; return index == FailedIndex ? throw new COMException("Unloaded item") : Values[index - 1]; }
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
