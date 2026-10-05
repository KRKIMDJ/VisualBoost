using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>빌드 도구(MSBuild, Ninja)를 실행한 결과입니다.</summary>
internal sealed class ToolRunResult
{
    public ToolRunResult(string output, string? error)
    {
        Output = output;
        Error = error;
    }

    /// <summary>표준 출력 전체입니다. 실패했거나 상한을 넘으면 비어 있을 수 있습니다.</summary>
    public string Output { get; }

    /// <summary>실패 이유입니다. 성공하면 null입니다.</summary>
    public string? Error { get; }
}

/// <summary>
/// 컴파일 명령을 얻는 외부 도구를 창 없이 실행합니다. 출력이 쌓여 파이프가 막히지 않게 비동기로 비우고,
/// 시간 초과·취소 시 자신이 시작한 프로세스만 끝냅니다.
/// </summary>
internal static class ToolProcess
{
    public static ToolRunResult Run(string name, string executable, string arguments, string workDirectory, TimeSpan timeout, int maxOutputChars,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var truncated = false;
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output)
            {
                if (output.Length + e.Data.Length < maxOutputChars) output.AppendLine(e.Data);
                else truncated = true;
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            lock (errors)
            {
                if (errors.Length < 4000) errors.AppendLine(e.Data);
            }
        };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return new ToolRunResult(string.Empty, $"{name}를 시작하지 못했습니다: {exception.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using (cancellationToken.Register(() => Kill(process)))
        {
            if (!process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds)))
            {
                Kill(process);
                process.WaitForExit();
                return new ToolRunResult(string.Empty, $"{name}가 {timeout.TotalSeconds:N0}초 안에 끝나지 않았습니다.");
            }

            // 인자 없는 WaitForExit가 비동기 출력 읽기의 끝까지 기다립니다.
            process.WaitForExit();
        }

        cancellationToken.ThrowIfCancellationRequested();
        string text;
        lock (output) text = output.ToString();
        if (truncated)
        {
            return new ToolRunResult(string.Empty, $"{name} 출력이 너무 큽니다({maxOutputChars:N0}자 초과).");
        }

        if (process.ExitCode == 0)
        {
            return new ToolRunResult(text, null);
        }

        string detail;
        lock (errors) detail = (errors.ToString() + text).Trim();
        if (detail.Length > 500) detail = detail.Substring(0, 500) + "…";
        return new ToolRunResult(text, $"{name} 종료 코드 {process.ExitCode}" + (detail.Length > 0 ? ": " + detail : string.Empty));
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException)
        {
            // 이미 끝났습니다.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 끝나는 중입니다.
        }
    }
}
