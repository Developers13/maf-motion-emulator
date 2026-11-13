using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace MafMotionEmulator.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // MuMu 管理 / adb 操作要求管理员权限；子进程（mumu.ps1 / adb-prep.ps1 等）
        // 继承本进程令牌，因此必须先确保 GUI 自身提权。
        if (!IsElevated())
        {
            RelaunchAsAdmin(); // 重启成功与否，本实例都不再继续：提权后由新实例接管
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(new System.Drawing.Font("Segoe UI", 9.75f));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowFatal(e.ExceptionObject as Exception);

        Application.Run(new MainForm());
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 以管理员身份重启自身（触发 UAC）。返回 false 表示用户取消授权或重启失败。
    /// </summary>
    private static bool RelaunchAsAdmin()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runAs",
            };

            // 以 `dotnet xxx.dll` 方式启动时，重启的是 dotnet.exe，需要带上原参数。
            if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1)
                {
                    psi.Arguments = string.Join(" ", args.Skip(1)
                        .Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                }
            }

            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            MessageBox.Show(
                "已取消管理员授权，程序将退出。\n\n" +
                "MuMu 虚拟机管理与 adb 操作需要管理员权限，请重新启动并允许授权。",
                "MAF Motion Emulator GUI",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }
    }

    private static void ShowFatal(Exception? ex)
    {
        try
        {
            MessageBox.Show(
                "发生未处理的异常：\n\n" + (ex?.ToString() ?? "未知错误"),
                "MAF Motion Emulator GUI",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // 兜底：异常提示本身失败时不再处理，避免递归。
        }
    }
}
