using System.Drawing;
using MafMotionEmulator.Gui.Models;
using MafMotionEmulator.Gui.Services;

namespace MafMotionEmulator.Gui;

public sealed class MainForm : Form
{
    private const string AllStudents = "全部";

    private static readonly Font LogBannerFont =
        new(UiTheme.Mono.FontFamily, UiTheme.Mono.Size, FontStyle.Bold);

    private readonly string _projectRoot;
    private readonly string _settingsPath;
    private readonly UserListService _userListService;
    private readonly RunnerService _runner = new();
    private AppSettings _settings;

    private readonly ComboBox _studentCombo = new();
    private readonly Button _newVmButton;
    private readonly Button _prepButton;
    private readonly Button _settingsButton;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly RichTextBox _logBox = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private bool _busy; // 新建虚拟机 / 运行前准备 流程进行中，避免并发

    public MainForm()
    {
        _projectRoot = ProjectLocator.FindProjectRoot();
        _settingsPath = Path.Combine(_projectRoot, "gui", "gui-settings.json");
        _settings = AppSettings.Load(_settingsPath);
        _userListService = new UserListService(_projectRoot);

        _newVmButton = UiTheme.MakeGhostButton("新建虚拟机…");
        _prepButton = UiTheme.MakeGhostButton("运行前准备");
        _settingsButton = UiTheme.MakeGhostButton("设置…");
        _startButton = UiTheme.MakePrimaryButton("开始");
        _stopButton = UiTheme.MakeDangerButton("停止");

        InitializeUi();
        ReloadStudentList();

        _runner.LineReceived += OnLineReceived;
        _runner.Exited += OnExited;

        AppendLog("=== 就绪：选择学号后点击“开始” ===", isError: false);
    }

    private void InitializeUi()
    {
        Text = "MAF Motion Emulator GUI";
        Width = 1000;
        Height = 700;
        MinimumSize = new Size(760, 500);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiTheme.Canvas;
        Font = UiTheme.Sans;

        // ---- 头部：衬线标题 + 等宽元信息 + 分隔线 + 控制行 ----
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 16, 24, 0),
            BackColor = UiTheme.Canvas,
            ColumnCount = 1,
        };

        var title = UiTheme.MakeTitle("MAF Motion Emulator");
        title.Margin = new Padding(0, 0, 0, 4);

        var pathLabel = new Label
        {
            Text = _projectRoot,
            AutoSize = false,
            Height = 22,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Font = UiTheme.Mono,
            ForeColor = UiTheme.Muted,
            Margin = new Padding(0),
        };
        var pathTooltip = new ToolTip();
        pathTooltip.SetToolTip(pathLabel, _projectRoot);

        var separator = UiTheme.MakeSeparator();
        separator.Dock = DockStyle.Top;
        separator.Margin = new Padding(0, 14, 0, 0);

        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0),
        };

        _studentCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _studentCombo.Width = 160;
        _studentCombo.FlatStyle = FlatStyle.Flat;
        _studentCombo.Font = UiTheme.Sans;
        _studentCombo.Margin = new Padding(0, 6, 12, 0);

        _startButton.Click += (_, _) => StartRun();
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopRun();
        _newVmButton.Click += async (_, _) => await RunNewVmFlowAsync();
        _prepButton.Click += async (_, _) => await RunPrepFlowAsync();
        _settingsButton.Click += (_, _) => OpenSettings();
        _newVmButton.Margin = new Padding(24, 4, 0, 0); // 与运行操作拉开组间距

        var studentLabel = UiTheme.MakeMutedLabel("学号");
        studentLabel.Margin = new Padding(0, 10, 6, 0);

        controls.Controls.Add(studentLabel);
        controls.Controls.Add(_studentCombo);
        controls.Controls.Add(_startButton);
        controls.Controls.Add(_stopButton);
        controls.Controls.Add(_newVmButton);
        controls.Controls.Add(_prepButton);
        controls.Controls.Add(_settingsButton);

        header.Controls.Add(title);
        header.Controls.Add(pathLabel);
        header.Controls.Add(separator);
        header.Controls.Add(controls);
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ---- 日志：白色文档卡片（1px 边框容器） ----
        var logArea = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Canvas,
            Padding = new Padding(24, 16, 24, 16),
        };
        var logCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Border, // 1px 边框
            Padding = new Padding(1),
        };

        _logBox.Dock = DockStyle.Fill;
        _logBox.ReadOnly = true;
        _logBox.BorderStyle = BorderStyle.None;
        _logBox.Font = UiTheme.Mono;
        _logBox.BackColor = UiTheme.Surface;
        _logBox.ForeColor = UiTheme.Body;
        _logBox.WordWrap = false;
        _logBox.DetectUrls = false;
        _logBox.ScrollBars = RichTextBoxScrollBars.Both;

        logCard.Controls.Add(_logBox);
        logArea.Controls.Add(logCard);

        // ---- 状态栏 ----
        var status = new StatusStrip
        {
            BackColor = UiTheme.Canvas,
            GripStyle = ToolStripGripStyle.Hidden,
            SizingGrip = false,
            Font = UiTheme.SansSmall,
        };
        _statusLabel.Text = "待机";
        _statusLabel.ForeColor = UiTheme.Muted;
        status.Items.Add(_statusLabel);
        status.Items.Add(new ToolStripStatusLabel { Spring = true });

        // Dock 布局：Fill 控件先加入（最靠后），再叠 Top / Bottom。
        Controls.Add(logArea);
        Controls.Add(header);
        Controls.Add(status);
    }

    private void ReloadStudentList()
    {
        var previous = _studentCombo.SelectedItem as string;

        _studentCombo.Items.Clear();
        _studentCombo.Items.Add(AllStudents);

        List<UserRow> rows;
        try
        {
            rows = _userListService.Load();
        }
        catch (Exception ex)
        {
            AppendLog("读取 user-list.json 失败：" + ex.Message, isError: true);
            rows = new List<UserRow>();
        }

        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.StudentId))
            {
                _studentCombo.Items.Add(row.StudentId);
            }
        }

        var target = previous ?? _settings.LastStudentId;
        if (!string.IsNullOrEmpty(target) && _studentCombo.Items.Contains(target))
        {
            _studentCombo.SelectedItem = target;
        }
        else
        {
            _studentCombo.SelectedIndex = 0;
        }
    }

    private async Task RunNewVmFlowAsync()
    {
        if (_busy)
        {
            return;
        }

        using var dialog = new NewVmDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var studentId = dialog.StudentId;
        if (studentId is null)
        {
            return;
        }

        var rows = LoadUserRows();
        if (rows.Any(r => r.StudentId == studentId))
        {
            MessageBox.Show(this, $"学号 {studentId} 已存在于 user-list.json 中，请勿重复添加。", "新建虚拟机", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!ConfirmUserListUnchanged(rows))
        {
            return;
        }

        SetBusy(true, "正在新建虚拟机…");
        try
        {
            AppendLog($"=== 开始新建虚拟机：{studentId} ===", isError: false);
            var scriptDir = Path.Combine(_projectRoot, "gui", "scripts");
            var managerPath = string.IsNullOrWhiteSpace(_settings.MuMuManagerPath)
                ? @"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe"
                : _settings.MuMuManagerPath;
            var template = string.IsNullOrWhiteSpace(_settings.TemplateVmName) ? "模板" : _settings.TemplateVmName;

            var result = await Task.Run(() => PowerShellRunner.Run(
                Path.Combine(scriptDir, "mumu.ps1"),
                new Dictionary<string, string?>
                {
                    ["Action"] = "create",
                    ["StudentId"] = studentId,
                    ["Template"] = template,
                    ["ManagerPath"] = managerPath,
                },
                line => AppendLog(line, isError: false),
                timeout: TimeSpan.FromMinutes(5)));

            if (!result.Succeeded || !result.Lines.Any(l => l.Contains("[mumu] CREATED", StringComparison.Ordinal)))
            {
                var errorLine = result.Lines.FirstOrDefault(l => l.Contains("[mumu] ERROR", StringComparison.Ordinal)) ?? $"退出码 {result.ExitCode}";
                throw new InvalidOperationException("克隆失败：" + errorLine);
            }

            AppendLog("=== 虚拟机已创建，写入 user-list.json ===", isError: false);

            // 写入前重新加载，避免对话框停留期间文件被改动造成覆盖丢失。
            rows = LoadUserRows();
            rows.Add(new UserRow { StudentId = studentId, Password = "" }); // 密码与公里数留空
            _userListService.Save(rows);

            ReloadStudentList();
            _settings.LastStudentId = studentId;
            _settings.Save(_settingsPath);

            MessageBox.Show(
                this,
                $"已为 {studentId} 创建虚拟机（未启动）。\n\n" +
                "密码与公里数请在「设置 → 用户列表」中补全；\n" +
                "运行前请先在「设置 → 运行环境」确认 MuMu 管理器路径，并执行一次「运行前准备」。",
                "新建虚拟机",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"=== 新建失败：{ex.Message} ===", isError: true);
            MessageBox.Show(this, "新建虚拟机失败：\n\n" + ex.Message, "新建虚拟机", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunPrepFlowAsync()
    {
        if (_busy)
        {
            return;
        }

        var studentId = GetSelectedStudentId();
        if (studentId is null)
        {
            MessageBox.Show(this, "请先选择要准备的学号。", "运行前准备", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在准备：启动实例并检查环境…");
        try
        {
            AppendLog($"=== 运行前准备：{studentId} ===", isError: false);
            var scriptDir = Path.Combine(_projectRoot, "gui", "scripts");
            var managerPath = string.IsNullOrWhiteSpace(_settings.MuMuManagerPath)
                ? @"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe"
                : _settings.MuMuManagerPath;
            var template = string.IsNullOrWhiteSpace(_settings.TemplateVmName) ? "模板" : _settings.TemplateVmName;

            // 1. mumu.ps1 ensure：找到/克隆实例，启动并等待安卓开机，输出 READY index=… port=…
            var readyLines = new List<string>();
            var ensureResult = await Task.Run(() => PowerShellRunner.Run(
                Path.Combine(scriptDir, "mumu.ps1"),
                new Dictionary<string, string?>
                {
                    ["Action"] = "ensure",
                    ["StudentId"] = studentId,
                    ["Template"] = template,
                    ["ManagerPath"] = managerPath,
                },
                line =>
                {
                    AppendLog(line, isError: false);
                    readyLines.Add(line);
                },
                timeout: TimeSpan.FromMinutes(6)));

            if (!ensureResult.Succeeded)
            {
                var errorLine = readyLines.FirstOrDefault(l => l.Contains("[mumu] ERROR", StringComparison.Ordinal)) ?? $"退出码 {ensureResult.ExitCode}";
                throw new InvalidOperationException("实例启动失败：" + errorLine);
            }

            var readyLine = readyLines.FirstOrDefault(l => l.Contains("READY", StringComparison.Ordinal)) ?? "";
            var port = ExtractValue(readyLine, "port=");
            if (string.IsNullOrEmpty(port))
            {
                throw new InvalidOperationException("未能从脚本输出中解析 adb 端口：" + readyLine);
            }

            AppendLog($"=== 实例已就绪（adb 端口 {port}），检查 root 环境… ===", isError: false);

            // 2. adb-prep.ps1 -Port <port>：adb 连接 + MODEL/ANDROID/MAGISK/LSPOSED 检查
            var prepLines = new List<string>();
            var prepResult = await Task.Run(() => PowerShellRunner.Run(
                Path.Combine(scriptDir, "adb-prep.ps1"),
                new Dictionary<string, string?>
                {
                    ["Port"] = port,
                },
                line =>
                {
                    AppendLog(line, isError: false);
                    prepLines.Add(line);
                },
                timeout: TimeSpan.FromMinutes(3)));

            if (prepResult.ExitCode != 0)
            {
                var failLine = prepLines.FirstOrDefault(l => l.Contains("FAIL", StringComparison.Ordinal)) ?? $"退出码 {prepResult.ExitCode}";
                throw new InvalidOperationException("adb 环境检查失败：" + failLine);
            }

            var magiskMissing = prepLines.Any(l => l.Contains("MAGISK NOT-FOUND", StringComparison.Ordinal));
            var lsposedMissing = prepLines.Any(l => l.Contains("LSPOSED MISSING", StringComparison.Ordinal));

            if (magiskMissing || lsposedMissing)
            {
                AppendLog("=== 环境检查未通过 ===", isError: true);
                MessageBox.Show(
                    this,
                    "实例已启动并连上 adb，但 root 环境缺失：\n" +
                    (magiskMissing ? "• MAGISK NOT-FOUND（克隆实例不继承 Magisk 注入）\n" : "") +
                    (lsposedMissing ? "• LSPOSED MISSING\n" : "") +
                    "\n需在模拟器内完成 root 恢复后重试：\n" +
                    "打开 Magisk（com.cj.u.tusbfyo）→ 安装 → 直接安装（直接修改 /system）→ 等待完成 →\n" +
                    "用宿主机 adb 重启（adb -s 127.0.0.1:" + port + " reboot，不要点屏幕上的重启按钮），\n" +
                    "待重启完成后再点一次「运行前准备」。",
                    "运行前准备",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            else
            {
                AppendLog("=== 准备完成：环境正常，可开始运行 ===", isError: false);
                MessageBox.Show(this, "实例已启动，adb 连接与 root 环境均正常，可以开始运行。", "运行前准备", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"=== 运行前准备失败：{ex.Message} ===", isError: true);
            MessageBox.Show(this, "运行前准备失败：\n\n" + ex.Message, "运行前准备", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        _newVmButton.Enabled = !busy;
        _prepButton.Enabled = !busy;
        _settingsButton.Enabled = !busy;
        _startButton.Enabled = !busy && !_runner.IsRunning;
        _studentCombo.Enabled = !busy;
        if (busy)
        {
            SetStatus(status ?? "处理中…", UiTheme.PaleBlueText);
        }
        else
        {
            SetStatus("待机", UiTheme.Muted);
        }
    }

    private void StartRun()
    {
        if (_runner.IsRunning || _busy)
        {
            return;
        }

        var selected = _studentCombo.SelectedItem as string;
        var studentId = selected == AllStudents || string.IsNullOrEmpty(selected) ? null : selected;

        if (studentId is not null)
        {
            var row = LoadUserRows().FirstOrDefault(r => r.StudentId == studentId);
            if (row is null || string.IsNullOrEmpty(row.Password))
            {
                var confirm = MessageBox.Show(
                    this,
                    $"学号 {studentId} 尚未设置密码（user-list.json 中为空）。\n" +
                    "没有密码将无法登录体育 App，确认要继续吗？\n\n" +
                    "建议先在「设置 → 用户列表」补全密码与公里数。",
                    "开始运行",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }
            }
        }

        var python = string.IsNullOrWhiteSpace(_settings.PythonLauncher) ? "py" : _settings.PythonLauncher;

        AppendLog($"=== 开始运行：{(studentId ?? AllStudents)} ===", isError: false);
        _runner.Start(python, _projectRoot, studentId, _settings.Environment);

        _startButton.Enabled = false;
        _stopButton.Enabled = true;
        SetStatus("运行中…", UiTheme.PaleBlueText);

        if (studentId is not null)
        {
            _settings.LastStudentId = studentId;
        }
    }

    private void StopRun()
    {
        _runner.Stop();
        AppendLog("=== 已请求停止（正在终止子进程…） ===", isError: false);
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_userListService, _settings);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            _settings = form.Settings;
            _settings.Save(_settingsPath);
            ReloadStudentList();
            AppendLog("=== 设置已保存 ===", isError: false);
        }
    }

    private List<UserRow> LoadUserRows()
    {
        try
        {
            return _userListService.Load();
        }
        catch (Exception ex)
        {
            AppendLog("读取 user-list.json 失败：" + ex.Message, isError: true);
            return new List<UserRow>();
        }
    }

    private bool ConfirmUserListUnchanged(List<UserRow> rows)
    {
        var current = LoadUserRows();
        var a = string.Join("\n", rows.Select(r => r.StudentId));
        var b = string.Join("\n", current.Select(r => r.StudentId));
        return a == b;
    }

    private string? GetSelectedStudentId()
    {
        var selected = _studentCombo.SelectedItem as string;
        return selected is null || selected == AllStudents ? null : selected;
    }

    private static string? ExtractValue(string line, string prefix)
    {
        var index = line.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = index + prefix.Length;
        var end = line.IndexOfAny(new[] { ' ', '\t' }, start);
        return end < 0 ? line[start..] : line[start..end];
    }

    private void OnLineReceived(object? sender, LogLineEventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(() => AppendLog(e.Line, e.IsError));
        }
        catch (InvalidOperationException)
        {
            // 窗体句柄已销毁，丢弃该行。
        }
    }

    private void OnExited(object? sender, int code)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                _startButton.Enabled = !_busy;
                _stopButton.Enabled = false;

                var ok = code == 0;
                AppendLog(
                    ok ? $"=== 结束：成功（退出码 {code}） ===" : $"=== 结束：失败（退出码 {code}） ===",
                    isError: !ok);
                SetStatus(
                    ok ? "完成" : "失败",
                    ok ? UiTheme.PaleGreenText : UiTheme.PaleRedText);
            });
        }
        catch (InvalidOperationException)
        {
            // 窗体已关闭。
        }
    }

    private void AppendLog(string line, bool isError)
    {
        Color color;
        Font font = UiTheme.Mono;
        if (line.StartsWith("===", StringComparison.Ordinal))
        {
            color = UiTheme.Muted; // GUI 分节标记：灰色粗体
            font = LogBannerFont;
        }
        else if (line.Contains("-> OK", StringComparison.Ordinal))
        {
            color = UiTheme.LogOk;
        }
        else if (line.Contains("FAILED", StringComparison.Ordinal)
                 || isError
                 || line.Contains("[mumu] ERROR", StringComparison.Ordinal)
                 || line.Contains("[adb-prep] FAIL", StringComparison.Ordinal))
        {
            color = UiTheme.LogError;
        }
        else if (line.StartsWith("[", StringComparison.Ordinal))
        {
            color = UiTheme.LogInfo; // [e2e] / [mumu] / [adb-prep] / [wait_exercise_finish] 等脚本输出
        }
        else
        {
            color = UiTheme.LogDefault;
        }

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = UiTheme.LogMeta;
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] ");
        _logBox.SelectionColor = color;
        _logBox.SelectionFont = font;
        _logBox.AppendText(line + Environment.NewLine);
        _logBox.SelectionColor = _logBox.ForeColor;
        _logBox.SelectionFont = UiTheme.Mono;

        if (_settings.AutoScroll)
        {
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _runner.Dispose();

        if (_studentCombo.SelectedItem is string selected && selected != AllStudents)
        {
            _settings.LastStudentId = selected;
        }

        try
        {
            _settings.Save(_settingsPath);
        }
        catch
        {
            // 关闭时保存失败不阻断退出。
        }

        base.OnFormClosing(e);
    }
}
