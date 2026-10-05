using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Indexing;

namespace VisualBoost.Services;

internal static class SolutionSearchRootCollector
{
    public static async Task<SolutionIndexDiscoveryResult> CollectAsync(
        DTE2 dte, CancellationToken cancellationToken, Action<IReadOnlyList<string>> publish)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var solution = dte.Solution.FullName ?? string.Empty;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedProjects = new HashSet<Project>();
        var projects = new List<(string Path, string Name, List<string> Files)>();
        var pending = new Queue<Action>();
        var batch = new List<string>();

        void ReadItems(ProjectItems? items, List<string> members)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items is null) return;
            var count = items.Count;
            var next = 1;
            void ReadNext()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var itemIndex = next++;
                // 파일 수만큼 작업 객체를 한 번에 만들면 UI에서 큰 할당·GC가 발생할 수 있습니다.
                if (next <= count) pending.Enqueue(ReadNext);
                var item = items.Item(itemIndex);
                if (item.SubProject is Project subProject) { ReadProject(subProject); return; }
                var fileCount = item.FileCount;
                for (var fileIndex = 1; fileIndex <= fileCount; fileIndex++)
                {
                    var path = item.FileNames[(short)fileIndex];
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    members.Add(path);
                    if (files.Add(path)) batch.Add(path);
                }
                ReadItems(item.ProjectItems, members);
            }
            if (count > 0) pending.Enqueue(ReadNext);
        }

        void ReadProject(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // 같은 하위 프로젝트를 여러 번 노출하는 계층이나 순환 참조를 방어합니다.
            if (!visitedProjects.Add(project)) return;
            var members = new List<string>();
            var isFolder = string.Equals(project.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase);
            projects.Add((isFolder ? string.Empty : project.FullName, project.Name, members));
            ReadItems(project.ProjectItems, members);
        }

        var solutionProjects = dte.Solution.Projects;
        for (var i = 1; i <= solutionProjects.Count; i++)
        {
            var projectIndex = i;
            pending.Enqueue(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ReadProject(solutionProjects.Item(projectIndex));
            });
        }

        var slice = Stopwatch.StartNew();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { pending.Dequeue()(); }
            catch (COMException) { /* 언로드·삭제 중인 항목은 다음 프로젝트 이벤트에서 재수집합니다. */ }
            catch (ArgumentException) { /* 순회 도중 제거된 컬렉션 항목입니다. */ }
            if (slice.ElapsedMilliseconds < 8 && batch.Count < 128) continue;
            var ready = batch.ToArray();
            batch.Clear();
            await Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); publish(ready); }, cancellationToken);
            // COM 객체는 항상 UI에서만 접근하고, 키 입력·화면 갱신에 실행 기회를 줍니다.
            await Task.Delay(1, cancellationToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            slice.Restart();
        }
        var last = batch.ToArray();
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            publish(last);
            var roots = projects.SelectMany(project => ProjectSourceScope.GetRoots(project.Path, project.Files))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var scopes = projects.Where(project => project.Path.Length > 0)
                .Select(project => SymbolSearchScope.Project(project.Path, project.Name, project.Files));
            var engineRoots = UnrealEngineSourceLocator.Find(Path.GetDirectoryName(solution), Array.Empty<string>())
                .Select(source => Path.GetDirectoryName(source)!).ToArray();
            // 큰 파일 집합을 다루므로 분석 순서 정보도 UI thread 밖에서 만듭니다.
            var priority = new SourceAnalysisPriority(projects.Where(p => p.Path.Length > 0).Select(p => p.Files), engineRoots);
            return new SolutionIndexDiscoveryResult(solution, roots, files.ToArray(),
                SymbolSearchScope.CreateCatalog(scopes, projects.Where(p => p.Path.Length > 0).SelectMany(p => p.Files), engineRoots), priority);
        }, cancellationToken);
    }
}
