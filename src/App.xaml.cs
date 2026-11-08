using System.IO;
using System.Windows;

namespace DshLauncher;

/// <summary>
/// 应用入口。
///
/// 这里必须存在全局异常兜底，否则一个 UI 线程异常就会把整个进程打死：
/// 2026-11-06 实测两次 —— 打开大会话（前端 renderer 内存涨到 815MB）后
/// WebView2 渲染进程崩溃，控件抛出的未处理异常沿 UI 线程冒泡，
/// Windows 事件日志留下 "DshLauncher.exe / 异常代码 0xe0434352 /
/// The process was terminated due to an unhandled exception"（12:30:02、13:10:15）。
///
/// 启动器一崩，它拉起的 DSH 也会被 Job Object 连带杀死（或变成孤儿占住端口，
/// 导致下次启动 EADDRINUSE）。所以兜底不是为了"好看"，是为了别把 DSH 一起带走。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // ① UI 线程异常：WebView2 的 COM/渲染进程异常都从这条路上来。
        //    标记 Handled 让进程活下去；写到 crash 日志便于事后定位。
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("UI 线程未处理异常", args.Exception);
            // WebView2 的 RPC_E_DISCONNECTED 等属于可恢复异常，继续运行；
            // 若是致命状态，用户至少还能看到界面并手动停止实例。
            args.Handled = true;
        };

        // ② 非 UI 线程 / 终结器线程异常：兜底记录，进程随后仍会终止，
        //    但有日志可查（总比无声消失好）。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog("非 UI 线程未处理异常", args.ExceptionObject as Exception);
        };

        // ③ 后台任务里未观察到的异常，不要升级成进程崩溃。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("未观察的 Task 异常", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private static void WriteCrashLog(string kind, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshLauncher", "logs");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd}.log");
            string text = $"[{DateTime.Now:HH:mm:ss}] {kind}\r\n{ex}\r\n\r\n";
            File.AppendAllText(path, text);
        }
        catch
        {
            // 记日志本身失败时什么都不要做 —— 绝不能在异常处理里再抛
        }
    }
}
