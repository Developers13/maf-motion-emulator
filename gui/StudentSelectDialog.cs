using System.Drawing;
using MafMotionEmulator.Gui.Services;

namespace MafMotionEmulator.Gui;

/// <summary>
/// 学号多选对话框：枚举 user-list.json 中的全部学号供勾选，并在每行右侧用两个
/// 圆点标出「本周额度已满（黄）/ 总次数已满（绿）」；已达上限的学号仍可勾选（手动强制运行）。
/// Selected 返回确定时勾选的学号（保持传入顺序）；一个都没勾时「确定」不可用。
/// </summary>
public sealed class StudentSelectDialog : FadeForm
{
    private readonly CheckedListBox _list = new();
    private readonly Button _okButton;
    private readonly ExerciseStatusStore _statusStore;
    private readonly int _weekLimit;
    private readonly int _totalLimit;
    private bool _syncingItems; // 程序化勾选（全选/清空）期间抑制按钮态刷新

    /// <summary>确定时勾选的学号（传入顺序）。</summary>
    public List<string> Selected { get; private set; } = new();

    public StudentSelectDialog(
        IReadOnlyList<string> students,
        IReadOnlyCollection<string> checkedIds,
        ExerciseStatusStore statusStore,
        int weekLimit,
        int totalLimit)
    {
        _statusStore = statusStore;
        _weekLimit = weekLimit;
        _totalLimit = totalLimit;
        Text = "选择学号";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 470);
        BackColor = UiTheme.Canvas;
        Font = UiTheme.Sans;

        _okButton = UiTheme.MakePrimaryButton("确定");

        // ---- 标题 + 分隔线 ----
        var title = UiTheme.MakeTitle("选择学号", small: true);
        title.Margin = new Padding(0, 0, 0, 4);
        var separator = UiTheme.MakeSeparator();
        separator.Margin = new Padding(0, 10, 0, 0);
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(16, 14, 16, 0),
            BackColor = UiTheme.Canvas,
        };
        header.Controls.Add(title);
        header.Controls.Add(separator);

        // ---- 学号勾选列表（1px 边框卡片）----
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Border,
            Padding = new Padding(1),
        };
        _list.Dock = DockStyle.Fill;
        _list.BorderStyle = BorderStyle.None;
        _list.CheckOnClick = true;
        _list.Font = UiTheme.Mono;
        _list.BackColor = UiTheme.Surface;
        _list.ForeColor = UiTheme.Body;
        // 自绘：原生 CheckedListBox 无法按项上色，这里自己画复选框 + 学号 + 两个状态圆点。
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = 30;
        _list.DrawItem += DrawStudentItem;
        // ItemCheck 触发时新状态尚未生效，等消息循环空闲后再按真实勾选数刷新「确定」。
        _list.ItemCheck += (_, _) =>
        {
            if (_syncingItems || !IsHandleCreated)
            {
                return;
            }

            BeginInvoke(RefreshOkEnabled);
        };
        card.Controls.Add(_list);

        foreach (var id in students)
        {
            _list.Items.Add(id, checkedIds.Contains(id));
        }

        RefreshOkEnabled();
        // 显式设 DialogResult 再 Close：FadeForm 的关闭动画会取消第一次关闭请求，
        // ShowDialog 在那次被取消的请求上就返回，只靠按钮的 DialogResult 属性读到的是 None。
        _okButton.Click += (_, _) =>
        {
            Selected = _list.CheckedItems.Cast<object>().Select(o => (string)o).ToList();
            DialogResult = DialogResult.OK;
            Close();
        };

        // ---- 底部：全选/清空 + 取消/确定 ----
        var toggleAll = UiTheme.MakeGhostButton("全选 / 清空");
        toggleAll.Click += (_, _) =>
        {
            var checkAll = _list.CheckedIndices.Count < students.Count;
            _syncingItems = true;
            for (var i = 0; i < _list.Items.Count; i++)
            {
                _list.SetItemChecked(i, checkAll);
            }

            _syncingItems = false;
            RefreshOkEnabled();
        };

        var cancelButton = UiTheme.MakeGhostButton("取消");
        cancelButton.DialogResult = DialogResult.Cancel;

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            BackColor = UiTheme.Canvas,
            Padding = new Padding(16, 10, 16, 14),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(toggleAll, 0, 0);
        bottom.Controls.Add(new Label(), 1, 0);
        cancelButton.Margin = new Padding(0, 0, 8, 0);
        bottom.Controls.Add(cancelButton, 2, 0);
        bottom.Controls.Add(_okButton, 3, 0);

        // Dock 顺序：Fill 控件先加入（最靠后），再叠 Top / Bottom。
        Controls.Add(card);
        Controls.Add(header);
        Controls.Add(bottom);

        AcceptButton = _okButton;
        CancelButton = cancelButton;
    }

    private void RefreshOkEnabled() => _okButton.Enabled = _list.CheckedIndices.Count > 0;

    /// <summary>自绘一行：复选框 + 学号 + 两个状态圆点（黄=本周满，绿=总数满）。</summary>
    private void DrawStudentItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count)
        {
            return;
        }

        var studentId = (string)_list.Items[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var background = new SolidBrush(selected ? UiTheme.PaleBlueBg : UiTheme.Surface))
        {
            e.Graphics.FillRectangle(background, e.Bounds);
        }

        var check = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + (e.Bounds.Height - 14) / 2, 14, 14);
        ControlPaint.DrawCheckBox(
            e.Graphics,
            check,
            _list.GetItemChecked(e.Index) ? ButtonState.Checked : ButtonState.Normal);

        const int dot = 11;
        const int dotGap = 4;
        var dotsWidth = dot * 2 + dotGap;
        var textWidth = Math.Max(0, e.Bounds.Width - (check.Right - e.Bounds.X) - 12 - dotsWidth - 12);
        var textRect = new Rectangle(check.Right + 8, e.Bounds.Y, textWidth, e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            studentId,
            UiTheme.Mono,
            textRect,
            UiTheme.Body,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        _statusStore.TryGet(studentId, out var status);
        var weekColor = status is not null && _statusStore.WeekIsCurrent(status) && status.Week >= _weekLimit
            ? UiTheme.DotWeek
            : UiTheme.DotOff;
        var totalColor = status is not null && status.Total >= _totalLimit
            ? UiTheme.DotTotal
            : UiTheme.DotOff;

        var dotY = e.Bounds.Y + (e.Bounds.Height - dot) / 2f;
        var dotX = e.Bounds.Right - dotsWidth - 10;
        StatusDot.PaintOrb(e.Graphics, new RectangleF(dotX, dotY, dot, dot), weekColor);
        StatusDot.PaintOrb(e.Graphics, new RectangleF(dotX + dot + dotGap, dotY, dot, dot), totalColor);

        if (selected)
        {
            e.DrawFocusRectangle();
        }
    }
}
