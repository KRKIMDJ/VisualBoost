using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 분석 오류가 난 색인 단위를 clang-cl로 문법만 검사해(<c>/Zs</c>) 모르는 이름을 찾습니다(<see cref="IncludeSupplements.NamesInCompilerOutput"/>).
/// </summary>
/// <remarks>
/// background index의 실패 알림에는 원인이 없습니다. 합성 TU를 clangd 문서로 열면 구성원 cpp가 모두 맨 앞 include 묶음(preamble)에 들어가
/// 함수 본문을 건너뛰고, 다른 파일의 오류는 include 줄마다 첫 오류만 알립니다. 그래서 함수 본문의 불완전 타입(멤버 접근 등)을 보지 못해 헤더만
/// 넣으면 되는 단위도 PCH로 넘어갔습니다(실제 Unreal 프로젝트에서 그렇게 넘어간 단위 8개 모두 해당, 2026-10-09 측정). 컴파일러는 색인과 같은
/// 명령으로 모든 오류를 내고, 끝나면 쓴 메모리를 돌려줍니다(clangd는 돌려주지 않음). 명령이 길어 응답 파일로 넘기며, clangd가 무시하는
/// cl 전용 옵션을 clang-cl은 입력 파일로 보므로 그런 인자는 빼고 다시 실행합니다.
/// </remarks>
internal static class CompilerProbe
{
    /// <summary>출력을 모으는 상한(문자)입니다. 경고가 많아 단위 하나에 1 MB 안팎이며, 넘으면 그때까지의 오류 줄만 봅니다.</summary>
    private const int MaxOutputChars = 16 * 1024 * 1024;

    /// <summary>인식하지 못한 인자를 빼고 다시 실행하는 상한입니다.</summary>
    private const int MaxAttempts = 4;

    private static readonly Regex MissingInput = new(@"^clang-cl(?:\.exe)?: error: no such file or directory: '(.+)'\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// 명령의 파일을 검사해 모르는 이름을 돌려줍니다. 컴파일러가 없거나 시작하지 못했거나 시간 안에 끝나지 않으면 null입니다.
    /// 오류가 없거나 이름을 알 수 없는 오류뿐이면 빈 목록입니다. 작업 스레드에서 부릅니다.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> MissingNamesAsync(CompileCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // 이름만 있는 컴파일러("clang-cl.exe")는 Windows가 VS의 현재 폴더(열린 저장소일 수 있음)에서 먼저 찾으므로 절대 경로일 때만 실행합니다.
        if (command.Arguments.Count < 2 || !ProcessLaunchSafety.IsFullyQualified(command.Arguments[0]) || !File.Exists(command.Arguments[0])) return null;
        // clangd는 없는 명령 폴더도 받지만 프로세스는 그 폴더에서 시작할 수 없습니다. 상대 경로는 어느 쪽이든 찾지 못하므로 파일 폴더에서 돌립니다.
        var directory = Directory.Exists(command.Directory) ? command.Directory : Path.GetDirectoryName(command.File) ?? command.Directory;
        // 인자는 프로젝트의 빌드 응답 파일에서 오므로 컴파일러에 DLL·설정 파일을 올리게 하는 옵션을 빼고 실행합니다.
        var arguments = ProcessLaunchSafety.WithoutCodeLoadingOptions(command.Arguments.Skip(1).Where(a => a is not ("/c" or "-c")).ToList()).ToList();
        arguments.InsertRange(0, new[] { "/Zs", "/clang:-fno-caret-diagnostics", "/clang:-ferror-limit=0" });
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var output = await RunAsync(command.Arguments[0], arguments, directory, timeout, cancellationToken).ConfigureAwait(false);
            if (output is null) return null;
            // 옵션 모양의 인자만 뺍니다(검사할 파일이 없어 난 오류면 이름 없이 끝남).
            var unknown = MissingInput.Matches(output).Cast<Match>().Select(m => m.Groups[1].Value)
                .Where(a => (a.StartsWith("/", StringComparison.Ordinal) || a.StartsWith("-", StringComparison.Ordinal)) && arguments.Contains(a))
                .ToArray();
            if (unknown.Length == 0) return IncludeSupplements.NamesInCompilerOutput(output);
            arguments.RemoveAll(a => unknown.Contains(a));
        }

        return null;
    }

    private static async Task<string?> RunAsync(string compiler, IReadOnlyList<string> arguments, string directory, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var response = Path.Combine(Path.GetTempPath(), "VisualBoost.Probe." + Guid.NewGuid().ToString("N") + ".rsp");
        try
        {
            // clang-cl은 응답 파일의 줄 끝을 명령 끝으로 보므로(/link 처리) 한 줄에 씁니다.
            File.WriteAllText(response, string.Join(" ", arguments.Select(ClangdSession.QuoteArgument)), new UTF8Encoding(false));
            var info = new ProcessStartInfo(compiler, ClangdSession.QuoteArgument("@" + response))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // clangd와 같은 헤더를 보도록 개발자 프롬프트의 INCLUDE 등을 비웁니다.
            foreach (var name in ClangdSession.IsolatedEnvironment) info.EnvironmentVariables.Remove(name);
            var output = new StringBuilder();
            void Append(string? line)
            {
                if (line is null) return;
                lock (output)
                {
                    if (output.Length + line.Length < MaxOutputChars) output.Append(line).Append('\n');
                }
            }

            using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => exited.TrySetResult(true);
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            try
            {
                process.Start();
            }
            catch (Win32Exception)
            {
                return null;
            }

            try
            {
                // 색인처럼 사용자 작업보다 뒤에 돕니다.
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception exception) when (exception is Win32Exception || exception is InvalidOperationException)
            {
                // 벌써 끝났거나 바꿀 수 없으면 보통 우선순위로 둡니다.
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            using (limit.Token.Register(() => Kill(process)))
            {
                await exited.Task.ConfigureAwait(false);
            }

            // 인자 없는 WaitForExit가 비동기 출력 읽기의 끝까지 기다립니다.
            process.WaitForExit();
            if (limit.IsCancellationRequested) return null;
            lock (output) return output.ToString();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            TryDelete(response);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is Win32Exception)
        {
            // 이미 끝났거나 끝나는 중입니다.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // 임시 폴더의 작은 파일이라 남아도 됩니다.
        }
    }
}
