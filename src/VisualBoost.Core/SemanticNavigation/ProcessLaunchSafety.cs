using System;
using System.Collections.Generic;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 외부 도구를 시작할 때, 열어 본 저장소에서 읽은 값(빌드 응답 파일의 인자 등)이 VisualBoost가 고르지 않은 실행 파일·DLL을 올리지 못하게
/// 하는 검사입니다. 저장소의 파일은 신뢰할 수 없는 입력으로 봅니다.
/// </summary>
public static class ProcessLaunchSafety
{
    /// <summary>
    /// 드라이브 문자(<c>C:\</c>)나 UNC(<c>\\서버\공유</c>)로 시작하는 절대 경로인지 봅니다. 상대 경로와 현재 드라이브·폴더 기준 경로
    /// (<c>\a</c>, <c>C:a</c>)는 VS의 현재 폴더에 따라 다른 파일을 가리키고, 실행 파일 이름만 주면 Windows가 현재 폴더를 시스템 폴더보다
    /// 먼저 찾으므로 실행할 경로로 쓰지 않습니다.
    /// </summary>
    public static bool IsFullyQualified(string? path)
    {
        if (path is not { Length: >= 2 } value) return false;
        if (IsSeparator(value[0]) && IsSeparator(value[1])) return true;
        return value.Length >= 3 && IsDriveLetter(value[0]) && value[1] == ':' && IsSeparator(value[2]);
    }

    /// <summary>
    /// 컴파일러 인자에서 컴파일러 프로세스 안으로 다른 코드나 옵션을 더 불러오는 옵션을 뺍니다: clang 플러그인(<c>-fplugin…</c>, cc1의
    /// <c>-load</c>·<c>-plugin</c>·<c>-add-plugin</c>·<c>-plugin-arg-…</c>), LLVM pass 플러그인(<c>-fpass-plugin…</c>), 설정 파일
    /// (<c>--config…</c>)과 응답 파일(<c>@파일</c>)입니다. 각 옵션은 그대로, clang-cl의 <c>/clang:</c> 접두사로, cc1 전달
    /// (<c>-Xclang 값</c>)로 올 수 있어 모두 봅니다. 실제 빌드가 만든 명령에는 쓰이지 않는 옵션이라 정상 명령은 그대로 돌려줍니다.
    /// </summary>
    /// <remarks>
    /// clangd는 컴파일 명령의 플러그인을 스스로 끄지만, 분석 오류 원인을 보는 clang-cl(<see cref="CompilerProbe"/>)은 실제 컴파일러라
    /// 옵션대로 DLL을 올립니다. 그 인자는 프로젝트의 빌드 응답 파일에서 오므로, 받은 저장소에 넣어 둔 응답 파일만으로 임의 코드가 실행되지 않게
    /// 실행 직전에 거릅니다.
    /// </remarks>
    public static IReadOnlyList<string> WithoutCodeLoadingOptions(IReadOnlyList<string> arguments)
    {
        // -Xclang과 그 값처럼 두 토큰이 옵션 하나인 경우를 묶어서 봅니다.
        var options = new List<(int Start, int Count, string Text, bool Frontend)>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            var token = WithoutClangPrefix(arguments[i]);
            if (token == "-Xclang" && i + 1 < arguments.Count)
            {
                options.Add((i, 2, WithoutClangPrefix(arguments[i + 1]), true));
                i++;
            }
            else if (token.StartsWith("-Xclang=", StringComparison.Ordinal))
            {
                options.Add((i, 1, token.Substring("-Xclang=".Length), true));
            }
            else
            {
                options.Add((i, 1, token, false));
            }
        }

        var dropped = new bool[arguments.Count];
        var any = false;
        for (var o = 0; o < options.Count; o++)
        {
            var (start, count, text, frontend) = options[o];
            // 값을 다음 옵션으로 받는 형태입니다. cc1 옵션 -load 등은 드라이버에서 다른 뜻(-l)이므로 -Xclang으로 전달된 것만 봅니다.
            var takesValue = text == "--config" ||
                             frontend && (text is "-load" or "-plugin" or "-add-plugin" || text.StartsWith("-plugin-arg-", StringComparison.Ordinal));
            if (!takesValue && !text.StartsWith("@", StringComparison.Ordinal) && !text.StartsWith("-fplugin", StringComparison.Ordinal) &&
                !text.StartsWith("-fpass-plugin", StringComparison.Ordinal) && !text.StartsWith("--config", StringComparison.Ordinal))
            {
                continue;
            }

            any = true;
            for (var k = 0; k < count; k++) dropped[start + k] = true;
            if (takesValue && o + 1 < options.Count)
            {
                o++;
                for (var k = 0; k < options[o].Count; k++) dropped[options[o].Start + k] = true;
            }
        }

        if (!any) return arguments;
        var result = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (!dropped[i]) result.Add(arguments[i]);
        }

        return result;
    }

    /// <summary>clang-cl이 clang 드라이버에 그대로 넘기는 <c>/clang:</c>(또는 <c>-clang:</c>) 접두사를 뗍니다.</summary>
    private static string WithoutClangPrefix(string token) =>
        token.Length > 7 && (token[0] == '/' || token[0] == '-') && string.Compare(token, 1, "clang:", 0, 6, StringComparison.OrdinalIgnoreCase) == 0
            ? token.Substring(7)
            : token;

    private static bool IsSeparator(char c) => c == '\\' || c == '/';

    private static bool IsDriveLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
