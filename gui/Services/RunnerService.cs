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
    private Process? _process;

    public event EventHandler<LogLineEventArgs>? LineReceived;

    public event EventHandler<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(
        string pythonLauncher,
        string projectRoot,
        string? studentId,
        IReadOnlyDictionary<string, string>? extraEnv)
    {
        if (IsRunning)
        {
            return;
        }

        var arguments = string.IsNullOrEmpty(studentId) ? "tools/e2e.py" : $"tools/e2e.py {studentId}";

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonLauncher,
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
                RaiseLine("无法启动进程（python 启动器可能不存在）。", isError: true);
                Exited?.Invoke(this, -1);
                return;
            }
        }
        catch (Exception ex)
        {
            RaiseLine($"启动失败：{ex.Message}", isError: true);
            Exited?.Invoke(this, -1);
            return;
        }

        _process = process;
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
            process.Dispose();
            _process = null;
        }
    }
}
