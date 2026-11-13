using System.Diagnostics;

namespace MafMotionEmulator.Gui.Services;

public sealed record ScriptHost(string DisplayName, string Path)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// 扫描本机可运行 .ps1 脚本的 PowerShell 宿主：
/// Windows PowerShell 5.1（系统必带）+ PowerShell 7+（安装目录 / Store / PATH）。
/// 仅读取文件元数据，不启动进程。
/// </summary>
public static class ScriptHostScanner
{
    public static List<ScriptHost> Scan()
    {
        var hosts = new List<ScriptHost>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Windows PowerShell 5.1 —— System32，Windows 必带。
        var systemPs = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (AddIfFound(systemPs, seen, hosts, "Windows PowerShell")) { /* noop */ }

        // 2. PowerShell 7+：MSI 安装在 %ProgramFiles%\PowerShell\<版本>\pwsh.exe。
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installRoot = Path.Combine(programFiles, "PowerShell");
        if (Directory.Exists(installRoot))
        {
            foreach (var dir in Directory.GetDirectories(installRoot))
            {
                AddIfFound(Path.Combine(dir, "pwsh.exe"), seen, hosts, "PowerShell");
            }
        }

        // 3. Microsoft Store 版：WindowsApps 下的执行别名。
        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps");
        AddIfFound(Path.Combine(windowsApps, "pwsh.exe"), seen, hosts, "PowerShell");

        // 4. PATH 上的 pwsh.exe（ scoop / 自定义安装等）。
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (dir.Contains('%'))
            {
                continue; // 未展开的环境变量引用，跳过
            }

            AddIfFound(Path.Combine(dir, "pwsh.exe"), seen, hosts, "PowerShell");
        }

        return hosts;
    }

    private static bool AddIfFound(string fullPath, HashSet<string> seen, List<ScriptHost> hosts, string baseName)
    {
        if (!File.Exists(fullPath) || !seen.Add(fullPath))
        {
            return false;
        }

        var version = GetVersion(fullPath);
        var label = version is null
            ? $"{baseName} — {fullPath}"
            : $"{baseName} {version} — {fullPath}";
        hosts.Add(new ScriptHost(label, fullPath));
        return true;
    }

    private static string? GetVersion(string fullPath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(fullPath);
            return info.FileMajorPart > 0 ? $"{info.FileMajorPart}.{info.FileMinorPart}" : null;
        }
        catch
        {
            return null;
        }
    }
}
