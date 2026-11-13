using System.Diagnostics;
using System.Text;

namespace MafMotionEmulator.Gui.Services;

public sealed class LogLineEventArgs : EventArgs
{
    public LogLineEventArgs(string line, bool isError)
    {
        Line = line;
        IsError = isError;
    }

    public string Line { get; }

    public bool IsError { get; }
}

/// <summary>
/// 启动 <c>py tools/e2e.py [学号]</c> 并流式读取 stdout/stderr。
/// 事件在进程的 I/O 线程上触发，调用方需自行 Invoke 到 UI 线程。
/// </summary>
public sealed class RunnerService : IDisposable
{
    // 把 "py" 这类启动器解析成真正的 python.exe 并缓存：
    // py.exe 只负责转发，启动后立即退出 —— 若把它当作被跟踪进程，注册表会在运行开始
    // 一秒内变空（点「停止」只打印 Nothing is running），强杀也已找不到树根，
    // 子进程 e2e/run_task 会变成孤儿继续操作模拟器（实测 2026-11-12）。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ResolvedLaunchers = new();

    private Process? _process;

    public event EventHandler<LogLineEventArgs>? LineReceived;

    public event EventHandler<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(
        string pythonLauncher,
        string projectRoot,
        string? studentId,
        IReadOnlyDictionary<string, string>? extraEnv,
        string? extraArgs = null)
    {
        if (IsRunning)
        {
            return;
        }

        var arguments = string.IsNullOrEmpty(studentId) ? "tools/e2e.py" : $"tools/e2e.py {studentId}";
        if (!string.IsNullOrWhiteSpace(extraArgs))
        {
            arguments += " " + extraArgs;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveLauncher(pythonLauncher),
            Arguments = arguments,
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (extraEnv is not null)
        {
            foreach (var pair in extraEnv)
            {
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;
        process.Exited += OnProcessExited;

        try
        {
            if (!process.Start())
            {
                RaiseLine("Failed to start process (python launcher may be missing).", isError: true);
                Exited?.Invoke(this, -1);
                return;
            }
        }
        catch (Exception ex)
        {
                RaiseLine($"Failed to start: {ex.Message}", isError: true);
            Exited?.Invoke(this, -1);
            return;
        }

        _process = process;
        ProcessRegistry.Register(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public void Stop()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能已退出，忽略。
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is not null)
        {
            RaiseLine(e.Data, isError: false);
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is not null)
        {
            RaiseLine(e.Data, isError: true);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is Process process)
        {
            ProcessRegistry.Unregister(process);
            int code;
            try
            {
                code = process.ExitCode;
            }
            catch
            {
                code = -1;
            }

            Exited?.Invoke(this, code);
        }
    }

    private void RaiseLine(string line, bool isError)
    {
        LineReceived?.Invoke(this, new LogLineEventArgs(line, isError));
    }

    public void Dispose()
    {
        Stop();
        var process = _process;
        if (process is not null)
        {
            ProcessRegistry.Unregister(process);
            process.Dispose();
            _process = null;
        }
    }

    /// <summary>把启动器（py / python / 绝对路径）解析成可持续跟踪的真实解释器路径。</summary>
    private static string ResolveLauncher(string launcher)
    {
        if (string.IsNullOrWhiteSpace(launcher))
        {
            return "python";
        }

        return ResolvedLaunchers.GetOrAdd(launcher, raw =>
        {
            try
            {
                var probe = new ProcessStartInfo(raw, "-c \"import sys;print(sys.executable)\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var process = Process.Start(probe);
                if (process is null)
                {
                    return raw;
                }

                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                return process.ExitCode == 0 && File.Exists(output) ? output : raw;
            }
            catch
            {
                // 解析失败就按原样启动：行为退回旧逻辑，不影响功能。
                return raw;
            }
        });
    }
}
