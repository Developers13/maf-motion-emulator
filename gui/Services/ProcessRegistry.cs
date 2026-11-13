using System.Collections.Concurrent;
using System.Diagnostics;

namespace MafMotionEmulator.Gui.Services;

/// <summary>
/// 登记所有由 GUI 启动的、正在运行的子进程（python e2e / powershell 脚本链），
/// 供「停止」按钮一次性向全部进程发送 Ctrl+C。线程安全。
/// </summary>
public static class ProcessRegistry
{
    private static readonly ConcurrentDictionary<int, Process> Running = new();

    public static void Register(Process process)
    {
        try
        {
            if (process.Id > 0)
            {
                Running.TryAdd(process.Id, process);
            }
        }
        catch
        {
            // 进程已退出导致 Id 不可用：无需登记。
        }
    }

    public static void Unregister(Process process)
    {
        try
        {
            if (process.Id > 0)
            {
                Running.TryRemove(process.Id, out _);
            }
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>是否有 GUI 启动的子进程仍在运行。</summary>
    public static bool AnyRunning => Running.Values.Any(IsAlive);

    private static bool IsAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch
        {
            return false; // 进程对象已失效（如被 Dispose）。
        }
    }

    /// <summary>
    /// 立即向所有存活子进程发送 Ctrl+C（尽力而为：powershell 宿主可能忽略信号，
    /// 故不能依赖它），不阻塞调用线程；后台等待 graceMs 后对仍存活者
    /// Kill(entireProcessTree) 并经 log 回调记录，保证停止一定生效。
    /// 返回成功送达 Ctrl+C 的进程数。
    /// </summary>
    public static int StopAll(int graceMs = 2000, Action<string>? log = null)
    {
        var targets = Running.Values.Where(IsAlive).ToList();
        var sent = 0;

        foreach (var process in targets)
        {
            try
            {
                if (ConsoleCtrl.TrySendCtrlC(process.Id))
                {
                    sent++;
                }
                else
                {
                    process.Kill(entireProcessTree: true);
                    log?.Invoke($"Ctrl+C unavailable (pid {process.Id}); force-killed process tree.");
                }
            }
            catch
            {
                // 进程恰好退出，忽略。
            }
        }

        if (targets.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(graceMs).ConfigureAwait(false);
                foreach (var process in targets)
                {
                    if (IsAlive(process))
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                            log?.Invoke($"Force-terminated process tree (pid {process.Id}) after {graceMs}ms grace.");
                        }
                        catch
                        {
                            // 进程在宽限期内自行退出。
                        }
                    }
                }
            });
        }

        return sent;
    }

    /// <summary>强制结束全部登记的子进程（应用退出时用）。</summary>
    public static void KillAll()
    {
        foreach (var process in Running.Values)
        {
            if (!IsAlive(process))
            {
                continue;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已退出或已失效。
            }
        }

        Running.Clear();
    }
}
