using System.Text;
using System.Text.Json;

namespace MafMotionEmulator.Gui.Services;

/// <summary>
/// GUI 自身配置（Python 启动器、项目根、额外环境变量、上次选择等）。
/// 不保存任何密码；凭证只存在于 user-list.json。
/// </summary>
public sealed class AppSettings
{
    public string PythonLauncher { get; set; } = "py";

    /// <summary>项目根目录；为空时自动解析。</summary>
    public string ProjectRoot { get; set; } = "";

    /// <summary>运行时传给脚本的额外环境变量（脚本自身会从 user-list.json 注入 MAF_* 凭证变量）。</summary>
    public Dictionary<string, string> Environment { get; set; } = new()
    {
        ["PYTHONUTF8"] = "1",
    };

    /// <summary>MuMu 管理器 CLI 路径（克隆/改名/启动实例用）。</summary>
    public string MuMuManagerPath { get; set; } = @"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe";

    /// <summary>作为克隆源模板的虚拟机名称。</summary>
    public string TemplateVmName { get; set; } = "模板";

    /// <summary>
    /// 运行 PowerShell 脚本的宿主程序（如 Windows PowerShell 5.1 / PowerShell 7）。
    /// 空表示自动（使用系统 powershell.exe）。
    /// </summary>
    public string ScriptHostPath { get; set; } = "";

    public string LastStudentId { get; set; } = "";

    /// <summary>主页多选的学号集合（有效性与顺序以 user-list.json 为准；空=默认全选）。</summary>
    public List<string> SelectedStudents { get; set; } = new();

    /// <summary>每周计入成绩的次数上限（学校规则：5 次/周）。</summary>
    public int WeekLimit { get; set; } = 5;

    /// <summary>一学期总次数上限（学校规则：32 次/学期）。</summary>
    public int TotalLimit { get; set; } = 32;

    public bool AutoScroll { get; set; } = true;

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            return Normalize(settings);
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(Normalize(this), options);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.PythonLauncher = string.IsNullOrWhiteSpace(settings.PythonLauncher) ? "py" : settings.PythonLauncher;
        settings.ProjectRoot ??= "";
        settings.LastStudentId ??= "";
        settings.SelectedStudents ??= new List<string>();
        if (settings.WeekLimit <= 0)
        {
            settings.WeekLimit = 5;
        }

        if (settings.TotalLimit <= 0)
        {
            settings.TotalLimit = 32;
        }
        settings.Environment ??= new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(settings.MuMuManagerPath))
        {
            settings.MuMuManagerPath = @"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe";
        }

        if (string.IsNullOrWhiteSpace(settings.TemplateVmName))
        {
            settings.TemplateVmName = "模板";
        }

        settings.ScriptHostPath ??= "";
        return settings;
    }
}
