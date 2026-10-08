using System;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>clangd 메모리 정리 판단에 쓰는 한 시점의 상태입니다.</summary>
public readonly struct ClangdMemorySample
{
    public ClangdMemorySample(long privateBytes, bool indexing, bool busy, TimeSpan sinceLastRequest, TimeSpan sinceStart)
    {
        PrivateBytes = privateBytes;
        Indexing = indexing;
        Busy = busy;
        SinceLastRequest = sinceLastRequest;
        SinceStart = sinceStart;
    }

    /// <summary>clangd 프로세스의 private 메모리입니다.</summary>
    public long PrivateBytes { get; }

    /// <summary>background index가 진행 중입니다.</summary>
    public bool Indexing { get; }

    /// <summary>탐색 요청이나 저장 반영이 진행 중입니다.</summary>
    public bool Busy { get; }

    /// <summary>마지막 탐색 요청 뒤 지난 시간입니다.</summary>
    public TimeSpan SinceLastRequest { get; }

    /// <summary>이 clangd를 시작한 뒤 지난 시간입니다.</summary>
    public TimeSpan SinceStart { get; }
}

/// <summary>
/// clangd를 다시 시작해 해제된 메모리를 돌려받을지 정합니다. 상태는 Solution 하나의 수명 동안 유지하며 스레드 안전하지 않습니다.
/// </summary>
/// <remarks>
/// Windows의 clangd는 색인·문서 분석에 쓴 메모리를 해제한 뒤에도 프로세스에 남깁니다. 테스트 전용 UE 5.8 샘플(TU 305개, -j=8)에서
/// 첫 색인 뒤 private 3.8 GB(clangd가 집계한 사용량은 189 MB), 문서 8개를 연 뒤 6.6 GB였고, 같은 캐시로 다시 시작하면 1.3초 만에
/// 560 MB가 되었습니다(2026-10-08 측정, 회사 사용 피드백: 2~8 GB 장기 점유). clangd에는 메모리를 돌려주는 옵션이 없어(Windows판에
/// <c>--malloc-trim</c> 없음) 다시 시작이 유일한 방법입니다.
/// 다시 시작하면 열린 문서의 분석이 사라지고 색인을 다시 읽는 동안 결과가 불완전하므로, 색인 중·요청 중이 아니고 일정 시간 탐색 요청이
/// 없을 때만 합니다. 다시 시작한 뒤에도 상한을 넘는 큰 프로젝트에서 되풀이하지 않도록 상한을 그 사용량의 1.5배로 올립니다.
/// </remarks>
public sealed class ClangdMemoryPolicy
{
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan DefaultMinimumUptime = TimeSpan.FromMinutes(5);

    // 다시 시작한 뒤 색인을 읽고 안정될 때까지 기다렸다가 기준 사용량을 잽니다.
    public static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(1);

    private bool measuring;

    /// <param name="limitBytes">이 값을 넘으면 정리합니다. 0 이하면 정리하지 않습니다.</param>
    public ClangdMemoryPolicy(long limitBytes, TimeSpan? idle = null, TimeSpan? minimumUptime = null)
    {
        LimitBytes = limitBytes;
        EffectiveLimitBytes = limitBytes;
        Idle = idle ?? DefaultIdle;
        MinimumUptime = minimumUptime ?? DefaultMinimumUptime;
    }

    public long LimitBytes { get; }

    /// <summary>다시 시작한 뒤의 사용량으로 조정한 실제 상한입니다.</summary>
    public long EffectiveLimitBytes { get; private set; }

    public TimeSpan Idle { get; }

    public TimeSpan MinimumUptime { get; }

    /// <summary>표본으로 다시 시작할지 정합니다. true를 돌려주면 호출자는 다시 시작해야 합니다.</summary>
    public bool ShouldRestart(ClangdMemorySample sample)
    {
        if (LimitBytes <= 0 || sample.PrivateBytes <= 0)
        {
            return false;
        }

        if (measuring)
        {
            // 정리 뒤 첫 안정 표본: 색인을 다시 읽은 기준 사용량입니다.
            if (sample.Indexing || sample.SinceStart < SettleTime) return false;
            measuring = false;
            if (sample.PrivateBytes * 4 > EffectiveLimitBytes * 3)
            {
                EffectiveLimitBytes = Math.Max(EffectiveLimitBytes, sample.PrivateBytes + sample.PrivateBytes / 2);
            }

            return false;
        }

        if (sample.Indexing || sample.Busy || sample.SinceLastRequest < Idle || sample.SinceStart < MinimumUptime ||
            sample.PrivateBytes <= EffectiveLimitBytes)
        {
            return false;
        }

        measuring = true;
        return true;
    }

    /// <summary>색인 결과를 다시 읽으려고 다시 시작하기 전에 기다리는 요청 없는 시간입니다.</summary>
    public static readonly TimeSpan ReloadIdle = TimeSpan.FromSeconds(10);

    /// <summary>한 Solution을 연 동안 색인 결과를 다시 읽으려고 다시 시작하는 최대 횟수입니다. 색인 파일을 남기지 못하는 경우의 반복을 막습니다.</summary>
    public const int MaxReloads = 3;

    /// <summary>
    /// 색인 결과를 다시 읽으려고(<see cref="ClangdNavigator.NeedsReload"/>) 지금 다시 시작할지 정합니다. 결과가 틀린 채 남지 않도록 메모리 기준과
    /// 상관없이, 색인·요청이 멈추고 <see cref="ReloadIdle"/>이 지나면 다시 시작합니다.
    /// </summary>
    public static bool ShouldReload(bool needsReload, ClangdMemorySample sample, int reloads) =>
        needsReload && reloads < MaxReloads && !sample.Indexing && !sample.Busy && sample.SinceLastRequest >= ReloadIdle;

    /// <summary>색인 뒤 다시 시작해 돌려받을 만큼 남은 메모리의 하한입니다.</summary>
    public const long ReclaimFloorBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// 색인한 세션이 끝난 뒤 남은 메모리를 돌려받으려고 다시 시작할지 정합니다. 정리 기준과 상관없이, 이 세션에서 TU를 색인했고
    /// 색인·요청이 <see cref="DefaultIdle"/> 동안 멈췄으며 메모리가 <see cref="ReclaimFloorBytes"/>를 넘으면 다시 시작합니다.
    /// </summary>
    /// <remarks>
    /// clangd는 색인 작업이 쓴 힙을 운영체제에 돌려주지 않아, 테스트 전용 UE 5.8 샘플의 첫 색인 직후 1.1 GB(작업 2개)~2.7 GB(작업 6개)가
    /// 남고 다시 시작하면 0.3 GB였습니다(2026-10-09). 정리 기준을 이 수준으로 낮추면 공유 PCH 문서 하나(약 2.1 GB)만 열어 둬도 쉴 때마다
    /// 다시 시작하므로, 기준은 그대로 두고 색인한 세션에만 한 번 다시 시작합니다. 다시 시작한 세션은 저장된 색인을 읽어 다시 색인하지 않습니다.
    /// </remarks>
    public static bool ShouldReclaimAfterIndex(int indexedUnits, ClangdMemorySample sample, int reloads) =>
        indexedUnits > 0 && reloads < MaxReloads && !sample.Indexing && !sample.Busy && sample.SinceLastRequest >= DefaultIdle &&
        sample.PrivateBytes > ReclaimFloorBytes;

    /// <summary>옵션 값(MB)을 상한으로 바꿉니다. 0 이하면 이 PC의 <see cref="DefaultLimitBytes"/>입니다.</summary>
    public static long ResolveLimitBytes(int megabytes) =>
        megabytes > 0 ? megabytes * 1024L * 1024 : DefaultLimitBytes(PhysicalMemory.TotalBytes());

    /// <summary>
    /// 기본 상한입니다. 물리 메모리의 1/8이되 2~8 GiB로 맞춥니다. 메모리를 모르면 3 GiB입니다.
    /// </summary>
    public static long DefaultLimitBytes(long physicalBytes)
    {
        const long gib = 1024L * 1024 * 1024;
        if (physicalBytes <= 0) return 3 * gib;
        return Math.Max(2 * gib, Math.Min(8 * gib, physicalBytes / 8));
    }
}
