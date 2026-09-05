using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VisualBoost.Core.Analysis;
using VisualBoost.Services;
using VisualBoost.UI;

internal static class UsageLifecycleTests
{
    public static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var view = new SymbolUsagesControl();
        using var provider = new ControlledProvider();
        var index = new SolutionFileIndexService();
        var projects = Array.Empty<SolutionProjectInfo>();
        for (var iteration = 0; iteration < 12; iteration++)
        {
            provider.Reset();
            view.StartSearch(index, provider, "Old", null, projects, _ => { });
            Until(() => provider.Started.IsSet);
            view.StartSearch(index, provider, "New", null, projects, _ => { });
            Until(() => view.TestResultSymbol == "New");
            provider.Release.Set();
            Until(() => provider.Finished.IsSet);
            // 지연된 이전 요청의 예외가 새 결과와 상태를 덮어쓰지 않아야 합니다.
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (view.TestResultSymbol != "New" || view.TestStatus == "검색 오류")
                throw new InvalidOperationException("이전 검색이 최신 결과를 덮어썼습니다.");
        }
        index.Building = true;
        view.StartSearch(index, provider, "Waiting", null, projects, _ => { });
        view.CancelSearch();
        Until(() => view.TestStatus == "검색 취소됨");
        index.Building = false;
        view.StartSearch(index, provider, "Reopened", null, projects, _ => { });
        Until(() => view.TestResultSymbol == "Reopened");
        var owner = "Game.vcxproj";
        view.StartSearch(index, provider, "Owned", owner, projects, _ => owner = "Other.vcxproj", "Game");
        Until(() => view.TestResultSymbol == "Owned");
        view.TestOpenSelection();
        view.TestScope(SymbolUsageScope.EntireSolution);
        Until(() => provider.LastScope == SymbolUsageScope.EntireSolution && view.TestResultSymbol == "Owned");
        view.TestScope(SymbolUsageScope.CurrentProject);
        Until(() => provider.LastScope == SymbolUsageScope.CurrentProject && view.TestResultSymbol == "Owned");
        if (owner != "Other.vcxproj" || provider.LastProjectFile != "Game.vcxproj")
            throw new InvalidOperationException("결과를 연 뒤 실행 문서의 프로젝트가 변경되었습니다.");
        Console.WriteLine("PASS: 사용처 검색 교체 12회, 지연 오류 격리, 인덱싱 대기 취소 및 재실행");
        Console.WriteLine("PASS: 결과 이동 및 범위 전환 후에도 실행 프로젝트 유지");
    }

    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("사용처 검색 테스트 시간 초과");
            Thread.Yield();
        }
    }

    private sealed class ControlledProvider : ISymbolUsageProvider, IDisposable
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public ManualResetEventSlim Finished { get; } = new();
        public string? LastProjectFile { get; private set; }
        public SymbolUsageScope LastScope { get; private set; }
        public void Reset() { Started.Reset(); Release.Reset(); Finished.Reset(); }
        public IReadOnlyList<SourceUsageLocation> FindUsages(string symbol, IReadOnlyList<string> paths,
            string? root, SymbolUsageScope scope, int limit, CancellationToken token)
        {
            LastProjectFile = root;
            LastScope = scope;
            if (symbol == "Old")
            {
                Started.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                Finished.Set();
                throw new InvalidOperationException("지연된 이전 요청의 오류");
            }
            return new[] { new SourceUsageLocation(symbol, "Sample.cpp", 10, 1, symbol + "();") };
        }
        public void Dispose() { Started.Dispose(); Release.Dispose(); Finished.Dispose(); }
    }
}

namespace VisualBoost.Services
{
    // 실제 검색 제어 코드를 VS 호스트 없이 검증하기 위한 경계 대역입니다.
    internal enum SolutionFileIndexState { Ready, Building }
    internal sealed class SolutionFileIndexService
    {
        public bool Building { get; set; }
        public (SolutionFileIndexState State, int Count) GetSnapshot() =>
            (Building ? SolutionFileIndexState.Building : SolutionFileIndexState.Ready, 0);
        public IReadOnlyList<string> GetFilePathsSnapshot() => Building ? Array.Empty<string>() : new[] { "Sample.cpp" };
        public Task WaitUntilReadyAsync(CancellationToken token) => Task.Delay(Timeout.Infinite, token);
    }
    internal sealed class SolutionProjectInfo { }
    internal static class ProjectNameResolver
    {
        public static string Resolve(string path, IReadOnlyList<SolutionProjectInfo> projects) => "TestProject";
    }
}

namespace VisualBoost.UI
{
    public partial class SymbolUsagesControl
    {
        private readonly TextBlock SymbolText = new();
        private readonly ComboBox ScopeSelector = new();
        private readonly ComboBoxItem CurrentProjectScopeItem = new();
        private readonly Button CancelSearchButton = new();
        private readonly ListBox ResultsList = new();
        private readonly StackPanel EmptyStatePanel = new();
        private readonly TextBlock EmptyStateTitle = new();
        private readonly TextBlock EmptyStateDescription = new();
        private readonly TextBlock StatusText = new();
        private void InitializeComponent() { }
        internal string? TestResultSymbol => (ResultsList.SelectedItem as SymbolUsageResultItem)?.Location.Symbol;
        internal string TestStatus => StatusText.Text;
        internal void TestOpenSelection() => OpenSelection();
        internal void TestScope(SymbolUsageScope value) { scope = value; _ = RefreshResultsAsync(); }
    }
}
