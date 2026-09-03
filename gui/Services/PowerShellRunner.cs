using System.Diagnostics;
using System.Text;

namespace MafMotionEmulator.Gui.Services;

public sealed class PowerShellResult
{
    public PowerShellResult(int exitCode, IReadOnlyList<string> lines)
    {
        ExitCode = exitCode;
        Lines = lines;
    }

    public int ExitCode { get; }

    public IReadOnlyList<string> Lines { get; }

    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// 运行 PowerShell 脚本（mumu.ps1 / adb-prep.ps1 等），UTF-8 读取输出，可超时。
/// 同步阻塞调用，供后台任务使用；需逐行实时回调时用 <see cref="RunAsync"/>。
/// </summary>
public static class PowerShellRunner
{
    public static string FindPowerShell()
    {
        foreach (var candidate in new[]
        {
            Environment.GetEnvironmentVariable("SystemRoot"),
            @"C:\Windows",
        })
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            var path = Path.Combine(candidate, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return "powershell.exe"; // 兜底：交给 PATH 解析
    }

    /// <summary>
    /// 同步执行。每行输出（含结尾空行）都会经 onLine 回调；超时或取消时终止进程树并抛 TimeoutException / OperationCanceledException。
    /// </summary>
    public static PowerShellResult Run(
        string scriptPath,
        IReadOnlyDictionary<string, string?> arguments,
        Action<string>? onLine = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var argumentsText = BuildArguments(arguments);
        var psi = new ProcessStartInfo
        {
            FileName = FindPowerShell(),
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {argumentsText}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        var lines = new List<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lines.Add(e.Data);
                onLine?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lines.Add(e.Data);
                onLine?.Invoke(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 powershell.exe");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var effectiveTimeout = timeout ?? Timeout.InfiniteTimeSpan;
            if (!process.WaitForExit((int)effectiveTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程可能已退出，忽略。
                }

                throw new TimeoutException($"PowerShell 脚本执行超时（>{effectiveTimeout.TotalSeconds:0}s）：{Path.GetFileName(scriptPath)}");
            }

            process.WaitForExit(); // 确保异步读完成

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return new PowerShellResult(process.ExitCode, lines);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string BuildArguments(IReadOnlyDictionary<string, string?> arguments)
    {
        var parts = new List<string>();
        foreach (var pair in arguments)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            var value = pair.Value ?? "";
            parts.Add($"-{pair.Key} \"{value.Replace("\"", "`\"")}\"");
        }

        return string.Join(" ", parts);
    }
}
