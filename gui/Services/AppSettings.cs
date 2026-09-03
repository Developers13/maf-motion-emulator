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

    public string LastStudentId { get; set; } = "";

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
        settings.Environment ??= new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(settings.MuMuManagerPath))
        {
            settings.MuMuManagerPath = @"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe";
        }

        if (string.IsNullOrWhiteSpace(settings.TemplateVmName))
        {
            settings.TemplateVmName = "模板";
        }

        return settings;
    }
}
