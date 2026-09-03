using System.Text;
using System.Text.Json;
using MafMotionEmulator.Gui.Models;

namespace MafMotionEmulator.Gui.Services;

/// <summary>
/// 读写项目根目录下的 user-list.json。凭证仅在此处落盘，不进入日志或 GUI 配置。
/// </summary>
public sealed class UserListService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _userListPath;

    public UserListService(string projectRoot)
    {
        _userListPath = Path.Combine(projectRoot, "user-list.json");
    }

    public string UserListPath => _userListPath;

    public List<UserRow> Load()
    {
        if (!File.Exists(_userListPath))
        {
            return new List<UserRow>();
        }

        var json = File.ReadAllText(_userListPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<List<UserRow>>(json, Options) ?? new List<UserRow>();
    }

    public void Save(IReadOnlyCollection<UserRow> rows)
    {
        var json = JsonSerializer.Serialize(rows, Options);
        File.WriteAllText(_userListPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
