using System;
using System.IO;
using System.Security;

namespace VisualBoost.Services;

/// <summary>
/// Solution별 캐시 파일(<c>&lt;해시&gt;.bin</c>)을 정리합니다. 캐시는 고유 임시 파일(<c>&lt;캐시 파일&gt;.&lt;GUID&gt;.tmp</c>)에 쓴 뒤 바꿔 끼우는데,
/// 쓰는 도중 VS가 끝나면 임시 파일이 남습니다(이름 분석 캐시는 엔진 규모에서 약 300 MB).
/// </summary>
internal static class CacheFiles
{
    /// <summary>
    /// 이 캐시 파일의 남은 임시 파일을 지웁니다. 다른 VS 인스턴스가 쓰는 중인 임시 파일은 공유 없이 열려 있어 지워지지 않으므로 건너뜁니다.
    /// </summary>
    public static void DeleteTemporaries(string cachePath)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
        string[] leftovers;
        try
        {
            leftovers = Directory.GetFiles(directory, Path.GetFileName(cachePath) + ".*.tmp");
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
        {
            return;
        }

        foreach (var leftover in leftovers) TryDelete(leftover);
    }

    /// <summary>캐시 파일과 남은 임시 파일을 지웁니다(인덱스 다시 만들기). 캐시 파일을 지우지 못했으면 false입니다.</summary>
    public static bool Delete(string cachePath)
    {
        DeleteTemporaries(cachePath);
        return !File.Exists(cachePath) || TryDelete(cachePath);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is SecurityException)
        {
            // 다른 VS가 쓰는 중이거나 권한이 없는 파일입니다. 다음 저장·초기화에서 다시 시도합니다.
            return false;
        }
    }
}
