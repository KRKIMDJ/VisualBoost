using System;
using System.Collections.Generic;

namespace VisualBoost.Analysis;

/// <summary>
/// 같은 값의 문자열을 한 인스턴스로 모읍니다. 스레드 안전합니다. 분석 패스나 캐시 읽기 동안만 쓰고 버립니다.
/// </summary>
/// <remarks>
/// 이름 인덱스의 위치마다 이름·소속·시그니처 문자열을 따로 두면, 엔진 규모(위치 556만, 서로 다른 이름 164만)에서 관리 힙이 약 0.36 GB 더
/// 들었습니다(2026-10-10 측정). 풀을 계속 들고 있으면 풀 자체(약 240만 항목)가 남고 바뀐 파일의 옛 문자열도 붙잡으므로, 한 번에 많이 만드는
/// 구간에서만 씁니다. <see cref="string.Intern(string)"/>은 프로세스가 끝날 때까지 놓지 않아 쓰지 않습니다.
/// </remarks>
internal sealed class StringPool
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public string Intern(string value)
    {
        if (value.Length == 0) return string.Empty;
        lock (values)
        {
            if (values.TryGetValue(value, out var known)) return known;
            values[value] = value;
            return value;
        }
    }
}
