using System.Drawing;
using System.Text.RegularExpressions;
using MafMotionEmulator.Gui.Models;
using MafMotionEmulator.Gui.Services;

namespace MafMotionEmulator.Gui;

public sealed class MainForm : FadeForm
{
    private static readonly Font LogBannerFont =
        new(UiTheme.Mono.FontFamily, UiTheme.Mono.Size, FontStyle.Bold);

    // e2e 跑前读数探针的输出行：`[e2e] COUNTS sid=2025302319 week=0 total=33`
    private static readonly Regex CountsLine = new(
        @"\[e2e\]\s+COUNTS\s+sid=(\S+)\s+week=(-?\d+)\s+total=(-?\d+)",
        RegexOptions.Compiled);

    // 批量里 e2e 判定达上限而跳过的学号：`[e2e] SKIP sid=2025302319 reason=total ...`
    // 混合批量（有人跳过有人跑）时 e2e 整体仍退出 0，故必须按行识别是哪一个被跳过。
    private static readonly Regex SkipLine = new(
        @"\[e2e\]\s+SKIP\s+sid=(\S+)",
        RegexOptions.Compiled);

    private readonly string _projectRoot;
    private readonly string _settingsPath;
    private readonly UserListService _userListService;
    private readonly RunnerService _runner = new();
    private AppSettings _settings;

    private readonly Button _selectStudentsButton;
    private readonly Button _reloadButton;
    private readonly Button _settingsButton;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly RichTextBox _logBox = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly StudentStatusList _statusList = new();
    private readonly ExerciseStatusStore _statusStore;
    private List<string> _selectedStudents = new(); // 多选的学号（保持 user-list.json 顺序）
    private bool _busy; // 开始（准备阶段）/ 批量运行 流程进行中，避免并发
    private bool _batchRunning; // 批量运行进行中：进度由 RunBatchAsync 统一汇报
    private string? _activeRunStudentId; // 单次运行的目标学号：结束推通知时标识是哪一单
    private volatile string? _skippedStudentId; // 本轮 e2e 判定达上限而跳过的学号（解析 SKIP 行）
    private bool _stopRequested; // 用户点击「停止」后置位，用于把子进程退出识别为用户中断

    // UI 状态调和定时器：按钮可用性由当前状态周期性推导刷新，
    // 任何事件丢失（如进程被强杀后的 Exited 竞态）都会在 ~300ms 内自愈。
    private readonly System.Windows.Forms.Timer _stateTimer = new() { Interval = 300 };

    public MainForm()
    {
        _projectRoot = ProjectLocator.FindProjectRoot();
        _settingsPath = Path.Combine(_projectRoot, "gui", "gui-settings.json");
        _settings = AppSettings.Load(_settingsPath);
        _userListService = new UserListService(_projectRoot);
        _statusStore = new ExerciseStatusStore(Path.Combine(_projectRoot, "gui", "exercise-status.json"));

        _selectStudentsButton = UiTheme.MakeGhostButton("选择学号…");
        _reloadButton = UiTheme.MakeGhostButton("热重载");
        _settingsButton = UiTheme.MakeGhostButton("设置…");
        _startButton = UiTheme.MakePrimaryButton("开始");
        _stopButton = UiTheme.MakeDangerButton("停止");

        InitializeUi();
        ReloadStudentList();

        _runner.LineReceived += OnLineReceived;
        _runner.Exited += OnExited;
        _stateTimer.Tick += (_, _) => UpdateActionButtons();
        _stateTimer.Start();

        AppendLog(
            Program.IsElevated()
                ? "Running as administrator: yes (scripts inherit elevation)"
                : "Running as administrator: no (MuMu/adb features limited)",
            isError: !Program.IsElevated());
        AppendLog("=== Ready. Pick students and click Start ===", isError: false);
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

        _selectStudentsButton.Click += (_, _) => OpenStudentSelectDialog();
        _selectStudentsButton.Margin = new Padding(0, 4, 12, 0);

        _startButton.Click += async (_, _) => await StartRunAsync();
        // 停止按钮常驻可点：任一流程（运行/批量运行）进行中都可中断，空闲时点击仅提示。
        _stopButton.Click += (_, _) => StopRun();
        _reloadButton.Click += (_, _) => HotReload();
        _settingsButton.Click += (_, _) => OpenSettings();
        _reloadButton.Margin = new Padding(24, 4, 0, 0); // 与运行操作拉开组间距

        controls.Controls.Add(_selectStudentsButton);
        controls.Controls.Add(_startButton);
        controls.Controls.Add(_stopButton);
        controls.Controls.Add(_reloadButton);
        controls.Controls.Add(_settingsButton);

        header.Controls.Add(title);
        header.Controls.Add(pathLabel);
        header.Controls.Add(separator);
        header.Controls.Add(controls);
        header.Controls.Add(_statusList);
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
        List<UserRow> rows;
        try
        {
            rows = _userListService.Load();
        }
        catch (Exception ex)
        {
            AppendLog("Failed to read user-list.json: " + ex.Message, isError: true);
            rows = new List<UserRow>();
        }

        var allIds = rows
            .Select(r => r.StudentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        // 保留仍存在于 json 中的已选学号（保持 json 顺序）；一个都不剩则默认全选。
        var kept = allIds.Where(_selectedStudents.Contains).ToList();
        _selectedStudents = kept.Count > 0 ? kept : allIds.ToList();

        UpdateSelectionUi();
    }

    /// <summary>按钮文本反映当前选中状态；同时调和「开始」可用性（空选集禁用）。</summary>
    private void UpdateSelectionUi()
    {
        var total = LoadUserRows()
            .Select(r => r.StudentId)
            .Count(id => !string.IsNullOrWhiteSpace(id));
        var n = _selectedStudents.Count;
        _selectStudentsButton.Text = total == 0
            ? "选择学号…（列表为空）"
            : n >= total
                ? $"学号：全部（{total}）"
                    : n == 1
                        ? $"学号：{_selectedStudents[0]}"
                        : $"学号：已选 {n}/{total}";
        _statusList.Rebuild(_selectedStudents, _statusStore, _settings.WeekLimit, _settings.TotalLimit);
        UpdateActionButtons();
    }

    /// <summary>打开学号多选对话框；确定后持久化到 gui-settings.json 并刷新主页显示。</summary>
    private void OpenStudentSelectDialog()
    {
        if (_busy)
        {
            return;
        }

        var allIds = LoadUserRows()
            .Select(r => r.StudentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        if (allIds.Count == 0)
        {
            MessageBox.Show(this, "user-list.json 中没有学号。", "选择学号", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new StudentSelectDialog(
            allIds,
            _selectedStudents,
            _statusStore,
            _settings.WeekLimit,
            _settings.TotalLimit);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _selectedStudents = dialog.Selected;
        _settings.SelectedStudents = _selectedStudents;
        _settings.Save(_settingsPath);
        UpdateSelectionUi();

        var summary = _selectedStudents.Count == allIds.Count
            ? $"全部 {allIds.Count} 个"
            : string.Join(", ", _selectedStudents);
        AppendLog($"=== Selection updated: {summary} ===", isError: false);
    }

    /// <summary>
    /// 调用 mumu.ps1 shutdown-all：关闭所有运行中的实例，只保留 keepStudentId（可为空＝全关）。
    /// 模板实例永不关闭。失败只记警告不抛异常——关不掉其余实例只影响资源占用，
    /// 不影响 e2e 的正确性（它按实例名解析自己的 adb 地址），不应因此中断整批流程。
    /// </summary>
    private async Task ShutdownInstancesExceptAsync(string? keepStudentId)
    {
        PowerShellResult result;
        try
        {
            result = await Task.Run(() => PowerShellRunner.Run(
                Path.Combine(_projectRoot, "gui", "scripts", "mumu.ps1"),
                new Dictionary<string, string?>
                {
                    ["Action"] = "shutdown-all",
                    ["KeepStudentId"] = keepStudentId ?? "",
                    ["Template"] = string.IsNullOrWhiteSpace(_settings.TemplateVmName) ? "模板" : _settings.TemplateVmName,
                    ["ManagerPath"] = ResolveManagerPath(),
                },
                line => AppendLogSafe(line, isError: false),
                timeout: TimeSpan.FromMinutes(5),
                hostPath: _settings.ScriptHostPath));
        }
        catch (Exception ex)
        {
            AppendLog($"[mumu] WARN shutdown-all failed: {ex.Message}", isError: false);
            return;
        }

        if (!result.Succeeded && !_stopRequested)
        {
            var errorLine = result.Lines.FirstOrDefault(l => l.Contains("[mumu] ERROR", StringComparison.Ordinal))
                            ?? $"exit code {result.ExitCode}";
            AppendLog($"[mumu] WARN shutdown-all did not complete: {errorLine}", isError: false);
        }
    }

    /// <summary>关闭单个学号的实例（批量流程里每个学号处理完后的收尾）。失败只记日志，不影响后续学号。</summary>
    private async Task ShutdownStudentInstanceAsync(string studentId)
    {
        PowerShellResult result;
        try
        {
            result = await Task.Run(() => PowerShellRunner.Run(
                Path.Combine(_projectRoot, "gui", "scripts", "mumu.ps1"),
                new Dictionary<string, string?>
                {
                    ["Action"] = "shutdown",
                    ["StudentId"] = studentId,
                    ["ManagerPath"] = ResolveManagerPath(),
                },
                line => AppendLogSafe(line, isError: false),
                timeout: TimeSpan.FromMinutes(3),
                hostPath: _settings.ScriptHostPath));
        }
        catch (Exception ex)
        {
            AppendLog($"[mumu] WARN could not close instance for {studentId}: {ex.Message}", isError: false);
            return;
        }

        if (result.Succeeded || _stopRequested)
        {
            return;
        }

        // 该学号还没克隆出实例（如准备阶段失败在克隆之前）时 shutdown 会报 instance_not_found，
        // 属预期情况，不必告警。
        if (result.Lines.Any(l => l.Contains("instance_not_found", StringComparison.Ordinal)))
        {
            return;
        }

        var errorLine = result.Lines.FirstOrDefault(l => l.Contains("[mumu] ERROR", StringComparison.Ordinal))
                        ?? $"exit code {result.ExitCode}";
        AppendLog($"[mumu] WARN could not close instance for {studentId}: {errorLine}", isError: false);
    }

    private string ResolveManagerPath()
        => string.IsNullOrWhiteSpace(_settings.MuMuManagerPath)
            ? @"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe"
            : _settings.MuMuManagerPath;

    /// <summary>
    /// 调 mumu.ps1 ensure 并解析出 READY 行；Clone/启动都幂等，重试安全。
    /// 输出采集已在 PowerShellRunner 修好（等待回调排干），这里再兜一层：
    /// 退出码 0 却没有 READY 只可能是采集缺口，重试一次比直接判失败体验好。
    /// </summary>
    private async Task<string> EnsureInstanceReadyAsync(string studentId)
    {
        var scriptDir = Path.Combine(_projectRoot, "gui", "scripts");
        var template = string.IsNullOrWhiteSpace(_settings.TemplateVmName) ? "模板" : _settings.TemplateVmName;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var readyLines = new List<string>();
            var suffix = attempt > 1 ? $" (retry {attempt}/2)" : "";
            var ensureResult = await Task.Run(() => PowerShellRunner.Run(
                Path.Combine(scriptDir, "mumu.ps1"),
                new Dictionary<string, string?>
                {
                    ["Action"] = "ensure",
                    ["StudentId"] = studentId,
                    ["Template"] = template,
                    ["ManagerPath"] = ResolveManagerPath(),
                },
                line =>
                {
                    AppendLogSafe(line, isError: false);
                    readyLines.Add(line);
                },
                timeout: TimeSpan.FromMinutes(6),
                hostPath: _settings.ScriptHostPath));

            if (!ensureResult.Succeeded)
            {
                var errorLine = readyLines.FirstOrDefault(l => l.Contains("[mumu] ERROR", StringComparison.Ordinal))
                                ?? $"exit code {ensureResult.ExitCode}";
                throw new InvalidOperationException("Instance launch failed" + suffix + ": " + errorLine);
            }

            var readyLine = readyLines.FirstOrDefault(l => l.Contains("READY", StringComparison.Ordinal));
            if (readyLine is not null && !string.IsNullOrEmpty(ExtractValue(readyLine, "port=")))
            {
                return readyLine;
            }

            // 退出码 0 但没拿到 READY：输出被截断（历史 bug）。ensure 幂等，重试一次。
            AppendLog(
                $"=== ensure exited 0 without a READY line" + suffix + "; retrying ===",
                isError: false);
        }

        throw new InvalidOperationException(
            "Failed to parse adb port from script output (ensure succeeded twice without a READY line).");
    }

    /// <summary>
    /// 对单个学号执行完整准备：ensure（必要时克隆并启动）→ adb 环境检查 → 必要时自动修复 root 并复检。
    /// 失败时抛异常，由批处理循环决定是中断还是记录后继续。
    /// </summary>
    private async Task PrepareOneStudentAsync(string studentId)
    {
        var scriptDir = Path.Combine(_projectRoot, "gui", "scripts");
        var managerPath = ResolveManagerPath();

        // 1. mumu.ps1 ensure：找到/克隆实例，启动并等待安卓开机，输出 READY index=… port=…
        var readyLine = await EnsureInstanceReadyAsync(studentId);
        var port = ExtractValue(readyLine, "port=")!;

        if (_stopRequested)
        {
            return; // 用户中断：不再进入 adb 环境检查
        }

        AppendLog($"=== Instance ready (adb port {port}), checking root environment... ===", isError: false);

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
            AppendLogSafe(line, isError: false);
            prepLines.Add(line);
        },
        timeout: TimeSpan.FromMinutes(3),
        hostPath: _settings.ScriptHostPath));

    // RECOVERY_INCOMPLETE：上次 root 恢复装完没重启（安装器页面残留前台），
    // 也走 root-recover 补完，不算 adb 环境检查失败。
    var recoveryIncomplete = prepLines.Any(l => l.Contains("RECOVERY_INCOMPLETE", StringComparison.Ordinal));
    if (prepResult.ExitCode != 0 && !recoveryIncomplete)
    {
        var failLine = prepLines.FirstOrDefault(l => l.Contains("FAIL", StringComparison.Ordinal)) ?? $"exit code {prepResult.ExitCode}";
        throw new InvalidOperationException("adb environment check failed: " + failLine);
    }

    var magiskMissing = prepLines.Any(l => l.Contains("MAGISK NOT-FOUND", StringComparison.Ordinal));
    var lsposedMissing = prepLines.Any(l => l.Contains("LSPOSED MISSING", StringComparison.Ordinal));

    // 3. root 环境缺失或上次恢复半途而废 → 自动修复（Magisk system-mode 安装 + 重启 + 复检），
    //    复现 skill 的 root_recover 步骤；完成后重跑 adb-prep 确认。
    if (magiskMissing || recoveryIncomplete)
    {
        var index = ExtractValue(readyLine, "index=");
        if (string.IsNullOrEmpty(index))
        {
            throw new InvalidOperationException("Failed to parse instance index from script output: " + readyLine);
        }

        AppendLog(
            recoveryIncomplete
                ? "=== Root recovery was interrupted (installer page foreground); completing it (reboot + verify) ==="
                : "=== Root environment missing (MAGISK NOT-FOUND); starting automatic recovery ===",
            isError: false);
        SetStatus("正在修复 root 环境（安装 Magisk 并重启）…", UiTheme.PaleBlueText);

        var recoverLines = new List<string>();
        var recoverResult = await Task.Run(() => PowerShellRunner.Run(
            Path.Combine(scriptDir, "root-recover.ps1"),
            new Dictionary<string, string?>
            {
                ["Port"] = port,
                ["Index"] = index,
                ["NemuPath"] = Path.Combine(_projectRoot, "gui", "tools", "nemu", "nemu.exe"),
            },
            line =>
            {
                AppendLogSafe(line, isError: false);
                recoverLines.Add(line);
            },
            timeout: TimeSpan.FromMinutes(15), // 安装最长 3min + 重启开机最长 6min + 余量
            hostPath: _settings.ScriptHostPath));

        if (_stopRequested)
        {
            return;
        }

        if (!recoverResult.Succeeded)
        {
            var errorLine = recoverLines.FirstOrDefault(l => l.Contains("[root-recover] ERROR", StringComparison.Ordinal))
                            ?? $"exit code {recoverResult.ExitCode}";
            throw new InvalidOperationException("Root recovery failed: " + errorLine);
        }

        AppendLog("=== Root recovery done; re-checking environment ===", isError: false);

        // 复检：adb-prep 重新跑一遍
        prepLines = new List<string>();
        var recheck = await Task.Run(() => PowerShellRunner.Run(
            Path.Combine(scriptDir, "adb-prep.ps1"),
            new Dictionary<string, string?> { ["Port"] = port },
            line =>
            {
                AppendLogSafe(line, isError: false);
                prepLines.Add(line);
            },
            timeout: TimeSpan.FromMinutes(3),
            hostPath: _settings.ScriptHostPath));

        magiskMissing = prepLines.Any(l => l.Contains("MAGISK NOT-FOUND", StringComparison.Ordinal));
        lsposedMissing = prepLines.Any(l => l.Contains("LSPOSED MISSING", StringComparison.Ordinal));

        if (recheck.ExitCode != 0 || magiskMissing || lsposedMissing)
        {
            var recheckFail = prepLines.FirstOrDefault(l => l.Contains("FAIL", StringComparison.Ordinal))
                              ?? $"exit code {recheck.ExitCode}";
            throw new InvalidOperationException(
                "Root recovery finished but verification still failed (" +
                recheckFail + " " +
                (magiskMissing ? "MAGISK NOT-FOUND " : "") +
                (lsposedMissing ? "LSPOSED MISSING" : "") + ")");
        }
    }
    else if (lsposedMissing)
    {
        // root 在位但隐藏栈异常：按 skill 约定停止并报告，不自动处理。
        throw new InvalidOperationException(
            "LSPOSED MISSING while Magisk is present - hidden-root stack is broken; manual inspection required.");
    }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        UpdateActionButtons();
        if (busy)
        {
            SetStatus(status ?? "处理中…", UiTheme.PaleBlueText);
        }
        else
        {
            SetStatus("待机", UiTheme.Muted);
        }
    }

    /// <summary>
    /// 按钮可用性统一由当前状态推导：任一脚本在跑（e2e / mumu.ps1 / adb-prep）
    /// 或流程占用即禁用 开始（未选中任何学号时同样禁用）。
    /// 由 SetBusy、OnExited 与调和定时器共同调用，写状态只走这一处。
    /// </summary>
    private void UpdateActionButtons()
    {
        var scriptBusy = _runner.IsRunning || ProcessRegistry.AnyRunning;
        _startButton.Enabled = !_busy && !scriptBusy && _selectedStudents.Count > 0;
        _reloadButton.Enabled = !_busy;
        _settingsButton.Enabled = !_busy;
        _selectStudentsButton.Enabled = !_busy;
    }

    private async Task StartRunAsync()
    {
        if (_runner.IsRunning || _busy)
        {
            return;
        }

        var targets = _selectedStudents
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        if (targets.Count == 0)
        {
            return; // 「开始」在空选集下已禁用，这里兜底
        }

        _stopRequested = false;

        // 勾选 ≥2 个 → 批量；恰好 1 个 → 单次运行（走 RunnerService，结束推单条通知）。
        if (targets.Count > 1)
        {
            _ = RunBatchAsync(targets);
            return;
        }

        var studentId = targets[0];

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

        // 已并入「运行前准备」：开始即自动拉起实例并检查环境（ensure → adb 检查 →
        // 必要时 root 修复），模拟器未运行不再是报错退出，而是先准备再运行。
        AppendLog($"=== Start run: {studentId} ===", isError: false);
        SetBusy(true, $"正在准备 {studentId}…");
        _activeRunStudentId = studentId;

        try
        {
            await PrepareOneStudentAsync(studentId);
        }
        catch (Exception) when (_stopRequested)
        {
            SetBusy(false);
            AppendLog("=== Run stopped by user during prep ===", isError: false);
            SetStatus("已停止", UiTheme.Muted);
            ToastNotifier.Show("运行已停止", $"{studentId} 在准备阶段被手动停止。", ToastTone.Neutral);
            return;
        }
        catch (Exception ex)
        {
            SetBusy(false);
            AppendLog($"=== Run failed during prep: {ex.Message} ===", isError: true);
            ToastNotifier.Show($"运行失败 · {studentId}", $"准备阶段失败：{ex.Message}", ToastTone.Fail);
            MessageBox.Show(this, "运行前准备失败：\n\n" + ex.Message, "开始运行", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (_stopRequested)
        {
            // PrepareOneStudentAsync 内部不打异常直接返回的中断路径。
            SetBusy(false);
            AppendLog("=== Run stopped by user during prep ===", isError: false);
            SetStatus("已停止", UiTheme.Muted);
            ToastNotifier.Show("运行已停止", $"{studentId} 在准备阶段被手动停止。", ToastTone.Neutral);
            return;
        }

        // 准备完成 → 启动 e2e。先 Start 再清 _busy，按钮禁用由 _runner.IsRunning 无缝接管。
        var python = string.IsNullOrWhiteSpace(_settings.PythonLauncher) ? "py" : _settings.PythonLauncher;
        _runner.Start(python, _projectRoot, studentId, BuildRunEnv());
        SetBusy(false);
        SetStatus("运行中…", UiTheme.PaleBlueText);
    }

    /// <summary>
    /// 批量运行：对传入的学号列表逐个完成「清场 → 准备 → 运行 → 关闭该实例」，
    /// 全部结束后关闭所有实例。每次只保留当前学号的实例在运行，避免多开争抢资源。
    /// </summary>
    private async Task RunBatchAsync(List<string> students)
    {
        students = students
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (students.Count == 0)
        {
            AppendLog("=== Batch run aborted: selection is empty ===", isError: false);
            MessageBox.Show(this, "未选中任何学号。", "开始运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var missingPassword = students.Where(id =>
        {
            var row = LoadUserRows().FirstOrDefault(r => r.StudentId == id);
            return row is null || string.IsNullOrEmpty(row.Password);
        }).ToList();

        var confirm = MessageBox.Show(
            this,
            $"将对选中的 {students.Count} 个学号逐个运行：\n\n" +
            "每个学号：关闭其他实例 → 自动准备（启动实例 + 环境检查）→ 跑完整流程 → 关闭该实例；\n" +
            "全部完成后关闭所有实例。\n\n" +
            (missingPassword.Count > 0
                ? $"注意：{missingPassword.Count} 个学号尚未设置密码，将无法登录：{string.Join(", ", missingPassword)}\n\n"
                : "") +
            $"逐个运行约需 20–40 分钟/人（含模拟锻炼时长），合计较久。确认开始吗？",
            "开始运行（批量）",
            MessageBoxButtons.YesNo,
            missingPassword.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        SetBusy(true, "批量运行中…");
        _batchRunning = true;
        var python = string.IsNullOrWhiteSpace(_settings.PythonLauncher) ? "py" : _settings.PythonLauncher;
        var succeeded = new List<string>();
        var failed = new List<string>();
        var skipped = new List<string>();

        try
        {
            for (var i = 0; i < students.Count; i++)
            {
                if (_stopRequested)
                {
                    break;
                }

                var studentId = students[i];
                var progress = $" ({i + 1}/{students.Count})";
                AppendLog($"=== Batch run{progress}: {studentId} ===", isError: false);
                SetStatus($"批量运行 {studentId}{progress}…", UiTheme.PaleBlueText);

                // 1. 关掉其他实例，只留当前学号——确保 e2e 解析到的设备就是它自己的实例。
                await ShutdownInstancesExceptAsync(studentId);
                if (_stopRequested)
                {
                    break;
                }

                // 2. 准备当前学号（ensure 会启动/克隆；root 缺失时自动修复）。
                try
                {
                    await PrepareOneStudentAsync(studentId);
                }
                catch (Exception ex) when (!_stopRequested)
                {
                    failed.Add(studentId);
                    AppendLog($"[batch] {studentId} prep -> FAILED: {ex.Message}", isError: true);
                    ToastNotifier.Show(
                        $"批量运行 {progress} 失败",
                        $"{studentId} 准备失败：{ex.Message}",
                        ToastTone.Fail);
                    await ShutdownStudentInstanceAsync(studentId);
                    continue;
                }

                if (_stopRequested)
                {
                    break;
                }

                // 3. 运行完整流程，等它结束再处理下一个（避免同时驱动多个模拟器）。
                AppendLog($"=== Start run: {studentId} ===", isError: false);
                SetStatus($"运行中 {studentId}{progress}…", UiTheme.PaleBlueText);
                var exitCode = await RunE2EForStudentAsync(python, studentId);
                if (_stopRequested)
                {
                    break;
                }

                if (exitCode == 3 || _skippedStudentId == studentId)
                {
                    // e2e 的约定：跑前读数显示本周或总数已达上限，批量模式下跳过该学号。
                    skipped.Add(studentId);
                    AppendLog($"[batch] {studentId} -> SKIP (weekly/total cap reached)", isError: false);
                    ToastNotifier.Show(
                        $"批量运行 {progress} 跳过",
                        $"{studentId} 本周或总次数已达上限，已跳过。",
                        ToastTone.Neutral);
                    await ShutdownStudentInstanceAsync(studentId);
                    continue;
                }

                if (exitCode == 0)
                {
                    succeeded.Add(studentId);
                    // 本地 +1 只做即时显示；下次跑前读数会覆盖成本周/总数的真实值。
                    _statusStore.IncrementAfterSuccess(studentId);
                    UpdateSelectionUi();
                    AppendLog($"[batch] {studentId} -> OK", isError: false);
                    ToastNotifier.Show(
                        $"批量运行 {progress} 完成",
                        $"{studentId} 成功，继续处理下一个学号。",
                        ToastTone.Ok);
                }
                else
                {
                    failed.Add(studentId);
                    AppendLog($"[batch] {studentId} -> FAILED (exit code {exitCode})", isError: true);
                    ToastNotifier.Show(
                        $"批量运行 {progress} 失败",
                        $"{studentId} 失败（退出码 {exitCode}），已跳过并继续后续学号。",
                        ToastTone.Fail);
                }

                // 4. 关掉该实例，给下一个腾资源。
                await ShutdownStudentInstanceAsync(studentId);
            }
        }
        catch (Exception ex)
        {
            // 走到这里只有两种情况：用户点了停止（准备阶段的异常按设计不会被上面的
            // when 过滤器捕获），或出现预期外的错误。两种都不该让异常逃出本方法——
            // 本方法是 fire-and-forget 调用，逃逸会变成未处理异常。
            if (!_stopRequested)
            {
                AppendLog($"=== Batch run aborted: {ex.Message} ===", isError: true);
                ToastNotifier.Show("批量运行中止", ex.Message, ToastTone.Fail);
            }
        }
        finally
        {
            // 收尾：无论成功、失败还是被停止，都关闭所有实例（模板除外）。
            _batchRunning = false;
            AppendLog("=== Closing all emulator instances ===", isError: false);
            try
            {
                await ShutdownInstancesExceptAsync(null);
            }
            catch (Exception ex)
            {
                AppendLog($"[mumu] WARN final cleanup failed: {ex.Message}", isError: false);
            }

            SetBusy(false);
        }

        if (_stopRequested)
        {
            AppendLog("=== Batch run stopped by user ===", isError: false);
            SetStatus("已停止", UiTheme.Muted);
            ToastNotifier.Show(
                "批量运行已停止",
                $"已完成 {succeeded.Count} 个，失败 {failed.Count} 个。",
                ToastTone.Neutral);
            return;
        }

        var summary = succeeded.Count == 0 && failed.Count == 0 && skipped.Count > 0
            ? $"批量运行完成：{skipped.Count} 个学号均已达上限（本周或总次数），已全部跳过。"
            : $"批量运行完成：成功 {succeeded.Count} 个" +
              (skipped.Count > 0 ? $"，跳过 {skipped.Count} 个（已达上限）" : "") +
              (failed.Count > 0 ? $"，失败 {failed.Count} 个（{string.Join(", ", failed)}）" : "，全部通过") + "。";
        AppendLog("=== " + summary + " ===", isError: failed.Count > 0);
        ToastNotifier.Show(
            failed.Count > 0 ? "批量运行完成（部分失败）" : "批量运行完成",
            summary,
            failed.Count > 0 ? ToastTone.Fail : ToastTone.Ok);
        MessageBox.Show(
            this,
            summary + "\n\n所有模拟器实例已关闭。",
            "开始运行（批量）",
            MessageBoxButtons.OK,
            failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        SetStatus(failed.Count > 0 ? "部分失败" : "完成", failed.Count > 0 ? UiTheme.PaleRedText : UiTheme.PaleGreenText);
    }

    /// <summary>
    /// 跑单个学号的 <c>py tools/e2e.py &lt;学号&gt;</c> 并等到退出，返回退出码。
    /// 用本地 TaskCompletionSource 承接 RunnerService.Exited，不干扰界面上的单次运行状态显示。
    /// </summary>
    private async Task<int> RunE2EForStudentAsync(string python, string studentId)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnExited(object? sender, int code) => completion.TrySetResult(code);

        _skippedStudentId = null;
        _runner.Exited += OnExited;
        try
        {
            _runner.Start(python, _projectRoot, studentId, BuildRunEnv(), extraArgs: "--skip-if-done");
            if (!_runner.IsRunning)
            {
                // 启动即失败：RunnerService 已把错误写入日志并触发过 Exited。
                return completion.Task.IsCompleted ? await completion.Task : -1;
            }

            return await completion.Task;
        }
        finally
        {
            _runner.Exited -= OnExited;
        }
    }

    /// <summary>
    /// 运行所需环境变量：设置页配置 + MuMu 管理器路径（用于按学号解析各自实例的 adb 端口，
    /// 避免多开时所有学号都落到第一个设备）；用户在环境变量表里的显式配置优先。
    /// </summary>
    private Dictionary<string, string> BuildRunEnv()
    {
        var runEnv = new Dictionary<string, string>(_settings.Environment);
        if (!runEnv.ContainsKey("MAF_MUMU_MANAGER") && !string.IsNullOrWhiteSpace(_settings.MuMuManagerPath))
        {
            runEnv["MAF_MUMU_MANAGER"] = _settings.MuMuManagerPath;
        }

        // 上限阈值由 GUI 维护、通过环境变量下发，避免 Python 侧再硬编码一份。
        if (!runEnv.ContainsKey("MAF_WEEK_LIMIT"))
        {
            runEnv["MAF_WEEK_LIMIT"] = _settings.WeekLimit.ToString();
        }

        if (!runEnv.ContainsKey("MAF_TOTAL_LIMIT"))
        {
            runEnv["MAF_TOTAL_LIMIT"] = _settings.TotalLimit.ToString();
        }

        return runEnv;
    }

    private void StopRun()
    {
        // runner 自己也是"在运行"的证据：启动器（如 py.exe）秒退时注册表可能为空，
        // 只看注册表会让停止按钮变成哑巴（实测 2026-11-12）。
        var hasRunner = _runner.IsRunning;
        if (!hasRunner && !ProcessRegistry.AnyRunning)
        {
            AppendLog("=== Nothing is running ===", isError: false);
            return;
        }

        _stopRequested = true;

        // 立即向所有 GUI 启动的子进程（python e2e / mumu.ps1 / adb-prep.ps1 及其子进程）
        // 尽力广播 Ctrl+C；后台 2s 宽限后对仍存活者强杀进程树，保证停止一定生效。
        // StopAll 不阻塞 UI。
        var sent = ProcessRegistry.StopAll(graceMs: 2000, log: line => AppendLogSafe(line, isError: false));
        if (sent == 0 && hasRunner)
        {
            // 注册表里没有可发信号的进程（启动器已退出等）：直接杀掉 runner 的进程树。
            _runner.Stop();
            AppendLog("=== Stop requested: runner process tree terminated ===", isError: false);
        }
        else
        {
            AppendLog(
                $"=== Stop requested: Ctrl+C signalled {sent} process(es); terminating within 2s ===",
                isError: false);
        }

        SetStatus("停止中…", UiTheme.Muted);
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_userListService, _settings);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            _settings = form.Settings;
            _settings.Save(_settingsPath);
            ReloadStudentList();
            UpdateSelectionUi(); // 阈值可能改过：重画圆点
            AppendLog("=== Settings saved ===", isError: false);
        }
    }

    /// <summary>
    /// 热重载：重读 gui-settings.json 与 user-list.json，让设置页/外部新增的学号
    /// （完整行：学号 + 密码 + 公里数）无需重启 GUI 即出现在下拉框中；
    /// 随后点「开始」即会为尚无实例的学号自动克隆新建（mumu.ps1 ensure 幂等）。
    /// </summary>
    private void HotReload()
    {
        if (_busy)
        {
            return;
        }

        _settings = AppSettings.Load(_settingsPath);
        _statusStore.Reload();
        ReloadStudentList();
        AppendLog($"=== Hot reload: {_selectedStudents.Count} student(s) selected + settings refreshed ===", isError: false);
        SetStatus("已重载", UiTheme.Muted);
    }

    private List<UserRow> LoadUserRows()
    {
        try
        {
            return _userListService.Load();
        }
        catch (Exception ex)
        {
            AppendLog("Failed to read user-list.json: " + ex.Message, isError: true);
            return new List<UserRow>();
        }
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
        AppendLogSafe(e.Line, e.IsError);
        TryHandleCountsLine(e.Line);

        var skip = SkipLine.Match(e.Line);
        if (skip.Success)
        {
            _skippedStudentId = skip.Groups[1].Value;
        }
    }

    /// <summary>
    /// 承接 e2e 跑前探针的读数行：写缓存 + 刷新主页圆点。
    /// 读数失败（week/total 为 -1）不覆盖已有缓存——失败意味着状态未知，不是"归零"。
    /// </summary>
    private void TryHandleCountsLine(string line)
    {
        var match = CountsLine.Match(line);
        if (!match.Success
            || !int.TryParse(match.Groups[2].Value, out var week)
            || !int.TryParse(match.Groups[3].Value, out var total)
            || week < 0 || total < 0)
        {
            return;
        }

        var studentId = match.Groups[1].Value;
        var weekLimit = _settings.WeekLimit;
        var totalLimit = _settings.TotalLimit;
        var atCap = week >= weekLimit || total >= totalLimit;

        try
        {
            BeginInvoke(() =>
            {
                _statusStore.SetFromRead(studentId, week, total);
                UpdateSelectionUi();
                AppendLog(
                    $"[counts] {studentId} week={week}/{weekLimit} total={total}/{totalLimit}" +
                    (atCap ? " → at cap" : ""),
                    isError: false);
                if (atCap && !_batchRunning)
                {
                    AppendLog(
                        $"[counts] {studentId} already at cap; running anyway (manual single run)",
                        isError: false);
                }
            });
        }
        catch (InvalidOperationException)
        {
            // 窗体已关闭。
        }
    }

    /// <summary>线程安全的日志追加（后台线程回调经此封送到 UI 线程）。</summary>
    private void AppendLogSafe(string line, bool isError)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(() => AppendLog(line, isError));
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

        // Exited 在进程 I/O 线程上触发，_batchRunning 是 UI 线程写的，
        // 故把判断放进 BeginInvoke 内，读到的一定是最新值。
        try
        {
            BeginInvoke(() =>
            {
                // 批量运行期间由 RunBatchAsync 统一汇报进度与结果，这里不重复写状态，
                // 否则每个学号结束都会把状态栏刷成「完成/失败」再被下一行覆盖。
                if (_batchRunning)
                {
                    UpdateActionButtons();
                    return;
                }

                UpdateActionButtons();

                var ok = code == 0;
                if (!ok && _stopRequested)
                {
                    // 用户点击「停止」导致的退出（Ctrl+C / 强杀）不算失败。
                    AppendLog("=== Run stopped by user ===", isError: false);
                    SetStatus("已停止", UiTheme.Muted);
                    ToastNotifier.Show(
                        "运行已停止",
                        $"{_activeRunStudentId ?? "本次运行"} 已被手动停止，模拟器实例未关闭。",
                        ToastTone.Neutral);
                    return;
                }

                AppendLog(
                    ok ? $"=== Finished: OK (exit code {code}) ===" : $"=== Finished: FAILED (exit code {code}) ===",
                    isError: !ok);
                SetStatus(
                    ok ? "完成" : "失败",
                    ok ? UiTheme.PaleGreenText : UiTheme.PaleRedText);
                if (ok && !string.IsNullOrEmpty(_activeRunStudentId))
                {
                    // 单次运行成功：本地 +1 让圆点立刻变化；下次跑前读数会覆盖成真实值。
                    _statusStore.IncrementAfterSuccess(_activeRunStudentId);
                    UpdateSelectionUi();
                }

                ToastNotifier.Show(
                    ok ? $"运行完成 · {_activeRunStudentId ?? "任务"}" : $"运行失败 · {_activeRunStudentId ?? "任务"}",
                    ok ? "全部流程已正常结束。" : $"退出码 {code}，请回来查看日志排查。",
                    ok ? ToastTone.Ok : ToastTone.Fail);
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
        _stateTimer.Stop();
        _stateTimer.Dispose();
        _runner.Dispose();
        ProcessRegistry.KillAll(); // 关窗时强杀全部残留子进程（如进行中的脚本流程）

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
