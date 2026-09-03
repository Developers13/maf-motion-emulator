namespace MafMotionEmulator.Gui;

/// <summary>
/// 新建虚拟机对话框：输入学号，校验为纯数字 4–20 位。
/// </summary>
public sealed class NewVmDialog : Form
{
    private readonly TextBox _studentIdBox = new();
    private readonly Button _okButton;
    private readonly Button _cancelButton;

    public NewVmDialog()
    {
        _okButton = UiTheme.MakePrimaryButton("确定");
        _cancelButton = UiTheme.MakeGhostButton("取消");
        InitializeUi();
    }

    public string? StudentId
    {
        get
        {
            var value = _studentIdBox.Text.Trim();
            return value.Length == 0 ? null : value;
        }
    }

    private void InitializeUi()
    {
        Text = "新建虚拟机";
        ClientSize = new Size(420, 190);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UiTheme.Canvas;
        Font = UiTheme.Sans;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24, 18, 24, 12),
            BackColor = UiTheme.Canvas,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = UiTheme.MakeTitle("新建虚拟机", small: true);
        title.Margin = new Padding(0, 0, 0, 6);

        var instruction = UiTheme.MakeMutedLabel("输入学号，克隆“模板”虚拟机并改名为该学号：");
        instruction.Margin = new Padding(0, 0, 0, 4);

        var field = UiTheme.MakeBorderedField(_studentIdBox);
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0, 4, 0, 0);
        _studentIdBox.Font = UiTheme.Mono;

        var hint = UiTheme.MakeMutedLabel("学号须为纯数字，4–20 位；克隆后不会自动启动。");
        hint.Margin = new Padding(0, 8, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = UiTheme.Canvas,
        };

        _okButton.Click += OnOk;
        _cancelButton.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(_okButton);   // RTL：第一个在最右
        buttons.Controls.Add(_cancelButton);

        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(instruction, 0, 1);
        panel.Controls.Add(field, 0, 2);
        panel.Controls.Add(hint, 0, 3);
        panel.Controls.Add(buttons, 0, 4);

        Controls.Add(panel);
        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        _studentIdBox.KeyPress += (_, e) =>
        {
            if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar))
            {
                e.Handled = true; // 只允许数字
            }
        };
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var value = StudentId;
        if (value is null)
        {
            MessageBox.Show(this, "请输入学号。", "新建虚拟机", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (value.Length is < 4 or > 20)
        {
            MessageBox.Show(this, "学号须为 4–20 位数字。", "新建虚拟机", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
