using System.Runtime.InteropServices;

namespace MafMotionEmulator.Gui;

/// <summary>
/// Windows 11 系统动画风格的淡入淡出：窗口/对话框打开渐显、关闭渐隐。
/// 尊重系统「显示动画效果」设置（SPI_GETCLIENTAREAANIMATION），关闭时跳过动画。
/// </summary>
internal static class UiAnimator
{
    private const int SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    private static readonly bool SystemAnimationsEnabled = QuerySystemAnimations();

    /// <summary>系统动画被关闭（辅助功能）时所有动画自动跳过。</summary>
    public static bool Enabled => SystemAnimationsEnabled;

    private static bool QuerySystemAnimations()
    {
        var value = true;
        return SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref value, 0) ? value : true;
    }

    /// <summary>渐显（约 180ms，ease-out）。系统动画关闭时直接置为不透明。</summary>
    public static void FadeIn(Form form, int durationMs = 180)
    {
        if (!Enabled || form.IsDisposed)
        {
            form.Opacity = 1;
            return;
        }

        Animate(form, fromOpacity: 0, toOpacity: 1, durationMs);
    }

    /// <summary>渐隐（约 140ms），完成后回调（通常用于真正 Close 窗体）。</summary>
    public static void FadeOut(Form form, int durationMs = 140, Action? completed = null)
    {
        if (!Enabled || form.IsDisposed)
        {
            completed?.Invoke();
            return;
        }

        Animate(form, fromOpacity: 1, toOpacity: 0, durationMs, completed);
    }

    /// <summary>UI 线程 Timer 驱动的透明度动画，ease-out cubic 插值。</summary>
    private static void Animate(Form form, double fromOpacity, double toOpacity, int durationMs, Action? completed = null)
    {
        if (form.IsDisposed)
        {
            return;
        }

        form.Opacity = fromOpacity;
        var timer = new System.Windows.Forms.Timer { Interval = 15 }; // ~60fps
        var startedAt = Environment.TickCount64;

        timer.Tick += (_, _) =>
        {
            if (form.IsDisposed || form.Disposing)
            {
                timer.Stop();
                timer.Dispose();
                return;
            }

            var elapsed = (int)Math.Min(Environment.TickCount64 - startedAt, durationMs);
            var t = durationMs == 0 ? 1 : (double)elapsed / durationMs;
            var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
            form.Opacity = fromOpacity + (toOpacity - fromOpacity) * eased;

            if (elapsed >= durationMs)
            {
                form.Opacity = toOpacity;
                timer.Stop();
                timer.Dispose();
                completed?.Invoke();
            }
        };

        timer.Start();
    }
}

/// <summary>
/// 带 Win11 风格开合动画的窗体基类：加载前预置透明、Shown 渐显、FormClosing 渐隐后关闭。
/// 所有窗体/对话框继承本类即可获得统一的系统动画手感。
/// </summary>
public class FadeForm : Form
{
    private bool _fadeClosing;

    protected override void OnLoad(EventArgs e)
    {
        // 首帧绘制前预置为全透明，避免渐显前闪现一帧不透明窗口。
        if (UiAnimator.Enabled)
        {
            Opacity = 0;
        }

        base.OnLoad(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UiAnimator.FadeIn(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 首次关闭请求：取消 → 渐隐 → 再次 Close（此时 _fadeClosing=true 放行）。
        if (!_fadeClosing && UiAnimator.Enabled && Visible)
        {
            // 记下本次关闭请求的 DialogResult：ShowDialog 在被取消的关闭请求上就返回，
            // 等渐隐结束真正关闭时窗体的 DialogResult 已被重置为 None。
            var result = DialogResult;
            e.Cancel = true;
            _fadeClosing = true;
            UiAnimator.FadeOut(this, completed: () =>
            {
                DialogResult = result;
                Close();
            });
            return;
        }

        base.OnFormClosing(e);
    }
}
