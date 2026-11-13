using System.Runtime.InteropServices;

namespace MafMotionEmulator.Gui.Services;

/// <summary>
/// 向控制台子进程发送 Ctrl+C（Windows 没有 POSIX 信号）。
/// GUI 进程自身没有控制台：先 AttachConsole 到目标进程的（隐藏）控制台，
/// 把自身 CTRL+C 处理器置为忽略，再向该控制台上的全部进程广播 CTRL_C，最后脱离。
/// 注意：不用 CTRL_BREAK —— 它无法被忽略，会连我们 GUI 自己一起终止。
/// </summary>
public static class ConsoleCtrl
{
    private const uint CtrlCEvent = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

    /// <summary>
    /// 尽力向目标进程所在控制台广播 Ctrl+C（目标及其子进程一并收到）。
    /// 返回 false 表示目标已退出或没有可附加的控制台，调用方应回退为强杀。
    /// </summary>
    public static bool TrySendCtrlC(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            if (!AttachConsole((uint)processId))
            {
                return false;
            }

            try
            {
                // NULL 处理器 + add=TRUE：让本进程忽略 CTRL+C，广播不会杀掉 GUI 自己。
                SetConsoleCtrlHandler(IntPtr.Zero, add: true);
                // 组 0 = 当前（已附加的）控制台上的全部进程。
                return GenerateConsoleCtrlEvent(CtrlCEvent, 0);
            }
            finally
            {
                SetConsoleCtrlHandler(IntPtr.Zero, add: false);
                FreeConsole();
            }
        }
        catch
        {
            return false;
        }
    }
}
