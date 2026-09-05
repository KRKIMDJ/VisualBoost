using System;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Completion;

// 뷰 생성 경로에서 패키지를 동기 로드하지 않고 현재 솔루션만 연결합니다.
internal static class CompletionRuntime
{
    internal static Func<SymbolCompletionSnapshot>? GetSnapshot;
    internal static bool Enabled = true;
    internal static int DelayMilliseconds = 250;
    internal static int MinimumLength = 3;
}
