using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Searching;

namespace VisualBoost.Analysis;

internal sealed class SolutionSourceAnalyzer : IDisposable
{
    private const long MaximumSourceLength = 8 * 1024 * 1024;
    private static readonly HashSet<string> CppExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".inl", ".ixx", ".cppm",
    };

    private readonly SourceAnalysisCache cache;
    private readonly SourceSymbolIndex symbols = new();
    private readonly object gate = new();
    // include 연결은 개수만 보여 주므로 그래프는 패스 안에서만 만들고 보관하지 않습니다(엔진 규모 연결 약 78만 개의 경로 문자열).
    private int includeEdgeCount;
    private string? cachedSolution;
    private IReadOnlyDictionary<string, CachedSourceAnalysis>? loadedCache;
    // 끝까지 마친 분석 패스가 이름 인덱스에 공개된 Solution입니다. 이때만 이름 인덱스가 loadedCache와 같은 위치 객체를 담아 부분 갱신할 수 있습니다.
    private string? completedSolution;
    // 부분 갱신한 결과를 아직 분석 캐시 파일에 쓰지 않았습니다. 다음 분석 패스가 바뀐 파일이 없어도 저장합니다.
    private bool unsavedUpdates;
    // 지난 세션에 마친 분석을 불러와 이름 인덱스에 공개했는지입니다(CachedSymbolsPublished).
    private volatile bool cachedSymbolsPublished;
    private string? discoverySolution;
    private readonly HashSet<string> discoveredCacheFiles = new(StringComparer.OrdinalIgnoreCase);
    // 진행 중인 분석 패스의 대기열입니다. 사용자가 연 파일을 앞으로 옮길 때만 다른 스레드에서 읽습니다.
    private volatile SourceAnalysisQueue? activeQueue;

    internal void PrepareCachedDiscovery(string solutionPath, CancellationToken token)
    {
        LoadPrevious(solutionPath, token);
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            discoverySolution = solutionPath;
            discoveredCacheFiles.Clear();
        }
    }

    internal void PublishCachedDiscovery(string solutionPath, IReadOnlyList<string> files, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (discoverySolution != solutionPath || loadedCache is null) return;
            var batch = new List<SourceSymbolLocation>();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (discoveredCacheFiles.Add(file) && loadedCache.TryGetValue(file, out var entry)) batch.AddRange(entry.Analysis.Symbols);
            }
            if (batch.Count > 0) symbols.AppendBatch(batch, token);
        }
    }

    internal SolutionSourceAnalyzer(SourceAnalysisCache? cache = null)
    {
        this.cache = cache ?? new SourceAnalysisCache();
    }

    public int SymbolCount => symbols.Count;

    /// <summary>
    /// 지난 세션에 마친 분석을 불러와 이름 인덱스에 공개했습니다. 분석 캐시는 끝까지 마친 패스만 저장하므로, 이번 분석이 끝나기 전에도 이름
    /// 인덱스가 Solution 전체를 담습니다(그 뒤 바뀐 파일만 예전 내용). 잠금 없이 읽습니다.
    /// </summary>
    public bool CachedSymbolsPublished => cachedSymbolsPublished;

    /// <summary>이름 인덱스의 공개 번호입니다(<see cref="SourceSymbolIndex.Revision"/>). 잠금 없이 읽습니다.</summary>
    public int SymbolRevision => symbols.Revision;
    public string? LastWarning { get; private set; }
    public SymbolCompletionSnapshot CompletionSnapshot => symbols.CompletionSnapshot;
    internal event Action<int>? SymbolsPublished;

    public int IncludeEdgeCount
    {
        get
        {
            // 캐시 게시는 이 잠금을 쥔 채 이름 묶음을 병합하므로, UI thread의 상태 조회(GetSnapshot)가 기다리지 않게 잠금 없이 읽습니다.
            return Volatile.Read(ref includeEdgeCount);
        }
    }

    /// <summary>진행 중인 분석에서 이 파일과 같은 프로젝트의 파일을 먼저 분석하게 합니다. 분석 중이 아니면 아무것도 하지 않습니다.</summary>
    public void Focus(string path) => activeQueue?.Focus(path);

    /// <param name="priority">프로젝트 소속·엔진 위치로 정하는 기본 분석 순서입니다.</param>
    /// <param name="focus">먼저 분석할 파일(열린 문서, 최근에 연 순서)입니다.</param>
    public IReadOnlyList<string> Analyze(
        string solutionPath,
        IReadOnlyList<string> files,
        IReadOnlyList<string> includeRoots,
        CancellationToken cancellationToken,
        Action<SourceAnalysisProgress>? reportProgress = null,
        SourceAnalysisPriority? priority = null,
        IReadOnlyList<string>? focus = null)
    {
        LastWarning = null;
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) completedSolution = null;
        var sourceFiles = files.Where(IsCppFile).ToArray();
        // 열린 파일 → 같은 프로젝트 → 다른 프로젝트 → 보충 파일 → 엔진 순서로 분석하고, 도중에 연 파일은 앞으로 옮깁니다.
        // 캐시를 읽는 동안 연 파일도 반영되게 대기열을 먼저 공개합니다.
        var queue = new SourceAnalysisQueue(sourceFiles, priority ?? SourceAnalysisPriority.None, focus);
        activeQueue = queue;
        try
        {
            return AnalyzeQueued(solutionPath, files, includeRoots, sourceFiles, queue, cancellationToken, reportProgress);
        }
        finally
        {
            // 끝났거나 실패한 패스의 대기열을 다음 분석까지 붙잡지 않습니다. 그 사이 새 패스가 대기열을 바꿨으면 두고 갑니다.
            if (ReferenceEquals(activeQueue, queue)) activeQueue = null;
        }
    }

    private IReadOnlyList<string> AnalyzeQueued(
        string solutionPath,
        IReadOnlyList<string> files,
        IReadOnlyList<string> includeRoots,
        string[] sourceFiles,
        SourceAnalysisQueue queue,
        CancellationToken cancellationToken,
        Action<SourceAnalysisProgress>? reportProgress)
    {
        var previous = LoadPrevious(solutionPath, cancellationToken);
        var progressGate = new object();
        var completedFiles = 0;
        // 저장된 분석이 있으면 다시 연 Solution입니다. 진행 표시는 바뀐 파일의 파싱만 셉니다(아래 1단계 참고).
        var refreshing = previous.Count > 0;
        var progressTotal = sourceFiles.Length;
        var activeFiles = new Dictionary<string, SourceAnalysisStage>(StringComparer.OrdinalIgnoreCase);
        void Report(SourceAnalysisStage stage, string? path = null, bool completed = false)
        {
            lock (progressGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completed)
                {
                    completedFiles++;
                    if (path is not null) activeFiles.Remove(path);
                    path = null;
                }
                else if (path is not null) activeFiles[path] = stage;
                // 병렬 작업 중 다른 파일의 캐시 확인이 실제 파싱 파일 표시를 가리지 않게 합니다.
                var parsing = activeFiles.FirstOrDefault(p => p.Value == SourceAnalysisStage.Parsing);
                if (parsing.Key is not null) { stage = parsing.Value; path = parsing.Key; }
                reportProgress?.Invoke(new SourceAnalysisProgress(stage, completedFiles, progressTotal, path, refreshing));
            }
        }
        if (previous.Count == 0) symbols.ReplaceAll(Array.Empty<SourceSymbolLocation>(), cancellationToken);
        var current = new ConcurrentDictionary<string, CachedSourceAnalysis>(StringComparer.OrdinalIgnoreCase);
        // 이 패스에서 새로 분석한 결과의 문자열을 모으는 풀입니다. 패스가 끝나면 버립니다(저장된 분석은 읽을 때 따로 모음).
        var pool = new StringPool();
        var timedOutFiles = 0;
        var publicationGate = new object();
        var pendingSymbols = new List<SourceSymbolLocation>();
        var pendingFiles = 0;
        var pendingBestRank = SourceAnalysisRank.Engine;
        // 최초 분석은 고정 크기 묶음으로 공개하고 검색 인덱스가 묶음을 계층적으로 병합합니다.
        // 사용자가 연 파일과 그 프로젝트는 작은 묶음으로 공개해 검색에 빨리 나타나게 합니다.
        void PublishProgress(CachedSourceAnalysis entry, SourceAnalysisRank rank)
        {
            // 재방문 시 이미 공개한 전체 캐시를 더 작은 부분 결과로 퇴행시키지 않습니다.
            if (previous.Count > 0) return;
            lock (publicationGate)
            {
                pendingSymbols.AddRange(entry.Analysis.Symbols);
                // 묶음 크기는 묶음에 든 가장 앞선 등급으로 정해, 연 파일이 뒤 등급 파일 128개를 기다리지 않게 합니다.
                // 관련 파일은 최대 수천 개이므로 연 파일보다 큰 묶음으로 병합 횟수를 줄입니다.
                if (rank < pendingBestRank) pendingBestRank = rank;
                var threshold = pendingBestRank switch
                {
                    SourceAnalysisRank.Focus => 8,
                    SourceAnalysisRank.Related => 64,
                    _ => 128,
                };
                if (++pendingFiles < threshold) return;
                symbols.AppendBatch(pendingSymbols, cancellationToken);
                pendingSymbols.Clear();
                pendingFiles = 0;
                pendingBestRank = SourceAnalysisRank.Engine;
                SymbolsPublished?.Invoke(current.Count);
            }
        }
        // 편집기 응답성을 우선하고 남는 처리량만 초기 분석에 사용합니다.
        var workers = Math.Min(2, Math.Max(1, Environment.ProcessorCount - 1));
        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = workers };
        // 2단계에서 파일마다 처음 보고하는 단계입니다. 처음 분석은 캐시 확인부터 보입니다.
        var checking = SourceAnalysisStage.CacheChecking;
        // 1단계에서 없거나 너무 커서 분석하지 않기로 정한 파일입니다. 2단계에서 다시 보지 않습니다.
        var settled = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        if (refreshing)
        {
            // 1단계(다시 연 Solution): 저장된 분석을 쓸 수 있는 파일을 먼저 모두 채택합니다. 그래야 2단계의 진행 수가 실제로 다시 파싱할
            // 파일만 세고, 바뀐 파일이 없으면 파싱 진행을 아예 보이지 않습니다(2026-10-07 사용자 피드백: 열 때마다 파싱이 반복되어 보임).
            Parallel.ForEach(sourceFiles, parallelOptions, file =>
            {
                Report(SourceAnalysisStage.CacheChecking, file);
                try
                {
                    var info = TryGetInfo(file);
                    if (info is null || info.Length > MaximumSourceLength) settled[file] = 0;
                    else TryReuse(file, info, SourceAnalysisRank.Engine);
                }
                finally { if (!cancellationToken.IsCancellationRequested) Report(SourceAnalysisStage.CacheChecking, file, completed: true); }
            });
            lock (progressGate)
            {
                completedFiles = 0;
                progressTotal = sourceFiles.Length - current.Count - settled.Count;
                // 하나도 재사용하지 못했으면(분석 형식 변경·캐시 손상 등) 처음 분석과 같으므로 처음 열기 단계로 보입니다(2026-10-07 검토).
                refreshing = current.Count > 0;
                // 2단계 파일은 모두 다시 파싱할 파일이므로 캐시 확인과 파싱을 한 단계로 보고합니다. 파일 사이의 캐시 확인 보고가 다시 열기 문구를
                // 한 틱씩 끊어 상태 표시줄이 깜박이지 않게 합니다(2026-10-07 검토).
                if (refreshing) checking = SourceAnalysisStage.Parsing;
            }
        }

        // 2단계: 열린 파일 → 같은 프로젝트 → … 순서로 나머지를 분석합니다. 1단계에서 채택하거나 정한 파일은 건너뜁니다.
        // 작업자 하나가 예외로 끝나면 다른 작업자도 남은 대기열(엔진 포함)을 계속 비우지 않고 멈춥니다.
        Parallel.For(0, workers, parallelOptions, (_, loop) =>
        {
            while (!loop.ShouldExitCurrentIteration && queue.TryTake(out var file, out var rank))
            {
                if (current.ContainsKey(file) || settled.ContainsKey(file)) continue;
                AnalyzeFile(file, rank);
            }
        });

        // 표시 종류가 아니라 저장된 분석 버전과 파일 변경 여부로 재사용을 판정합니다.
        bool TryReuse(string file, FileInfo info, SourceAnalysisRank rank)
        {
            if (!previous.TryGetValue(file, out var cached) ||
                cached.Length != info.Length || cached.LastWriteUtcTicks != info.LastWriteTimeUtc.Ticks ||
                cached.Revision != CachedSourceAnalysis.CurrentRevision)
            {
                return false;
            }

            current[file] = cached;
            PublishProgress(cached, rank);
            return true;
        }

        void AnalyzeFile(string file, SourceAnalysisRank rank)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(checking, file);
            try
            {
                var info = TryGetInfo(file);
                if (info is null || info.Length > MaximumSourceLength) return;
                if (TryReuse(file, info, rank)) return;

                Report(SourceAnalysisStage.Parsing, file);
                var analysis = CppSourceAnalyzer.Analyze(file, File.ReadAllText(file), cancellationToken, pool.Intern);
                var after = TryGetInfo(file);
                if (after is null || after.Length != info.Length || after.LastWriteTimeUtc != info.LastWriteTimeUtc) return;
                current[file] = new CachedSourceAnalysis(info.Length, info.LastWriteTimeUtc.Ticks, analysis);
                PublishProgress(current[file], rank);
            }
            catch (RegexMatchTimeoutException)
            {
                // 비정상 구문 하나가 전체 탐색을 막지 않게 격리하고, 불완전한 결과는 캐시하지 않습니다.
                Interlocked.Increment(ref timedOutFiles);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // 잠겼거나 사라진 파일은 다음 증분 분석에서 다시 시도합니다.
            }
            finally { if (!cancellationToken.IsCancellationRequested) Report(checking, file, completed: true); }
        }

        cancellationToken.ThrowIfCancellationRequested();
        LastWarning = timedOutFiles == 0 ? null : $"복잡한 구문으로 {timedOutFiles:N0}개 파일 분석을 건너뛰었습니다. 일부 심볼이 누락될 수 있습니다.";
        Report(SourceAnalysisStage.Indexing);
        // 검색에 필요하지 않은 include 경로 확인 및 캐시 저장이 완료되기 전에 심볼을 공개합니다.
        symbols.ReplaceAll(current.Values.SelectMany(entry => entry.Analysis.Symbols), cancellationToken);
        // include 후처리 도중 종료되어도 이미 완료한 소스 분석을 다음 실행에서 다시 파싱하지 않습니다.
        if (unsavedUpdates || cache.NeedsUpgrade || current.Count != previous.Count || current.Any(pair =>
            !previous.TryGetValue(pair.Key, out var old) || !ReferenceEquals(old, pair.Value)))
        {
            Report(SourceAnalysisStage.Saving);
            cache.Save(solutionPath, current, cancellationToken);
        }
        lock (gate)
        {
            cachedSolution = solutionPath;
            loadedCache = current;
            unsavedUpdates = false;
        }
        var knownFiles = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var filesByName = knownFiles
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var graph = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var externalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 공통 roots는 한 패스 안에서 불변입니다. 로컬 상대 include는 먼저 확인하고,
        // 그 밖의 같은 경로 검색은 실패도 재사용해 반복 디스크 조회를 줄입니다.
        var sharedIncludes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var rootLookup = new IncludeRootLookup(includeRoots);
        var linkedFiles = 0;
        var invalidIncludes = 0;
        string? ResolveSafe(string path, SourceIncludeReference include)
        {
            try { return ResolveInclude(path, include, rootLookup, filesByName, sharedIncludes, cancellationToken); }
            catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
            {
                // 구문 분석 중 추출된 include가 운영체제 경로가 아닐 수 있습니다. 해당 항목만 건너뛰고 진단을 남깁니다.
                invalidIncludes++;
                return null;
            }
        }
        foreach (var entry in current.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportProgress?.Invoke(new SourceAnalysisProgress(SourceAnalysisStage.Linking, linkedFiles, current.Count, entry.Analysis.Path, refreshing));
            var resolved = entry.Analysis.Includes
                .Select(include => ResolveSafe(entry.Analysis.Path, include))
                .Where(path => path is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            graph[entry.Analysis.Path] = resolved;
            foreach (var path in resolved)
            {
                if (!knownFiles.Contains(path)) externalFiles.Add(path);
            }
            linkedFiles++;
        }

        if (invalidIncludes > 0)
            LastWarning = (LastWarning is null ? string.Empty : LastWarning + " ") +
                $"파일 경로로 해석할 수 없는 include {invalidIncludes:N0}개를 건너뛰었습니다. 일부 연결이 누락될 수 있습니다.";

        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            includeEdgeCount = graph.Values.Sum(paths => paths.Length);
            completedSolution = solutionPath;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return externalFiles.ToArray();
    }

    /// <summary>
    /// 저장·생성·삭제된 파일만 다시 분석해 이름 인덱스를 고칩니다. 이 Solution의 분석 패스를 끝까지 마친 뒤가 아니면 아무것도 하지 않고
    /// false를 돌려주므로 호출자가 전체 다시 수집합니다. 분석 패스와 동시에 부르지 않습니다(호출자가 작업을 하나씩 돌림).
    /// </summary>
    /// <remarks>
    /// 전체 다시 수집은 파일 열거·모든 파일 확인·이름 인덱스 두 번 재구성·include 연결·분석 캐시 저장을 하므로 엔진 규모에서 파일 하나 저장에
    /// 수십 초와 1 GB 넘는 일시 메모리가 들었습니다. 분석 캐시 파일은 여기서 쓰지 않습니다. 쓰지 못한 채 끝나도 다음 패스가 바뀐 파일을 수정
    /// 시각으로 알아보고 다시 분석합니다.
    /// </remarks>
    public bool UpdateFiles(string solutionPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, CachedSourceAnalysis> previous;
        lock (gate)
        {
            if (loadedCache is null || !string.Equals(completedSolution, solutionPath, StringComparison.OrdinalIgnoreCase)) return false;
            previous = loadedCache;
        }

        var pool = new StringPool();
        var changes = new Dictionary<string, CachedSourceAnalysis?>(StringComparer.OrdinalIgnoreCase);
        var removed = new List<SourceSymbolLocation>();
        var added = new List<SourceSymbolLocation>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (changes.ContainsKey(path)) continue;
            previous.TryGetValue(path, out var old);
            CachedSourceAnalysis? updated = null;
            var info = IsCppFile(path) ? TryGetInfo(path) : null;
            if (info is not null && info.Length <= MaximumSourceLength)
            {
                if (old is not null && old.Length == info.Length && old.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
                    old.Revision == CachedSourceAnalysis.CurrentRevision) continue;
                try
                {
                    var analysis = CppSourceAnalyzer.Analyze(path, File.ReadAllText(path), cancellationToken, pool.Intern);
                    var after = TryGetInfo(path);
                    // 읽는 사이 또 바뀌었으면 옛 결과를 두고, 그 변경 알림의 다음 갱신에서 다시 분석합니다.
                    if (after is null || after.Length != info.Length || after.LastWriteTimeUtc != info.LastWriteTimeUtc) continue;
                    updated = new CachedSourceAnalysis(info.Length, info.LastWriteTimeUtc.Ticks, analysis);
                }
                catch (RegexMatchTimeoutException)
                {
                    // 전체 패스와 같이 분석하지 못한 파일의 심볼은 공개하지 않습니다.
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    // 저장하는 프로그램이 아직 쥐고 있으면 옛 결과를 둡니다. 쓰기를 마칠 때 오는 변경 알림에서 다시 시도합니다.
                    continue;
                }
            }
            if (old is null && updated is null) continue;
            changes[path] = updated;
            if (old is not null) removed.AddRange(old.Analysis.Symbols);
            if (updated is not null) added.AddRange(updated.Analysis.Symbols);
        }
        if (changes.Count == 0) return true;

        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 그 사이 분석 패스가 돌았으면 옛 위치가 이미 공개 목록에 없으므로 전체 다시 수집에 맡깁니다.
            if (!ReferenceEquals(loadedCache, previous) || !string.Equals(completedSolution, solutionPath, StringComparison.OrdinalIgnoreCase)) return false;
            var next = new Dictionary<string, CachedSourceAnalysis>(previous.Count + changes.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in previous) next[pair.Key] = pair.Value;
            foreach (var change in changes)
            {
                if (change.Value is null) next.Remove(change.Key);
                else next[change.Key] = change.Value;
            }
            loadedCache = next;
            unsavedUpdates = true;
        }
        // 숨긴 위치가 쌓이면 이름 인덱스를 다시 만들므로(엔진 규모 약 10초) 이 잠금 밖에서 합니다. Solution을 닫는 Clear가 이 잠금을 기다리지
        // 않고, 닫기는 공개 전에 확인하는 취소로 막습니다.
        symbols.Update(removed, added, cancellationToken);
        return true;
    }

    public void LoadCachedSymbols(string solutionPath, CancellationToken cancellationToken, IReadOnlyList<string>? allowedFiles = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = LoadPrevious(solutionPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = allowedFiles is null ? null : new HashSet<string>(allowedFiles, StringComparer.OrdinalIgnoreCase);
        lock (gate) completedSolution = null;
        symbols.ReplaceAll(cached.Where(entry => allowed is null || allowed.Contains(entry.Key))
            .SelectMany(entry => entry.Value.Analysis.Symbols), cancellationToken);
        cachedSymbolsPublished = cached.Count > 0;
    }

    public IReadOnlyList<SourceSymbolLocation> FindSymbol(string name) => symbols.Find(name);

    public IReadOnlyList<SourceSymbolMatch> SearchSymbols(
        string query,
        int maximumResults,
        CancellationToken cancellationToken, Func<string, bool>? includes = null) =>
        symbols.Search(query, maximumResults, cancellationToken, includes);

    public void Clear()
    {
        lock (gate)
        {
            discoverySolution = null;
            discoveredCacheFiles.Clear();
            cachedSymbolsPublished = false;
            completedSolution = null;
            symbols.ReplaceAll(Array.Empty<SourceSymbolLocation>());
            includeEdgeCount = 0;
        }
    }

    /// <summary>
    /// Solution을 닫을 때 지난 분석 결과(다음 패스가 바뀌지 않은 파일에 다시 쓰는 파일별 분석)도 놓습니다. <see cref="Clear"/>는 같은 Solution의
    /// 다시 수집에서도 불려 그때는 이 결과를 다시 쓰므로 놓지 않습니다. 놓지 않으면 엔진 규모에서 약 1.1 GB가 다음 Solution을 열 때까지 남았습니다.
    /// </summary>
    public void ReleasePreviousAnalysis()
    {
        lock (gate)
        {
            loadedCache = null;
            cachedSolution = null;
        }
    }

    public void Dispose() => symbols.Dispose();

    private IReadOnlyDictionary<string, CachedSourceAnalysis> LoadPrevious(string solutionPath, CancellationToken cancellationToken)
    {
        // 동일 분석 패스의 선공개와 본 분석에서 큰 캐시 파일을 두 번 역직렬화하지 않습니다.
        if (loadedCache is not null && string.Equals(cachedSolution, solutionPath, StringComparison.OrdinalIgnoreCase))
            return loadedCache;
        loadedCache = cache.Load(solutionPath, cancellationToken);
        cachedSolution = solutionPath;
        return loadedCache;
    }

    private static string? ResolveInclude(
        string sourcePath,
        SourceIncludeReference include,
        IncludeRootLookup rootLookup,
        IReadOnlyDictionary<string, string[]> filesByName,
        Dictionary<string, string?> sharedIncludes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relative = include.Value.Replace('/', Path.DirectorySeparatorChar);
        if (!include.IsSystem)
        {
            var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, relative));
            if (File.Exists(local)) return local;
        }

        if (sharedIncludes.TryGetValue(relative, out var shared)) return shared;

        var name = Path.GetFileName(relative);
        if (filesByName.TryGetValue(name, out var candidates))
        {
            var suffix = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var match = candidates
                .Where(path => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.Length)
                .FirstOrDefault();
            if (match is not null) { sharedIncludes[relative] = match; return match; }
        }

        var resolved = rootLookup.Find(relative, cancellationToken);
        sharedIncludes[relative] = resolved;
        return resolved;
    }

    private static bool IsCppFile(string path) => CppExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 크기·수정 시각을 이 자리에서 읽어 둔 파일 정보입니다. <see cref="FileInfo"/>는 처음 접근할 때 읽으므로, 확인 뒤 파일이 지워지면
    /// 나중의 <c>Length</c>가 <see cref="FileNotFoundException"/>을 내 분석 패스 전체를 멈춥니다(2026-10-07 검토). <c>Exists</c>로 미리 읽습니다.
    /// </summary>
    private static FileInfo? TryGetInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info : null;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return null;
        }
    }
}
