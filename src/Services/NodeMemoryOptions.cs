namespace DshLauncher.Services;

/// <summary>
/// Node / V8 堆上限。
///
/// 用户要求「内存无上限」。V8 的 max-old-space-size 在 64 位系统上默认只有
/// 约 2–4 GB（按物理内存自适应，上限远低于本机 31.5 GB）。大会话 + 多开会话时
/// 会先撞上这个**隐藏上限**，抛出 "JavaScript heap out of memory" 直接退出——
/// 表现就是「一多开会话就崩」，而且 dsh.err.log 里未必留下我们能看懂的原因。
///
/// 真正意义的「无上限」不存在（V8 必须拿到一个数值），但设成物理内存的 ~90%
/// 等价于把约束交给操作系统：物理内存够就不会 OOM，不够时由 Windows 统一调度，
/// 而不是 V8 自己提前终止进程。
///
/// 用户若在 CustomEnv 里自己设了 NODE_OPTIONS，则原样尊重，不覆盖。
/// </summary>
public static class NodeMemoryOptions
{
    public const string EnvName = "NODE_OPTIONS";
    private const string FlagPrefix = "--max-old-space-size=";

    /// <summary>按物理内存算出建议的堆上限（MB）。</summary>
    public static int ResolveHeapMb()
    {
        try
        {
            // .NET Core 提供的 GC 视角总可用内存，无需额外依赖
            long totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (totalBytes <= 0) return 16384;

            long totalMb = totalBytes / (1024L * 1024L);
            long heap = (long)(totalMb * 0.9);

            if (heap < 4096) heap = 4096;      // 再小就没有意义
            if (heap > 65536) heap = 65536;    // 防止传超大值让 V8 初始化失败
            return (int)heap;
        }
        catch
        {
            return 16384;
        }
    }

    /// <summary>
    /// 生成要注入的 NODE_OPTIONS 值。
    /// 已有值且用户自己指定过 --max-old-space-size 时保持原样。
    /// </summary>
    public static string BuildValue(string? existing)
    {
        string flag = FlagPrefix + ResolveHeapMb().ToString();

        if (string.IsNullOrWhiteSpace(existing)) return flag;
        if (existing.Contains("--max-old-space-size", StringComparison.OrdinalIgnoreCase))
            return existing;   // 用户显式设置过，尊重

        return existing.TrimEnd() + " " + flag;
    }
}
