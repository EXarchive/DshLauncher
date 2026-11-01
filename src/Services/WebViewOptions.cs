using Microsoft.Web.WebView2.Core;

namespace DshLauncher.Services;

/// <summary>
/// WebView2 环境选项工厂。
///
/// 当 <paramref name="cdpPort"/> &gt; 0 时，为内嵌视图打开 Chromium 远程调试端口。
/// 这样 <c>dsh-cdp</c> 插件就能通过 CDP 附加到这个视图，让监督 Agent **观察并操作
/// 用户眼前这个界面**——也就是参考工作流里「agent 通过 CDP 操纵另一个 agent」那一环。
///
/// 默认是关闭的（端口 0）：打开远程调试意味着本机任意进程都能脚本化这个视图，
/// 必须是用户显式选择，不能是默认行为。
/// </summary>
public static class WebViewOptions
{
    /// <summary>返回环境选项；cdpPort &lt;= 0 时返回 null（用 WebView2 默认行为）。</summary>
    public static CoreWebView2EnvironmentOptions? ForPort(int cdpPort)
    {
        if (cdpPort <= 0) return null;

        return new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = $"--remote-debugging-port={cdpPort}"
        };
    }
}
