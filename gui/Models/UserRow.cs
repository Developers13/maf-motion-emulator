using System.Text.Json.Serialization;

namespace MafMotionEmulator.Gui.Models;

/// <summary>
/// user-list.json 中的一行。字段名必须与脚本约定保持一致（snake_case）。
/// </summary>
public sealed class UserRow
{
    [JsonPropertyName("student_id")]
    public string StudentId { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    /// <summary>为空表示未设置，序列化时省略键，脚本按默认 3.2 处理。</summary>
    [JsonPropertyName("distance_km")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DistanceKm { get; set; }
}
