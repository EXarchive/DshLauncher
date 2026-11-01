namespace DshLauncher.Models;

/// <summary>
/// 启动 DSH 时使用的权限级别。
///
/// 普通：与启动器同权限，直接用管道读取子进程 stdout —— 控制台输出与 auth token
///       捕获都是实时的，且能纳入 Job Object（启动器被强杀时子进程一起走）。
/// 管理员：通过 UAC 提权（Start-Process -Verb RunAs）。提权进程无法用管道重定向，
///       所以输出改走日志文件 + 尾随读取；同时无法纳入 Job Object。
/// SYSTEM：建一个「一次性」计划任务以 SYSTEM 身份启动，启动后立即删除该任务，
///       不驻留、不自启、不重启。同样走日志文件。
///
/// 后两者都需要在启动前把真实用户环境注入进去：SYSTEM 会话里没有 USERPROFILE /
/// DSH_HOME / PATH，缺了它们 dsh 会去找错误的 ~/.dsh。
/// </summary>
public enum PrivilegeLevel
{
    Normal = 0,
    Administrator = 1,
    System = 2,
}

public static class PrivilegeLevelText
{
    public static string Label(PrivilegeLevel level) => level switch
    {
        PrivilegeLevel.Administrator => "管理员",
        PrivilegeLevel.System => "SYSTEM",
        _ => "普通",
    };

    /// <summary>One line explaining what the level actually costs the user.</summary>
    public static string Hint(PrivilegeLevel level, bool launcherElevated) => level switch
    {
        PrivilegeLevel.Administrator => launcherElevated
            ? "启动器已是管理员：直接继承权限，不会再弹 UAC。"
            : "每次启动会弹一次 UAC。管理员进程无法用管道读输出，改为日志文件尾随。",
        PrivilegeLevel.System => launcherElevated
            ? "启动器已是管理员：直接建一次性 SYSTEM 计划任务（启动后立即删除）。"
            : "会弹一次 UAC 用于创建 SYSTEM 计划任务；SYSTEM 的窗口在会话 0，本来就看不见。",
        _ => "与启动器同权限，输出实时走管道，子进程纳入生命周期 Job。",
    };
}
