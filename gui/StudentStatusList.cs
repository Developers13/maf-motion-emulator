using MafMotionEmulator.Gui.Services;

namespace MafMotionEmulator.Gui;

/// <summary>
/// 主页「学号 + 两个状态圆点」横条：只显示当前勾选的学号。
/// 第一个点=本周额度（黄=已达上限），第二个点=总次数额度（绿=已达上限）；
/// 未满或读数未知时为灰。数字不进界面，鼠标悬停才用 tooltip 显示。
/// </summary>
public sealed class StudentStatusList : FlowLayoutPanel
{
    private readonly ToolTip _tip = new()
    {
        InitialDelay = 300,
        ReshowDelay = 100,
        AutoPopDelay = 8000,
    };

    public StudentStatusList()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = true;
        Dock = DockStyle.Top;
        FlowDirection = FlowDirection.LeftToRight;
        BackColor = UiTheme.Canvas;
        Margin = new Padding(0, 6, 0, 0);
        TabStop = false;
    }

    public void Rebuild(IReadOnlyList<string> studentIds, ExerciseStatusStore store, int weekLimit, int totalLimit)
    {
        SuspendLayout();
        try
        {
            while (Controls.Count > 0)
            {
                var old = Controls[0];
                Controls.RemoveAt(0);
                old.Dispose();
            }

            foreach (var id in studentIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                store.TryGet(id, out var status);
                var chip = new StudentStatusChip(id, status, store, weekLimit, totalLimit);
                _tip.SetToolTip(chip, BuildTip(status, store, weekLimit, totalLimit));
                Controls.Add(chip);
            }

            Visible = Controls.Count > 0;
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    private static string BuildTip(
        ExerciseStatus? status,
        ExerciseStatusStore store,
        int weekLimit,
        int totalLimit)
    {
        if (status is null)
        {
            return $"本周 —/{weekLimit} · 总 —/{totalLimit}\n未读取过（开始运行时会先读 App 次数）";
        }

        var weekText = store.WeekIsCurrent(status) ? status.Week.ToString() : "—";
        var tip = $"本周 {weekText}/{weekLimit} · 总 {status.Total}/{totalLimit}";
        if (!store.WeekIsCurrent(status))
        {
            tip += "\n（本周次数待重读：上次读数属于上一自然周）";
        }

        return tip + $"\n读取于 {status.ReadAt.LocalDateTime:MM-dd HH:mm}";
    }

    /// <summary>单行：学号 + 两个圆点。</summary>
    private sealed class StudentStatusChip : Control
    {
        private const int DotSize = 11;
        private const int DotGap = 4;
        private const int TextDotGap = 8;

        private readonly string _studentId;
        private readonly Color _weekColor;
        private readonly Color _totalColor;

        public StudentStatusChip(
            string studentId,
            ExerciseStatus? status,
            ExerciseStatusStore store,
            int weekLimit,
            int totalLimit)
        {
            _studentId = studentId;
            _weekColor = status is not null && store.WeekIsCurrent(status) && status.Week >= weekLimit
                ? UiTheme.DotWeek
                : UiTheme.DotOff;
            _totalColor = status is not null && status.Total >= totalLimit
                ? UiTheme.DotTotal
                : UiTheme.DotOff;

            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;

            var textWidth = TextRenderer.MeasureText(studentId, UiTheme.Mono).Width;
            Height = 20;
            Width = textWidth + TextDotGap + DotSize * 2 + DotGap;
            Margin = new Padding(0, 0, 18, 2);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var textHeight = TextRenderer.MeasureText(_studentId, UiTheme.Mono).Height;
            TextRenderer.DrawText(
                e.Graphics,
                _studentId,
                UiTheme.Mono,
                new Point(0, (Height - textHeight) / 2),
                UiTheme.Body,
                TextFormatFlags.NoPadding);

            var x = Width - DotSize * 2 - DotGap;
            var y = (Height - DotSize) / 2f;
            StatusDot.PaintOrb(e.Graphics, new RectangleF(x, y, DotSize, DotSize), _weekColor);
            StatusDot.PaintOrb(e.Graphics, new RectangleF(x + DotSize + DotGap, y, DotSize, DotSize), _totalColor);
        }
    }
}
