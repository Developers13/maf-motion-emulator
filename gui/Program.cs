namespace MafMotionEmulator.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(new System.Drawing.Font("Segoe UI", 9.75f));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowFatal(e.ExceptionObject as Exception);

        Application.Run(new MainForm());
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
