using System.Globalization;
using System.Text;
using MafMotionEmulator.Gui.Models;
using MafMotionEmulator.Gui.Services;

namespace MafMotionEmulator.Gui;

public sealed class SettingsForm : FadeForm
{
    private const int PasswordColumnIndex = 1;

    private readonly UserListService _userListService;
    private readonly AppSettings _settings;
    private bool _userListDirty;

    private readonly DataGridView _userGrid = new();
    private readonly DataGridView _envGrid = new();
    private readonly TextBox _pythonBox = new();
    private readonly TextBox _projectRootBox = new();
    private readonly TextBox _mumuManagerBox = new();
    private readonly TextBox _templateVmBox = new();
    private readonly ComboBox _scriptHostCombo = new();
    private static readonly ScriptHost AutoScriptHost = new("自动（推荐）", "");
    private readonly Button _addUserButton;
    private readonly Button _removeUserButton;
    private readonly Button _reloadUserButton;
    private readonly Button _saveUserButton;
    private readonly Button _addEnvButton;
    private readonly Button _removeEnvButton;
    private readonly Button _okButton;
    private readonly Button _cancelButton;

    // MakeBorderedField 每次调用会新建 Panel，这里按 textbox 缓存避免同一输入框被包两次。
    private readonly Dictionary<TextBox, Panel> _fieldPanels = new();

    public AppSettings Settings => _settings;

    public SettingsForm(UserListService userListService, AppSettings settings)
    {
        _userListService = userListService;
        _settings = settings;

        _addUserButton = UiTheme.MakeGhostButton("添加行");
        _removeUserButton = UiTheme.MakeGhostButton("删除行");
        _reloadUserButton = UiTheme.MakeGhostButton("重新加载");
        _saveUserButton = UiTheme.MakeGhostButton("保存");
        _addEnvButton = UiTheme.MakeGhostButton("添加变量");
        _removeEnvButton = UiTheme.MakeGhostButton("删除变量");
        _okButton = UiTheme.MakePrimaryButton("确定");
        _cancelButton = UiTheme.MakeGhostButton("取消");

        InitializeUi();
        LoadUserList();
        LoadSettings();
        _userListDirty = false;
    }

    private void InitializeUi()
    {
        Text = "设置";
        Width = 780;
        Height = 660;
        MinimumSize = new Size(660, 520);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = UiTheme.Canvas;
        Font = UiTheme.Sans;

        // ---- 头部：标题 + user-list.json 路径 ----
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 16, 24, 0),
            BackColor = UiTheme.Canvas,
            ColumnCount = 1,
        };
        var title = UiTheme.MakeTitle("设置", small: true);
        title.Margin = new Padding(0, 0, 0, 4);
        var subtitle = UiTheme.MakeMonoMeta(_userListService.UserListPath);
        var separator = UiTheme.MakeSeparator();
        separator.Dock = DockStyle.Top;
        separator.Margin = new Padding(0, 12, 0, 0);
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(separator);
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ---- 扁平标签页 ----
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(120, 38),
            Padding = new Point(0, 0),
            BackColor = UiTheme.Canvas,
            Font = UiTheme.Sans,
        };
        tabs.DrawItem += OnTabDrawItem;
        tabs.TabPages.Add(BuildUserListTab());
        tabs.TabPages.Add(BuildEnvironmentTab());

        // ---- 底部按钮栏 ----
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            BackColor = UiTheme.Canvas,
        };
        var bottomSeparator = UiTheme.MakeSeparator();
        bottomSeparator.Dock = DockStyle.Top;
        var bottomButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 12, 24, 0),
            BackColor = UiTheme.Canvas,
        };

        _okButton.Click += OnOk;
        _cancelButton.DialogResult = DialogResult.Cancel;

        bottomButtons.Controls.Add(_okButton);   // RTL：第一个在最右
        bottomButtons.Controls.Add(_cancelButton);
        bottom.Controls.Add(bottomButtons);
        bottom.Controls.Add(bottomSeparator);

        Controls.Add(tabs);
        Controls.Add(header);
        Controls.Add(bottom);

        AcceptButton = _okButton;
        CancelButton = _cancelButton;
    }

    private static void OnTabDrawItem(object? sender, DrawItemEventArgs e)
    {
        var tabs = (TabControl)sender!;
        var selected = tabs.SelectedIndex == e.Index;
        using var background = new SolidBrush(selected ? UiTheme.Surface : UiTheme.Canvas);
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics,
            tabs.TabPages[e.Index].Text,
            UiTheme.Sans,
            e.Bounds,
            selected ? UiTheme.Body : UiTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (selected)
        {
            using var ink = new SolidBrush(UiTheme.Ink);
            e.Graphics.FillRectangle(ink, e.Bounds.X + 16, e.Bounds.Bottom - 3, e.Bounds.Width - 32, 2);
        }
    }

    // ---- 用户列表页 ----

    private TabPage BuildUserListTab()
    {
        var page = new TabPage("用户列表") { BackColor = UiTheme.Surface, Padding = new Padding(0) };

        var container = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(20, 16, 20, 0),
        };

        ConfigureUserGrid();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 10),
            BackColor = UiTheme.Surface,
        };

        _addUserButton.Click += OnAddUser;
        _removeUserButton.Click += OnRemoveUser;
        _reloadUserButton.Click += (_, _) =>
        {
            LoadUserList();
            _userListDirty = false;
        };
        _saveUserButton.Click += (_, _) => SaveUserList();

        buttons.Controls.Add(_addUserButton);
        buttons.Controls.Add(_removeUserButton);
        buttons.Controls.Add(_reloadUserButton);
        buttons.Controls.Add(_saveUserButton);

        container.Controls.Add(_userGrid);
        container.Controls.Add(buttons);
        page.Controls.Add(container);
        return page;
    }

    private void ConfigureUserGrid()
    {
        _userGrid.Dock = DockStyle.Fill;
        _userGrid.AllowUserToAddRows = false;
        _userGrid.AllowUserToDeleteRows = false;
        UiTheme.StyleDataGrid(_userGrid);
        _userGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

        _userGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "学号 (student_id)",
            Name = "StudentId",
            FillWeight = 40,
        });
        _userGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "密码 (password)",
            Name = "Password",
            FillWeight = 40,
        });
        _userGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "距离 km（2.4 / 3.2，可留空）",
            Name = "DistanceKm",
            FillWeight = 20,
        });

        _userGrid.CellFormatting += OnUserGridCellFormatting;
        _userGrid.EditingControlShowing += OnUserGridEditingControlShowing;
        _userGrid.CellValueChanged += (_, _) => _userListDirty = true;
        _userGrid.RowsRemoved += (_, _) => _userListDirty = true;
    }

    private void OnUserGridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (_userGrid.Columns[e.ColumnIndex].Name == "Password" && e.Value is string value && value.Length > 0)
        {
            e.Value = new string('●', value.Length);
            e.FormattingApplied = true;
        }
    }

    private void OnUserGridEditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (_userGrid.CurrentCell.ColumnIndex == PasswordColumnIndex && e.Control is TextBox textBox)
        {
            textBox.UseSystemPasswordChar = true;
        }
    }

    private void OnAddUser(object? sender, EventArgs e)
    {
        _userGrid.Rows.Add("", "", "3.2");
        _userListDirty = true;
        var rowIndex = _userGrid.Rows.Count - 1;
        _userGrid.CurrentCell = _userGrid.Rows[rowIndex].Cells[0];
        _userGrid.BeginEdit(selectAll: true);
    }

    private void OnRemoveUser(object? sender, EventArgs e)
    {
        if (_userGrid.CurrentRow is null)
        {
            return;
        }

        var index = _userGrid.CurrentRow.Index;
        _userGrid.Rows.RemoveAt(index);
        _userListDirty = true;
    }

    private void LoadUserList()
    {
        _userGrid.Rows.Clear();
        try
        {
            var rows = _userListService.Load();
            foreach (var row in rows)
            {
                _userGrid.Rows.Add(
                    row.StudentId,
                    row.Password,
                    row.DistanceKm is { } km ? km.ToString(CultureInfo.InvariantCulture) : "");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "读取 user-list.json 失败：" + ex.Message,
                "错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool SaveUserList()
    {
        if (!TryBuildUserRows(out var rows, out var error))
        {
            MessageBox.Show(
                this,
                "用户列表存在以下问题：\n\n" + error,
                "校验失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        try
        {
            _userListService.Save(rows);
            _userListDirty = false;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "保存 user-list.json 失败：" + ex.Message,
                "错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    private bool TryBuildUserRows(out List<UserRow> rows, out string error)
    {
        rows = new List<UserRow>();
        var errors = new StringBuilder();

        for (var i = 0; i < _userGrid.Rows.Count; i++)
        {
            var cells = _userGrid.Rows[i].Cells;
            var studentId = (cells[0].Value?.ToString() ?? "").Trim();
            var password = cells[1].Value?.ToString() ?? "";
            var distanceText = (cells[2].Value?.ToString() ?? "").Trim();

            if (studentId.Length == 0 && password.Length == 0 && distanceText.Length == 0)
            {
                continue; // 跳过整行为空的行
            }

            if (studentId.Length == 0)
            {
                errors.AppendLine($"第 {i + 1} 行：学号不能为空");
                continue;
            }

            double? distance = null;
            if (distanceText.Length > 0)
            {
                if (!double.TryParse(distanceText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    errors.AppendLine($"第 {i + 1} 行：距离必须是数字（可留空，或填 2.4 / 3.2）");
                    continue;
                }

                if (Math.Abs(parsed - 2.4) > 0.001 && Math.Abs(parsed - 3.2) > 0.001)
                {
                    errors.AppendLine($"第 {i + 1} 行：距离仅支持 2.4 或 3.2（留空表示未设置）");
                    continue;
                }

                distance = parsed;
            }

            rows.Add(new UserRow { StudentId = studentId, Password = password, DistanceKm = distance });
        }

        error = errors.ToString();
        return error.Length == 0;
    }

    // ---- 运行环境页 ----

    private TabPage BuildEnvironmentTab()
    {
        var page = new TabPage("运行环境") { BackColor = UiTheme.Surface, Padding = new Padding(0) };

        var container = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(20, 16, 20, 0),
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
            BackColor = UiTheme.Surface,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        }
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        panel.Controls.Add(FieldLabel("Python 启动器"), 0, 0);
        panel.Controls.Add(FieldLabel("项目根目录"), 0, 1);
        panel.Controls.Add(FieldLabel("MuMu 管理器"), 0, 2);
        panel.Controls.Add(FieldLabel("模板虚拟机"), 0, 3);
        panel.Controls.Add(FieldLabel("脚本执行器"), 0, 4);
        panel.Controls.Add(WrapField(_pythonBox), 1, 0);
        panel.Controls.Add(WrapField(_projectRootBox), 1, 1);
        panel.Controls.Add(WrapField(_mumuManagerBox), 1, 2);
        panel.Controls.Add(WrapField(_templateVmBox), 1, 3);
        panel.Controls.Add(_scriptHostCombo, 1, 4);

        var envLabel = UiTheme.MakeMutedLabel(
            "运行时传给脚本的额外环境变量（MAF_USER / MAF_PASS / MAF_LAPS / MAF_TARGET_KM 由脚本依据 user-list.json 自动注入，无需在此填写）：");
        envLabel.Margin = new Padding(0, 10, 0, 6);
        envLabel.AutoSize = true;
        panel.Controls.Add(envLabel, 0, 5);
        panel.SetColumnSpan(envLabel, 2);

        ConfigureEnvGrid();
        panel.Controls.Add(_envGrid, 0, 6);
        panel.SetColumnSpan(_envGrid, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = UiTheme.Surface,
            Padding = new Padding(0, 10, 0, 12),
        };
        _addEnvButton.Click += (_, _) => _envGrid.Rows.Add("", "");
        _removeEnvButton.Click += (_, _) =>
        {
            if (_envGrid.CurrentRow is not null)
            {
                _envGrid.Rows.RemoveAt(_envGrid.CurrentRow.Index);
            }
        };
        buttons.Controls.Add(_addEnvButton);
        buttons.Controls.Add(_removeEnvButton);
        panel.Controls.Add(buttons, 0, 7);
        panel.SetColumnSpan(buttons, 2);

        container.Controls.Add(panel);
        page.Controls.Add(container);
        return page;
    }

    private Control WrapField(TextBox box)
    {
        if (!_fieldPanels.TryGetValue(box, out var panel))
        {
            panel = UiTheme.MakeBorderedField(box);
            panel.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            panel.Margin = new Padding(0, 4, 0, 0);
            box.Font = UiTheme.Sans;
            _fieldPanels[box] = panel;
        }

        return panel;
    }

    private static Label FieldLabel(string text)
    {
        var label = UiTheme.MakeMutedLabel(text);
        label.Margin = new Padding(0, 10, 12, 0);
        return label;
    }

    /// <summary>
    /// 扫描已安装的 PowerShell 宿主填充下拉框；选中已保存的宿主，
    /// 保存值已卸载/不存在时显示「（未找到）」项，避免配置悄悄丢失。
    /// </summary>
    private void PopulateScriptHosts()
    {
        _scriptHostCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _scriptHostCombo.Font = UiTheme.Sans;
        _scriptHostCombo.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        _scriptHostCombo.Margin = new Padding(0, 6, 0, 0);
        _scriptHostCombo.Items.Clear();
        _scriptHostCombo.Items.Add(AutoScriptHost);
        foreach (var host in ScriptHostScanner.Scan())
        {
            _scriptHostCombo.Items.Add(host);
        }

        var saved = _settings.ScriptHostPath;
        var selected = _scriptHostCombo.Items.OfType<ScriptHost>()
            .FirstOrDefault(h => h.Path.Equals(saved, StringComparison.OrdinalIgnoreCase));
        if (selected is null && !string.IsNullOrWhiteSpace(saved))
        {
            selected = new ScriptHost($"{saved}（未找到）", saved);
            _scriptHostCombo.Items.Add(selected);
        }

        _scriptHostCombo.SelectedItem = selected ?? AutoScriptHost;
    }

    private void ConfigureEnvGrid()
    {
        _envGrid.Dock = DockStyle.Fill;
        _envGrid.AllowUserToAddRows = false;
        _envGrid.AllowUserToDeleteRows = false;
        UiTheme.StyleDataGrid(_envGrid);
        _envGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;

        _envGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "键", Name = "Key", FillWeight = 40 });
        _envGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "值", Name = "Value", FillWeight = 60 });
    }

    private void LoadSettings()
    {
        _pythonBox.Text = _settings.PythonLauncher;
        _projectRootBox.Text = string.IsNullOrWhiteSpace(_settings.ProjectRoot)
            ? ProjectLocator.FindProjectRoot()
            : _settings.ProjectRoot;
        _mumuManagerBox.Text = _settings.MuMuManagerPath;
        _templateVmBox.Text = _settings.TemplateVmName;
        PopulateScriptHosts();

        _envGrid.Rows.Clear();
        foreach (var pair in _settings.Environment)
        {
            _envGrid.Rows.Add(pair.Key, pair.Value);
        }
    }

    private void ApplySettings()
    {
        _settings.PythonLauncher = _pythonBox.Text.Trim();
        _settings.ProjectRoot = _projectRootBox.Text.Trim();
        _settings.MuMuManagerPath = string.IsNullOrWhiteSpace(_mumuManagerBox.Text)
            ? @"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe"
            : _mumuManagerBox.Text.Trim();
        _settings.TemplateVmName = string.IsNullOrWhiteSpace(_templateVmBox.Text) ? "模板" : _templateVmBox.Text.Trim();
        _settings.ScriptHostPath = _scriptHostCombo.SelectedItem is ScriptHost host ? host.Path : "";

        var environment = new Dictionary<string, string>();
        foreach (DataGridViewRow row in _envGrid.Rows)
        {
            var key = (row.Cells[0].Value?.ToString() ?? "").Trim();
            var value = row.Cells[1].Value?.ToString() ?? "";
            if (key.Length == 0)
            {
                continue; // 跳过空键
            }

            environment[key] = value;
        }

        _settings.Environment = environment;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        if (_userListDirty && !SaveUserList())
        {
            return;
        }

        ApplySettings();
        DialogResult = DialogResult.OK;
        Close();
    }
}
