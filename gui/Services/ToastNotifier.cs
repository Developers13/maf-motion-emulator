using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace MafMotionEmulator.Gui.Services;

/// <summary>通知语气：决定标题前缀图标。</summary>
internal enum ToastTone
{
    Ok,
    Fail,
    Neutral,
}

/// <summary>
/// Windows 通知推送：长流程（运行/准备）结束时告知用户，成功失败都推。
/// 免安装可用 —— ToastNotificationManagerCompat 首次推送会自动在用户开始菜单
/// 创建带 AUMID 的快捷方式，通知归属本应用。
/// 本 GUI 恒以管理员身份运行（Program.cs runAs），提权进程上 Compat 通道可能被
/// 通知平台拒收，此时退回 WinRT 直推并借用 PowerShell 的已注册 AUMID
/// （BurntToast 在提权 PowerShell 下的同款做法）；仍失败则静默放弃——
/// 结果在日志、状态栏与弹窗里都有，通知只是走开时的提醒。
/// </summary>
internal static class ToastNotifier
{
    private const string FallbackAumid =
        @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    public static void Show(string title, string message, ToastTone tone)
    {
        var fullTitle = tone switch
        {
            ToastTone.Ok => "✅ " + title,
            ToastTone.Fail => "❌ " + title,
            _ => "⏹ " + title,
        };

        try
        {
            new ToastContentBuilder()
                .AddText(fullTitle)
                .AddText(message)
                .Show();
            return;
        }
        catch (Exception)
        {
            // 落到备用通道。
        }

        try
        {
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var texts = xml.GetElementsByTagName("text");
            texts[0].AppendChild(xml.CreateTextNode(fullTitle));
            texts[1].AppendChild(xml.CreateTextNode(message));
            ToastNotificationManager.CreateToastNotifier(FallbackAumid).Show(new ToastNotification(xml));
        }
        catch (Exception)
        {
            // 通知推不出去不影响任何结果呈现，静默放弃。
        }
    }
}
