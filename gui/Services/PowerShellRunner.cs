using System.Collections.Concurrent;
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
/// 同步阻塞调用，供后台任务使用（配合 Task.Run 实现异步）。
/// 脚本以子进程身份继承 GUI 的管理员令牌：GUI 未提权时脚本也不会提权
/// （Windows 不允许从非提权进程重定向输出的同时提权子进程），
/// 因此 GUI 在 Program.Main 中强制提权后才会走到这里。
/// 宿主程序可通过 <paramref name="hostPath"/> 指定（设置页「脚本执行器」），
/// 无效或未指定时使用系统 powershell.exe。
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
    /// 解析实际使用的宿主：hostPath 有效则用之，否则回退系统 powershell.exe。
    /// </summary>
    public static string ResolveHost(string? hostPath)
    {
        return !string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath)
            ? hostPath
            : FindPowerShell();
    }

    /// <summary>
    /// 同步执行。每行输出（含结尾空行）都会经 onLine 回调；超时或取消时终止进程树并抛 TimeoutException / OperationCanceledException。
    /// </summary>
    public static PowerShellResult Run(
        string scriptPath,
        IReadOnlyDictionary<string, string?> arguments,
        Action<string>? onLine = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        string? hostPath = null)
    {
        var argumentsText = BuildArguments(arguments);
        var psi = new ProcessStartInfo
        {
            FileName = ResolveHost(hostPath),
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {argumentsText}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };

        // 输出回调在 .NET 的线程池线程上投递；Run 返回前必须确认所有回调都已跑完，
        // 否则调用方拿到的 Lines 会缺最后一两行 —— 实测（60 轮，脚本只输出末尾一行
        // 后立即 exit 0）约 1/3 的轮次丢掉那唯一一行，表现为「exit 0 但 (no output)」，
        // 正是 mumu.ps1 ensure 输出 READY 的致命形态。
        // 计数 + 门闩：每个输出流结束时投递一条 null（EOF 哨兵），两个流都到齐才放行。
        var lines = new ConcurrentQueue<string>();
        var streamsDone = 0;
        using var drained = new ManualResetEventSlim(false);
        var streamsTotal = 2; // stdout + stderr

        void OnStreamLine(string? data)
        {
            if (data is null)
            {
                // EOF 哨兵：该流已经读干净，不再有后续回调。
                if (Interlocked.Increment(ref streamsDone) >= streamsTotal)
                {
                    drained.Set();
                }

                return;
            }

            lines.Enqueue(data);
            onLine?.Invoke(data);
        }

        process.OutputDataReceived += (_, e) => OnStreamLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnStreamLine(e.Data);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start powershell.exe");
            }

            ProcessRegistry.Register(process);
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

                throw new TimeoutException($"PowerShell script timed out (>{effectiveTimeout.TotalSeconds:0}s): {Path.GetFileName(scriptPath)}");
            }

            // 有界等待异步输出排干：正常情况是进程退出后回调很快跑完。能走到这里说明
            // 主进程一定是自行退出的（超时路径在上面已抛 TimeoutException），EOF 仍未
            // 到达只可能是残留孙进程持有管道写端。此时不再强杀进程树——可能误杀仍在
            // 干活的合法后台进程（如收尾中的 adb / MuMuManager）；只再多等 2s，然后带
            // 部分输出返回：行数可能不全，但比无限等待或误杀都好。
            if (!drained.Wait(TimeSpan.FromSeconds(3)))
            {
                drained.Wait(TimeSpan.FromSeconds(2));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return new PowerShellResult(process.ExitCode, lines.ToArray());
        }
        finally
        {
            ProcessRegistry.Unregister(process);
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
