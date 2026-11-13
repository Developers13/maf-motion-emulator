using System.Drawing.Drawing2D;

namespace MafMotionEmulator.Gui;

/// <summary>
/// 玻璃球质感的圆形状态灯画法：灰=未满/未知，黄=本周额度已满，绿=总次数已满。
/// 主页状态条与学号多选对话框（自绘 CheckedListBox）共用同一套绘制。
/// </summary>
internal static class StatusDot
{
    /// <summary>在指定矩形内画一颗玻璃球（高光偏左上，边缘略深）。</summary>
    public static void PaintOrb(Graphics g, RectangleF bounds, Color color)
    {
        if (bounds.Width < 2 || bounds.Height < 2)
        {
            return;
        }

        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = new GraphicsPath();
        path.AddEllipse(bounds);

        var center = new PointF(
            bounds.X + bounds.Width * 0.36f,
            bounds.Y + bounds.Height * 0.30f);
        using (var brush = new PathGradientBrush(path)
               {
                   CenterPoint = center,
                   CenterColor = Lighten(color, 0.55f),
                   SurroundColors = new[] { Darken(color, 0.22f) },
               })
        {
            g.FillPath(brush, path);
        }

        // 高光：左上一个小椭圆，模拟球面反射
        using (var highlight = new SolidBrush(Color.FromArgb(190, Color.White)))
        {
            var hl = new RectangleF(
                bounds.X + bounds.Width * 0.20f,
                bounds.Y + bounds.Height * 0.12f,
                bounds.Width * 0.36f,
                bounds.Height * 0.28f);
            g.FillEllipse(highlight, hl);
        }

        // 边缘：1px 更深的描边，让浅色（灰点）在白底上也有轮廓
        using (var rim = new Pen(Darken(color, 0.30f), 1f))
        {
            g.DrawEllipse(rim, bounds);
        }

        g.SmoothingMode = previous;
    }

    private static Color Lighten(Color color, float amount) => Color.FromArgb(
        color.A,
        (int)Math.Min(255, color.R + (255 - color.R) * amount),
        (int)Math.Min(255, color.G + (255 - color.G) * amount),
        (int)Math.Min(255, color.B + (255 - color.B) * amount));

    private static Color Darken(Color color, float amount) => Color.FromArgb(
        color.A,
        (int)(color.R * (1 - amount)),
        (int)(color.G * (1 - amount)),
        (int)(color.B * (1 - amount)));
}
