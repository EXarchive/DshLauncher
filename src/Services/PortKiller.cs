using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DshLauncher.Services;

/// <summary>端口诊断结果。</summary>
public sealed record PortDiagnosis(
    int Port,
    bool Bindable,          // 现在能不能真的 bind（最终判据）
    bool TimeWaitOnly,      // 只剩 TIME_WAIT 残留、没有进程占用
    List<int> OwnerPids,    // 占用进程（LISTENING / ESTABLISHED）
    int TimeWaitCount,      // TIME_WAIT 条目数（内核持有，PID 恒为 0，杀不掉）
    string Detail);

/// <summary>
/// 端口占用分析与强制释放。
///
/// 关键事实（2026-11-06 实测）：
///   1) `netstat -ano` 对 SYSTEM 监听会把 owner PID 报成 **0**；
///   2) 进程崩溃后端口上会留下一堆 **TIME_WAIT (PID 0)** —— 由内核持有，
///      **没有任何进程可杀**，只看 PID 会得出「端口空闲」的错误结论；
///   3) 是否真的能用，唯一可靠的判据是**试着 bind 一次**。
///
/// 所以本类的流程是：先 netstat 分类（进程 / TIME_WAIT），再实测 bind，
/// 最后才决定"杀进程"还是"告诉用户只能等或换端口"。
/// </summary>
public static class PortKiller
{
    /// <summary>分析端口当前状态：谁能用、谁在占、有多少 TIME_WAIT。</summary>
    public static PortDiagnosis Diagnose(int port)
    {
        var pids = new List<int>();
        int timeWait = 0;

        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string output = p.StandardOutput.ReadToEnd();
                try { p.WaitForExit(4000); } catch { }

                string suffix = ":" + port;
                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;

                    string[] cols = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (cols.Length < 5) continue;
                    if (!cols[1].EndsWith(suffix, StringComparison.Ordinal)) continue;

                    string state = cols[3].ToUpperInvariant();
                    if (state == "TIME_WAIT") { timeWait++; continue; }

                    bool isListening = state == "LISTENING";
                    bool isEstablished = state == "ESTABLISHED";
                    if (!isListening && !isEstablished) continue;

                    if (int.TryParse(cols[4], out int pid) && pid > 0 && !pids.Contains(pid))
                        pids.Add(pid);
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Report("PortKiller.Diagnose.netstat", ex);
        }

        bool bindable = CanBind(port);
        bool timeWaitOnly = !bindable && pids.Count == 0 && timeWait > 0;

        string detail;
        if (bindable)
            detail = $"端口 {port} 可用";
        else if (pids.Count > 0)
            detail = $"端口 {port} 被占用：" + DescribeOwners(pids)
                     + (timeWait > 0 ? $"；另有 {timeWait} 条 TIME_WAIT" : "");
        else if (timeWaitOnly)
            detail = $"端口 {port} 暂时不能 bind：没有进程占用，只有 {timeWait} 条 TIME_WAIT 残留"
                     + "（内核持有，无法杀进程释放，只能等它自然回收或换端口）";
        else
            detail = $"端口 {port} 不能 bind，但 netstat 未报告占用者（可能被提权进程独占）";

        return new PortDiagnosis(port, bindable, timeWaitOnly, pids, timeWait, detail);
    }

    /// <summary>实测能否 bind（最终判据，比任何 netstat 解析都可靠）。</summary>
    public static bool CanBind(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            // Windows 上默认 ExclusiveAddressUse=true，最接近"DSH 能否 bind"的真实情况
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从 startPort 开始找一个能 bind 的端口（最多找 span 个）。</summary>
    public static int FindFreePort(int startPort, int span = 50)
    {
        for (int i = 0; i < span; i++)
        {
            int candidate = startPort + i;
            if (candidate > 65535) break;
            if (CanBind(candidate)) return candidate;
        }
        return startPort;   // 一个都找不到就原样返回，让上层报错
    }

    public static string DescribeOwners(IEnumerable<int> pids)
    {
        var parts = new List<string>();
        foreach (int pid in pids)
        {
            string name = "?";
            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
            }
            catch { }
            parts.Add($"{name}({pid})");
        }
        return parts.Count == 0 ? "无" : string.Join(", ", parts);
    }

    /// <summary>
    /// 强制结束占用该端口的**进程**。
    /// 注意：TIME_WAIT 属于内核、没有进程，本方法对它无效 —— 那种情况会如实报告。
    /// </summary>
    public static async Task<PortDiagnosis> KillAsync(int port)
    {
        var before = Diagnose(port);

        // 没有任何进程可杀：直接返回诊断（含 TIME_WAIT 的说明）
        if (before.OwnerPids.Count == 0)
            return before;

        string scriptPath = Path.Combine(
            Path.GetTempPath(), $"dsh-kill-port-{port}-{Environment.ProcessId}.ps1");

        try
        {
            File.WriteAllText(scriptPath, BuildKillScript(before.OwnerPids), new UTF8Encoding(false));

            bool needElevation = !IsElevated();
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = needElevation,
                CreateNoWindow = !needElevation,
                RedirectStandardOutput = !needElevation,
                RedirectStandardError = !needElevation
            };

            if (needElevation)
            {
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
            }

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                if (!needElevation)
                {
                    try { await proc.StandardOutput.ReadToEndAsync(); } catch { }
                    try { await proc.StandardError.ReadToEndAsync(); } catch { }
                }
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Safe.Report("PortKiller.KillAsync", ex);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }

        // 给 Windows 一点时间回收 socket
        await Task.Delay(800);

        return Diagnose(port);
    }

    private static string BuildKillScript(IReadOnlyList<int> pids)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = \"SilentlyContinue\"");
        sb.Append("$targets = @(");
        sb.Append(string.Join(",", pids));
        sb.AppendLine(")");
        sb.AppendLine("foreach ($procId in $targets) {");
        sb.AppendLine("    if ($procId -le 0) { continue }");
        sb.AppendLine("    taskkill /T /F /PID $procId 2>&1 | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Milliseconds 500");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
