using System.Text.Json.Serialization;

namespace DshLauncher.Models;

public class LauncherConfig
{
    public string DshPath { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string CustomArguments { get; set; } = "web";
    public int WebPort { get; set; } = 3080;
    public string NodePath { get; set; } = string.Empty;
    public bool EnableProxy { get; set; } = false;
    public string ProxyAddress { get; set; } = "http://127.0.0.1:7891";
    public bool AutoOpenBrowser { get; set; } = true;
    public bool AutoRestartOnCrash { get; set; } = false;

    /// <summary>
    /// 启动 DSH 时使用的权限级别（普通 / 管理员 / SYSTEM）。
    /// 以字符串持久化：以后往枚举里插入新成员时，已有配置的含义不会被悄悄改掉。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PrivilegeLevel Privilege { get; set; } = PrivilegeLevel.Normal;

    /// <summary>
    /// 是否隐藏 DSH 子进程的控制台窗口（默认隐藏）。
    /// 取消勾选会开一个真实控制台窗口 —— 管理员 / SYSTEM 模式下这是唯一能直接看到
    /// node 原始输出的方式（提权进程无法用管道读，只能落文件）。
    /// </summary>
    public bool HideConsole { get; set; } = true;

    /// <summary>
    /// 是否隐藏底部控制台面板（把纵向空间让给内嵌浏览器）。
    /// 工具栏的「📋 日志」和面板上的「⯆ 隐藏」改的都是这个值。
    /// </summary>
    public bool HideLogPanel { get; set; } = false;

    /// <summary>
    /// 内嵌视图的 Chromium 远程调试端口（0 = 关闭，默认关闭）。
    /// 打开后内嵌 WebView2 会暴露 CDP，让 dsh-cdp 插件能附加进来，
    /// 供监督 Agent 观察并操作这个视图。
    /// 注意：开启意味着本机任意进程都能脚本化该视图，因此必须是显式选择。
    /// </summary>
    public int CdpDebugPort { get; set; } = 0;

    public Dictionary<string, string> CustomEnv { get; set; } = new();
    public List<ExtensionItem> Extensions { get; set; } = new();

    /// <summary>
    /// 实例编排配置。只编排主工作区（profile=web），其配置与数据保持原样。
    /// 多 agent 等能力由 DSH 本体的插件提供，本软件不额外编排实例。
    /// </summary>
    public List<InstanceConfig> Instances { get; set; } = new();
}

/// <summary>
/// 一个 DSH 实例的隔离描述：独立 profile、独立端口。
/// profile 目录位于 $DSH_HOME/profiles/&lt;Profile&gt;，彼此不共享插件与 node_modules。
/// </summary>
public class InstanceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名，如「主工作区」。</summary>
    public string Name { get; set; } = "实例";

    /// <summary>dsh profile 名（$DSH_HOME/profiles/&lt;Profile&gt;）。</summary>
    public string Profile { get; set; } = "web";

    /// <summary>Web 服务监听端口。</summary>
    public int Port { get; set; } = 3080;

    public bool Enabled { get; set; } = true;

    /// <summary>是否为主工作区（启动后自动载入内嵌视图）。</summary>
    public bool IsPrimary { get; set; } = false;

    /// <summary>追加到命令行末尾的自定义参数（通常留空）。</summary>
    public string ExtraArguments { get; set; } = string.Empty;

    /// <summary>角色简述，仅用于界面展示。</summary>
    public string Role { get; set; } = string.Empty;
}

public class ExtensionItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public bool RunBeforeStart { get; set; } = false;
    public bool RunAfterStop { get; set; } = false;
    public bool IsEnabled { get; set; } = true;
}

public enum ProcessStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}
