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
    private string? cachedSolution;
    private IReadOnlyDictionary<string, CachedSourceAnalysis>? loadedCache;
    // 이름 인덱스에 지금 공개된 파일별 분석입니다(위치 객체까지 같음). 이것과 비교해 바뀐 파일의 위치만 숨기고 더하므로, 다시 열 때 이름
    // 인덱스는 저장된 분석을 읽은 직후 한 번만 만듭니다. null이면 공개 내용을 파일 단위로 모릅니다(비운 뒤, 처음 분석의 묶음 공개 중).
    private IReadOnlyDictionary<string, CachedSourceAnalysis>? published;
    private string? publishedSolution;
    // Clear가 취소합니다. 잠금 밖에서 만든 공개가 Solution을 닫거나 바꾼 뒤의 이름 인덱스에 들어가지 않게 합니다.
    private CancellationTokenSource clearing = new();
    // 끝까지 마친 분석 패스가 이름 인덱스에 공개된 Solution입니다. 이때만 공개된 분석이 디스크 기준이라 부분 갱신할 수 있습니다.
    private string? completedSolution;
    // 부분 갱신한 결과를 아직 분석 캐시 파일에 쓰지 않았습니다. 다음 분석 패스가 바뀐 파일이 없어도 저장합니다.
    private bool unsavedUpdates;
    // 지난 세션에 마친 분석을 불러와 이름 인덱스에 공개했는지입니다(CachedSymbolsPublished).
    private volatile bool cachedSymbolsPublished;
    // 진행 중인 분석 패스의 대기열입니다. 사용자가 연 파일을 앞으로 옮길 때만 다른 스레드에서 읽습니다.
    private volatile SourceAnalysisQueue? activeQueue;

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

    /// <summary>진행 중인 분석에서 이 파일과 같은 프로젝트의 파일을 먼저 분석하게 합니다. 분석 중이 아니면 아무것도 하지 않습니다.</summary>
    public void Focus(string path) => activeQueue?.Focus(path);

    /// <param name="priority">프로젝트 소속·엔진 위치로 정하는 기본 분석 순서입니다.</param>
    /// <param name="focus">먼저 분석할 파일(열린 문서, 최근에 연 순서)입니다.</param>
    public void Analyze(
        string solutionPath,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken,
        Action<SourceAnalysisProgress>? reportProgress = null,
        SourceAnalysisPriority? priority = null,
        IReadOnlyList<string>? focus = null)
    {
        LastWarning = null;
        CancellationToken cleared;
        lock (gate)
        {
            completedSolution = null;
            cleared = clearing.Token;
        }
        using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cleared);
        cancellationToken = link.Token;
        cancellationToken.ThrowIfCancellationRequested();
        var sourceFiles = files.Where(IsCppFile).ToArray();
        // 열린 파일 → 같은 프로젝트 → 다른 프로젝트 → 보충 파일 → 엔진 순서로 분석하고, 도중에 연 파일은 앞으로 옮깁니다.
        // 캐시를 읽는 동안 연 파일도 반영되게 대기열을 먼저 공개합니다.
        var queue = new SourceAnalysisQueue(sourceFiles, priority ?? SourceAnalysisPriority.None, focus);
        activeQueue = queue;
        try
        {
            AnalyzeQueued(solutionPath, sourceFiles, queue, cleared, cancellationToken, reportProgress);
        }
        finally
        {
            // 끝났거나 실패한 패스의 대기열을 다음 분석까지 붙잡지 않습니다. 그 사이 새 패스가 대기열을 바꿨으면 두고 갑니다.
            if (ReferenceEquals(activeQueue, queue)) activeQueue = null;
        }
    }

    private void AnalyzeQueued(
        string solutionPath,
        string[] sourceFiles,
        SourceAnalysisQueue queue,
        CancellationToken cleared,
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
        if (previous.Count == 0)
        {
            // 처음 분석은 파일 묶음을 덧붙여 공개하므로 끝날 때까지 공개 내용을 파일 단위로 추적하지 않습니다.
            lock (gate) published = null;
            symbols.ReplaceAll(Array.Empty<SourceSymbolLocation>(), cancellationToken);
        }
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
                current[file] = Entry(info, analysis);
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
        // 캐시 저장이 끝나기 전에 심볼을 공개합니다. 다시 열기는 바뀐 파일의 차이만 공개하고, 바뀐 파일이 없으면 이름 인덱스를 건드리지 않습니다.
        PublishEntries(solutionPath, current, cleared, cancellationToken);
        if (unsavedUpdates || cache.NeedsUpgrade || current.Count != previous.Count || current.Any(pair =>
            !previous.TryGetValue(pair.Key, out var old) || !ReferenceEquals(old, pair.Value)))
        {
            Report(SourceAnalysisStage.Saving);
            cache.Save(solutionPath, current, cancellationToken);
        }
        lock (gate)
        {
            // Solution을 닫아 지난 분석을 놓은 뒤(Clear·ReleasePreviousAnalysis)에 끝난 패스가 그 결과를 다시 붙잡지 않게 먼저 봅니다
            // (2026-10-10 검토 90). 저장은 끝났으므로 같은 Solution이면 다음 패스가 파일에서 읽습니다.
            cancellationToken.ThrowIfCancellationRequested();
            cachedSolution = solutionPath;
            loadedCache = current;
            unsavedUpdates = false;
            completedSolution = solutionPath;
        }
    }

    /// <summary>
    /// 저장·생성·삭제된 파일만 다시 분석해 이름 인덱스를 고칩니다. 이 Solution의 분석 패스를 끝까지 마친 뒤가 아니면 아무것도 하지 않고
    /// false를 돌려주므로 호출자가 전체 다시 수집합니다. 분석 패스와 동시에 부르지 않습니다(호출자가 작업을 하나씩 돌림).
    /// </summary>
    /// <remarks>
    /// 전체 다시 수집은 파일 열거·모든 파일 확인·이름 인덱스 재구성·분석 캐시 저장을 하므로 엔진 규모에서 파일 하나 저장에
    /// 수십 초와 1 GB 넘는 일시 메모리가 들었습니다. 분석 캐시 파일은 여기서 쓰지 않습니다. 쓰지 못한 채 끝나도 다음 패스가 바뀐 파일을 수정
    /// 시각으로 알아보고 다시 분석합니다.
    /// </remarks>
    /// <param name="excluded">디스크에 있어도 파일 목록에서 빠진 파일입니다(프로젝트에서 제거된 수집 루트 밖 항목). 지운 파일처럼 뺍니다.</param>
    public bool UpdateFiles(string solutionPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken, ISet<string>? excluded = null)
    {
        IReadOnlyDictionary<string, CachedSourceAnalysis> previous;
        CancellationToken cleared;
        lock (gate)
        {
            if (published is null || !string.Equals(completedSolution, solutionPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(publishedSolution, solutionPath, StringComparison.OrdinalIgnoreCase)) return false;
            previous = published;
            cleared = clearing.Token;
        }
        using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cleared);
        cancellationToken = link.Token;

        var pool = new StringPool();
        var changes = new Dictionary<string, CachedSourceAnalysis?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (changes.ContainsKey(path)) continue;
            previous.TryGetValue(path, out var old);
            CachedSourceAnalysis? updated = null;
            var info = IsCppFile(path) && excluded?.Contains(path) != true ? TryGetInfo(path) : null;
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
                    updated = Entry(info, analysis);
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
        }
        if (changes.Count == 0) return true;

        var next = new Dictionary<string, CachedSourceAnalysis>(previous.Count + changes.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in previous) next[pair.Key] = pair.Value;
        foreach (var change in changes)
        {
            if (change.Value is null) next.Remove(change.Key);
            else next[change.Key] = change.Value;
        }
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 그 사이 분석 패스가 돌았으면 공개 내용이 바뀌었으므로 전체 다시 수집에 맡깁니다.
            if (!ReferenceEquals(published, previous) || !string.Equals(completedSolution, solutionPath, StringComparison.OrdinalIgnoreCase)) return false;
        }
        PublishEntries(solutionPath, next, cleared, cancellationToken);
        lock (gate)
        {
            // Clear가 비웠으면 다음 수집이 다시 시작합니다.
            if (!ReferenceEquals(published, next)) return true;
            cachedSolution = solutionPath;
            loadedCache = next;
            unsavedUpdates = true;
        }
        return true;
    }

    /// <summary>
    /// 저장된 분석을 이름 인덱스에 공개합니다. 다시 열 때는 수집을 시작하자마자 전체를 한 번 공개하고(<paramref name="allowedFiles"/> 없음),
    /// 수집이 끝나면 그 파일 목록으로 다시 불러 목록 밖 파일의 위치만 뺍니다. 이미 공개한 분석은 다시 만들지 않습니다.
    /// </summary>
    /// <remarks>
    /// 0.46.3까지는 수집 묶음마다 확인된 파일의 저장 심볼을 덧붙이고(묶음 병합), 수집이 끝나면 전체를 다시 만들고, 분석 패스 끝에 또 다시
    /// 만들었습니다. 엔진 규모 다시 열기에서 공개 10회·전체 재구성 2회(각 5~7초)였습니다. 저장된 분석은 끝까지 마친 패스의 결과이므로 목록
    /// 확인 전에 공개해도 지난 세션의 Solution 전체이고, 그 뒤 빠진 파일은 수집이 끝나면 뺍니다.
    /// </remarks>
    public void LoadCachedSymbols(string solutionPath, CancellationToken cancellationToken, IReadOnlyList<string>? allowedFiles = null)
    {
        CancellationToken cleared;
        lock (gate) cleared = clearing.Token;
        using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cleared);
        cancellationToken = link.Token;
        cancellationToken.ThrowIfCancellationRequested();
        var cached = LoadPrevious(solutionPath, cancellationToken);
        IReadOnlyDictionary<string, CachedSourceAnalysis>? before;
        lock (gate)
        {
            completedSolution = null;
            before = string.Equals(publishedSolution, solutionPath, StringComparison.OrdinalIgnoreCase) ? published : null;
        }
        var source = before ?? cached;
        var target = source;
        if (allowedFiles is not null)
        {
            var allowed = new HashSet<string>(allowedFiles, StringComparer.OrdinalIgnoreCase);
            var kept = new Dictionary<string, CachedSourceAnalysis>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (allowed.Contains(pair.Key)) kept[pair.Key] = pair.Value;
            }
            if (kept.Count != source.Count) target = kept;
        }
        if (before is null || !ReferenceEquals(target, before)) PublishEntries(solutionPath, target, cleared, cancellationToken);
        lock (gate) if (!cleared.IsCancellationRequested) cachedSymbolsPublished = cached.Count > 0;
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
            // 잠금 밖에서 만들던 공개를 막습니다. 취소한 토큰은 진행 중인 작업이 아직 쥐고 있을 수 있어 Dispose하지 않습니다.
            clearing.Cancel();
            clearing = new CancellationTokenSource();
            published = null;
            publishedSolution = null;
            cachedSymbolsPublished = false;
            completedSolution = null;
            symbols.ReplaceAll(Array.Empty<SourceSymbolLocation>());
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

    /// <summary>이 Solution의 저장된 분석을 지우고 메모리에 둔 지난 분석도 놓습니다(인덱스 다시 만들기). 파일을 지우지 못했으면 false입니다.</summary>
    public bool DeleteCache(string solutionPath)
    {
        ReleasePreviousAnalysis();
        return cache.Delete(solutionPath);
    }

    public void Dispose() => symbols.Dispose();

    /// <summary>
    /// 이름 인덱스를 <paramref name="target"/>의 위치로 맞춥니다. 같은 Solution의 공개 내용을 알면 바뀐 파일의 위치만 숨기고 더하고(차이가 크면
    /// <see cref="SourceSymbolIndex.Update"/>가 다시 만듦), 모르면 다시 만듭니다. 호출자들은 서비스가 한 번에 하나씩 돌립니다.
    /// </summary>
    private void PublishEntries(string solutionPath, IReadOnlyDictionary<string, CachedSourceAnalysis> target, CancellationToken cleared,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, CachedSourceAnalysis>? before;
        lock (gate) before = string.Equals(publishedSolution, solutionPath, StringComparison.OrdinalIgnoreCase) ? published : null;
        if (before is null) symbols.ReplaceAll(target.Values.SelectMany(entry => entry.Analysis.Symbols), cancellationToken);
        else
        {
            var removedFiles = new List<CachedSourceAnalysis>();
            var addedFiles = new List<CachedSourceAnalysis>();
            var removedCount = 0;
            var addedCount = 0;
            foreach (var pair in before)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (target.TryGetValue(pair.Key, out var now) && ReferenceEquals(now, pair.Value)) continue;
                removedFiles.Add(pair.Value);
                removedCount += pair.Value.Analysis.Symbols.Count;
            }
            foreach (var pair in target)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (before.TryGetValue(pair.Key, out var old) && ReferenceEquals(old, pair.Value)) continue;
                addedFiles.Add(pair.Value);
                addedCount += pair.Value.Analysis.Symbols.Count;
            }
            // 다시 만들 수도 있으므로(엔진 규모 약 6초) 잠금 밖에서 합니다. Solution을 닫는 Clear는 이 잠금을 기다리지 않고 취소로 막습니다.
            // 이름 인덱스가 어차피 다시 만들 만큼 바뀌었으면 숨길 위치를 모아 거르지 않고 공개할 전체로 바로 만듭니다(2026-10-10 검토 89).
            // 공개 내용은 before와 같으므로(이 메서드만 바꾸고 기록함) 결과도 같습니다.
            if (symbols.UpdateRebuilds(removedCount, addedCount))
            {
                symbols.ReplaceAll(target.Values.SelectMany(entry => entry.Analysis.Symbols), cancellationToken);
            }
            else
            {
                symbols.Update(removedFiles.SelectMany(entry => entry.Analysis.Symbols).ToArray(),
                    addedFiles.SelectMany(entry => entry.Analysis.Symbols).ToArray(), cancellationToken);
            }
        }
        // 이름 인덱스를 바꾼 뒤에는 호출자가 취소돼도 공개 내용을 기록해야 다음 비교가 맞습니다(이름 인덱스는 취소하면 아무것도 바꾸지 않음).
        // 그 사이 Clear가 비웠으면 기록하지 않습니다.
        lock (gate)
        {
            if (cleared.IsCancellationRequested) return;
            published = target;
            publishedSolution = solutionPath;
        }
    }

    private IReadOnlyDictionary<string, CachedSourceAnalysis> LoadPrevious(string solutionPath, CancellationToken cancellationToken)
    {
        // 동일 분석 패스의 선공개와 본 분석에서 큰 캐시 파일을 두 번 역직렬화하지 않습니다. Solution 닫기(ReleasePreviousAnalysis)가 다른 스레드에서
        // 놓으므로 확인과 읽기를 한 잠금에서 하고, 닫은 뒤 끝난 읽기는 기록하지 않습니다(2026-10-10 검토 90).
        lock (gate)
        {
            if (loadedCache is not null && string.Equals(cachedSolution, solutionPath, StringComparison.OrdinalIgnoreCase)) return loadedCache;
        }
        var loaded = cache.Load(solutionPath, cancellationToken);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            loadedCache = loaded;
            cachedSolution = solutionPath;
        }
        return loaded;
    }

    /// <summary>
    /// 파일 하나의 분석 결과를 보관용으로 만듭니다. include 목록은 버립니다. 이름 인덱스는 쓰지 않고, 정의·참조 탐색은 clangd 색인 파일의
    /// include 기록을 씁니다. 예전에는 분석 패스마다 모든 include를 경로로 풀어(엔진 규모 약 35만 개, 다시 열 때마다 약 6초) 인덱스 상태 창의
    /// 개수 표시에만 썼습니다.
    /// </summary>
    private static CachedSourceAnalysis Entry(FileInfo info, SourceFileAnalysis analysis) =>
        new(info.Length, info.LastWriteTimeUtc.Ticks,
            new SourceFileAnalysis(analysis.Path, Array.Empty<SourceIncludeReference>(), analysis.Symbols));

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
