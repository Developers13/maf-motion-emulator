using System.Text;
using System.Text.Json;

namespace MafMotionEmulator.Gui.Services;

/// <summary>某学号的运动次数读数（来源：App 页面 OCR；不含任何凭证）。</summary>
public sealed class ExerciseStatus
{
    /// <summary>本周有效锻炼数（读数所属自然周见 <see cref="WeekKey"/>）。</summary>
    public int Week { get; set; }

    /// <summary>本学期评分次数（总运动次数）。</summary>
    public int Total { get; set; }

    /// <summary>该周读数的自然周标识（本周一的日期 yyyy-MM-dd）；跨周后周次数视为未知。</summary>
    public string WeekKey { get; set; } = "";

    public DateTimeOffset ReadAt { get; set; }
}

/// <summary>
/// 按学号缓存「本周 / 总」运动次数，落盘 gui/exercise-status.json。
///
/// 权威来源始终是 App 页面读数（每次跑前重读并覆盖）；本地 +1 只用于跑完后
/// 让主页圆点立刻变化，下一次读数会把它替换成真实值。
/// </summary>
public sealed class ExerciseStatusStore
{
    private readonly string _path;
    private readonly Dictionary<string, ExerciseStatus> _entries = new(StringComparer.Ordinal);

    public ExerciseStatusStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>当前自然周的标识：本周一（周一 00:00 本地时间为一周起点）。</summary>
    public static string CurrentWeekKey() => WeekKeyFor(DateTime.Now);

    public static string WeekKeyFor(DateTime time)
    {
        var daysSinceMonday = ((int)time.DayOfWeek + 6) % 7;
        return time.Date.AddDays(-daysSinceMonday).ToString("yyyy-MM-dd");
    }

    public bool TryGet(string studentId, out ExerciseStatus status) =>
        _entries.TryGetValue(studentId, out status!);

    /// <summary>丢弃内存缓存并重读磁盘（热重载 / 手工编辑 exercise-status.json 后使用）。</summary>
    public void Reload()
    {
        _entries.Clear();
        Load();
    }

    /// <summary>周读数是否是本周的（跨周后旧周的周次数不可再用）。</summary>
    public bool WeekIsCurrent(ExerciseStatus status) =>
        string.Equals(status.WeekKey, CurrentWeekKey(), StringComparison.Ordinal);

    /// <summary>写入一次 App 读数（覆盖本地缓存，含 +1 产生的估算值）。</summary>
    public void SetFromRead(string studentId, int week, int total)
    {
        _entries[studentId] = new ExerciseStatus
        {
            Week = Math.Max(0, week),
            Total = Math.Max(0, total),
            WeekKey = CurrentWeekKey(),
            ReadAt = DateTimeOffset.Now,
        };
        Save();
    }

    /// <summary>成功跑完一单后本地 +1，仅做即时显示；下次跑前读数会覆盖它。</summary>
    public void IncrementAfterSuccess(string studentId)
    {
        var weekKey = CurrentWeekKey();
        if (!_entries.TryGetValue(studentId, out var status))
        {
            status = new ExerciseStatus();
            _entries[studentId] = status;
        }

        if (!string.Equals(status.WeekKey, weekKey, StringComparison.Ordinal))
        {
            status.Week = 0;
            status.WeekKey = weekKey;
        }

        status.Week++;
        status.Total++;
        status.ReadAt = DateTimeOffset.Now;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ExerciseStatus>>(json);
            if (loaded is null)
            {
                return;
            }

            foreach (var pair in loaded)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                {
                    pair.Value.WeekKey ??= "";
                    _entries[pair.Key] = pair.Value;
                }
            }
        }
        catch
        {
            // 状态缓存坏了不影响运行：当作空缓存，跑前读数会重建。
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_entries, options);
            File.WriteAllText(_path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // 写失败只影响主页显示，不打断运行。
        }
    }
}
