using System.Drawing;
using System.Drawing.Text;

namespace MafMotionEmulator.Gui;

/// <summary>
/// Minimalist-ui 主题（暖色单色 + 语义淡彩），所有窗体的视觉常量与控件工厂。
/// 色板约定：骨白画布、白色卡片、1px 浅灰边框、墨黑主按钮；颜色只用于语义。
/// </summary>
internal static class UiTheme
{
    // ---- 色板 ----

    public static readonly Color Canvas = Color.FromArgb(0xF7, 0xF6, 0xF3);   // 骨白画布
    public static readonly Color Surface = Color.White;                        // 卡片/输入框
    public static readonly Color Ink = Color.FromArgb(0x11, 0x11, 0x11);      // 主墨色（标题/主按钮）
    public static readonly Color Body = Color.FromArgb(0x2F, 0x34, 0x37);     // 正文炭黑
    public static readonly Color Muted = Color.FromArgb(0x78, 0x77, 0x74);    // 次级灰
    public static readonly Color Border = Color.FromArgb(0xEA, 0xEA, 0xEA);   // 1px 分隔/边框
    public static readonly Color HoverFill = Color.FromArgb(0xF1, 0xF0, 0xED); // ghost hover

    public static readonly Color PaleRedBg = Color.FromArgb(0xFD, 0xEB, 0xEC);
    public static readonly Color PaleRedText = Color.FromArgb(0x9F, 0x2F, 0x2D);
    public static readonly Color PaleBlueBg = Color.FromArgb(0xE1, 0xF3, 0xFE);
    public static readonly Color PaleBlueText = Color.FromArgb(0x1F, 0x6C, 0x9F);
    public static readonly Color PaleGreenBg = Color.FromArgb(0xED, 0xF3, 0xEC);
    public static readonly Color PaleGreenText = Color.FromArgb(0x34, 0x65, 0x38);

    // 状态圆点（玻璃球质感）：灰=未满/未知，黄=本周额度已满，绿=总次数已满。
    public static readonly Color DotOff = Color.FromArgb(0xC8, 0xC7, 0xC2);
    public static readonly Color DotWeek = Color.FromArgb(0xE0, 0xA2, 0x12);
    public static readonly Color DotTotal = Color.FromArgb(0x3F, 0x9E, 0x4A);

    // ---- 字体 ----

    public static readonly Font Sans = new("Segoe UI", 9.75f);
    public static readonly Font SansSmall = new("Segoe UI", 9f);
    public static readonly Font SerifTitle = new("Georgia", 17f, FontStyle.Bold);
    public static readonly Font SerifTitleSmall = new("Georgia", 14f, FontStyle.Bold);

    // 私有字体集合必须保持被引用（GDI+ 字体句柄依赖它），故存于静态字段。
    // 注意声明顺序：字段初始化按声明顺序执行，BundledMono 必须在 Mono 之前。
    private static readonly PrivateFontCollection? BundledMono = LoadBundledMono();
    public static readonly Font Mono = CreateMono();

    private static Font CreateMono()
    {
        // JetBrains Mono 优先：先取随程序打包的私有字体（assets/fonts），
        // 再找系统已装的 JetBrains Mono / Cascadia Mono；都缺失时回退 Consolas。
        if (BundledMono is { Families.Length: > 0 } collection)
        {
            return new Font(collection.Families[0], 9.5f);
        }

        var familyName = TryCreateFamily("JetBrains Mono")
                         ?? TryCreateFamily("Cascadia Mono")
                         ?? "Consolas";
        return new Font(familyName, 9.5f);
    }

    private static PrivateFontCollection? LoadBundledMono()
    {
        try
        {
            var regular = Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "JetBrainsMono-Regular.ttf");
            if (!File.Exists(regular))
            {
                return null;
            }

            var collection = new PrivateFontCollection { };
            collection.AddFontFile(regular);
            var bold = Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "JetBrainsMono-Bold.ttf");
            if (File.Exists(bold))
            {
                collection.AddFontFile(bold);
            }

            return collection;
        }
        catch
        {
            return null; // 字体文件损坏/被占用时回退系统字体
        }
    }

    private static string? TryCreateFamily(string name)
    {
        try
        {
            using var family = new FontFamily(name);
            return name;
        }
        catch
        {
            return null;
        }
    }

    // ---- 控件工厂 ----

    /// <summary>主按钮：墨黑底白字，无边框，hover 深灰。</summary>
    public static Button MakePrimaryButton(string text)
    {
        return new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Ink,
            ForeColor = Color.White,
            Font = Sans,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 4, 0, 0),
        }.WithFlatColors(
            mouseOver: Color.FromArgb(0x33, 0x33, 0x33),
            mouseDown: Color.FromArgb(0x44, 0x44, 0x44));
    }

    /// <summary>次级按钮：白底 1px 浅灰边框，炭黑文字。</summary>
    public static Button MakeGhostButton(string text)
    {
        return new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Body,
            Font = Sans,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 4, 0, 0),
        }.WithFlatColors(
            mouseOver: HoverFill,
            mouseDown: Border,
            borderColor: Border);
    }

    /// <summary>危险按钮（停止）：淡红底深红字；禁用时置灰由 WinForms 自行处理。</summary>
    public static Button MakeDangerButton(string text)
    {
        return new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = PaleRedBg,
            ForeColor = PaleRedText,
            Font = Sans,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 4, 0, 0),
        }.WithFlatColors(
            mouseOver: Color.FromArgb(0xFA, 0xDD, 0xDF),
            mouseDown: Color.FromArgb(0xF6, 0xD2, 0xD5),
            borderColor: PaleRedBg);
    }

    private static Button WithFlatColors(
        this Button button,
        Color mouseOver,
        Color mouseDown,
        Color? borderColor = null)
    {
        button.FlatAppearance.MouseOverBackColor = mouseOver;
        button.FlatAppearance.MouseDownBackColor = mouseDown;
        button.FlatAppearance.BorderColor = borderColor ?? button.BackColor;
        return button;
    }

    /// <summary>1px 浅灰分隔线。</summary>
    public static Panel MakeSeparator()
    {
        return new Panel
        {
            Height = 1,
            BackColor = Border,
        };
    }

    /// <summary>衬线标题标签。</summary>
    public static Label MakeTitle(string text, bool small = false)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = small ? SerifTitleSmall : SerifTitle,
            ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 2),
        };
    }

    /// <summary>等宽元信息标签（路径等）。</summary>
    public static Label MakeMonoMeta(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = Mono,
            ForeColor = Muted,
            Margin = new Padding(0, 2, 0, 0),
        };
    }

    /// <summary>次级说明标签。</summary>
    public static Label MakeMutedLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = SansSmall,
            ForeColor = Muted,
        };
    }

    /// <summary>1px 浅灰边框输入框（用外层 Panel 模拟边框）。</summary>
    public static Panel MakeBorderedField(TextBox textBox)
    {
        textBox.BorderStyle = BorderStyle.None;
        textBox.BackColor = Surface;
        textBox.ForeColor = Body;
        textBox.Font = Sans;

        var panel = new Panel
        {
            BackColor = Border,
            Padding = new Padding(1),
            Height = textBox.PreferredHeight + 2 + 6,
        };
        textBox.Location = new Point(2, 4);
        textBox.Width = panel.Width - 4;
        textBox.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        panel.Controls.Add(textBox);
        panel.Resize += (_, _) => textBox.Width = panel.Width - 4;
        return panel;
    }

    /// <summary>DataGridView 极简样式：白底、浅灰网格线、骨白表头、淡蓝选中。</summary>
    public static void StyleDataGrid(DataGridView grid)
    {
        grid.BorderStyle = BorderStyle.None;
        grid.BackgroundColor = Surface;
        grid.GridColor = Border;
        grid.RowHeadersVisible = false;
        grid.EnableHeadersVisualStyles = false;
        grid.AllowUserToResizeRows = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        grid.ColumnHeadersDefaultCellStyle.BackColor = Canvas;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.Font = SansSmall;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Canvas;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 36;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;

        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Body;
        grid.DefaultCellStyle.SelectionBackColor = PaleBlueBg;
        grid.DefaultCellStyle.SelectionForeColor = Body;
        grid.DefaultCellStyle.Font = Sans;
        grid.DefaultCellStyle.Padding = new Padding(4, 4, 4, 4);
        grid.RowTemplate.Height = 34;
    }

    // ---- 日志语义色 ----

    public static Color LogDefault => Body;
    public static Color LogMeta => Muted;        // 时间戳
    public static Color LogInfo => PaleBlueText;  // [e2e] 行
    public static Color LogOk => PaleGreenText;   // 成功
    public static Color LogError => PaleRedText;  // 失败/错误
}
