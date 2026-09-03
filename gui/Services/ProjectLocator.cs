namespace MafMotionEmulator.Gui.Services;

/// <summary>
/// 从可执行文件位置向上查找 maf 项目根（含 tools/e2e.py 的目录）。
/// 兼容 dotnet run（工作目录在 gui/）与发布后 exe（在 gui/bin/... 下）。
/// </summary>
public static class ProjectLocator
{
    public static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "e2e.py")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
