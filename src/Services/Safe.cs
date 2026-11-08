using System.IO;
using System.Text;

namespace DshLauncher.Services;

/// <summary>
/// 统一的异常安全包装。
///
/// 为什么需要：2026-11-06 实测 —— 打开大会话（17.68MB）后 WebView2 渲染进程
/// 内存涨到 815MB 并崩溃，控件抛出的异常沿 UI 线程冒泡，DshLauncher.exe 以
/// 0xe0434352（CLR 未处理异常）整体崩溃，连带的 DSH 也被 Job Object 杀死
/// 或变成孤儿占住端口。一个事件处理器里的异常就足以毁掉整个会话。
///
/// 规矩：**所有事件处理器、fire-and-forget 任务、后台线程**都必须经过这里。
/// 异常一律记录到 %LOCALAPPDATA%\DshLauncher\logs\safe-YYYYMMDD.log，绝不外抛。
/// </summary>
public static class Safe
{
    private static readonly object _fileLock = new();

    /// <summary>异常发生时通知 UI（可为空）。参数：上下文名、异常。</summary>
    public static event Action<string, Exception>? OnError;

    /// <summary>同步动作：异常只记录，不抛。</summary>
    public static void Run(string context, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Report(context, ex);
        }
    }

    /// <summary>异步动作：异常只记录，不抛。</summary>
    public static async Task RunAsync(string context, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Report(context, ex);
        }
    }

    /// <summary>
    /// 供 async void 事件处理器使用的入口。
    /// async void 的异常无法被调用方 catch，不在这里吞掉就会直接终结进程。
    /// </summary>
    public static async void FireAndForget(string context, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Report(context, ex);
        }
    }

    /// <summary>带返回值的异步动作：失败返回 default，并记录。</summary>
    public static async Task<T?> TryAsync<T>(string context, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Report(context, ex);
            return default;
        }
    }

    /// <summary>后台异步任务：异常被观察并记录，不会变成 UnobservedTaskException。</summary>
    public static Task RunBackground(string context, Func<Task> action)
        => Task.Run(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report(context, ex);
            }
        });

    /// <summary>后台同步任务：异常被观察并记录。</summary>
    public static Task RunBackground(string context, Action action)
        => Task.Run(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Report(context, ex);
            }
        });

    /// <summary>记录一次异常。绝不自己抛。</summary>
    public static void Report(string context, Exception? ex)
    {
        if (ex == null) return;

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshLauncher", "logs");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, $"safe-{DateTime.Now:yyyyMMdd}.log");
            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ")
              .Append(context).AppendLine();
            sb.AppendLine(ex.ToString());
            sb.AppendLine();

            lock (_fileLock)
            {
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 记录失败就放弃 —— 绝不能在异常处理路径里再抛
        }

        try { OnError?.Invoke(context, ex); } catch { }
    }
}
