using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// 以管理员 / SYSTEM 身份启动 DSH，并把输出接回启动器的日志管线。
///
/// WHY THIS IS A SEPARATE SERVICE
/// ------------------------------
/// 普通模式下子进程的 stdout 是管道，读起来是实时的。但**提权进程没有管道**：
/// Start-Process -Verb RunAs 必须 UseShellExecute=true，而 UseShellExecute=true 就
/// 不能再重定向 stdout/stderr。SYSTEM 计划任务同理。
///
/// 所以提权模式改成「子进程把输出写进文件 → 启动器尾随该文件」。这不是退而求其次
/// 的降级：只有这样才能继续捕获 DSH 打印的 auth token URL —— 那是启动器存在的主要
/// 理由，丢了它这个功能就没有意义。
///
/// 三种权限的实际机制（参照桌面的 SYSTEM权限管理器.ps1 / DSH管理员终端.cmd）：
///   管理员  Start-Process -Verb RunAs（UAC），隐藏窗口时再套一层 wscript
///   SYSTEM  schtasks /create /ru SYSTEM /rl HIGHEST → /run → **立即 /delete**
///           一次性任务：不驻留、不自启、不重启
///
/// 还有一个普通模式没有的问题：SYSTEM 会话里没有 USERPROFILE / DSH_HOME / PATH，
/// 缺了它们 dsh 会去找一个错误的 ~/.dsh。所以启动前必须把真实用户环境注入进去
/// （见 WriteHostScript 里的 Set-Location / env 还原）。
/// </summary>
public sealed class PrivilegeLauncher : IDisposable
{
    public event Action<string, bool>? OnLog;   // 文本, 是否 stderr

    /// <summary>启动器自身是否已经提权。已提权时不再弹 UAC。</summary>
    public static bool LauncherIsElevated { get; } = DetectElevated();

    public string LaunchDir { get; }
    public int? ChildPid { get; private set; }

    private readonly string _logOut;
    private readonly string _logErr;
    private readonly string _pidFile;
    private readonly string _cfgFile;
    private readonly string _hostScript;
    private readonly string _vbsFile;
    private readonly string _launchScript;
    private readonly string _stopScript;

    private readonly List<FileTailer> _tailers = new();

    private static bool DetectElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public PrivilegeLauncher()
    {
        LaunchDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DshLauncher", "elevated");
        _logOut = Path.Combine(LaunchDir, "dsh.log");
        _logErr = Path.Combine(LaunchDir, "dsh.err.log");
        _pidFile = Path.Combine(LaunchDir, "node.pid");
        _cfgFile = Path.Combine(LaunchDir, "launch.json");
        _hostScript = Path.Combine(LaunchDir, "run-dsh.ps1");
        _vbsFile = Path.Combine(LaunchDir, "run-dsh.vbs");
        _launchScript = Path.Combine(LaunchDir, "privileged-launch.ps1");
        _stopScript = Path.Combine(LaunchDir, "stop-dsh.ps1");
    }

    // ─────────────────────────────── start ───────────────────────────────

    public async Task<bool> StartAsync(PrivilegeLevel level, string fileName, string arguments,
                                       string workDir, LauncherConfig config, int port)
    {
        try
        {
            Directory.CreateDirectory(LaunchDir);
            StopTailing();
            ChildPid = null;

            // Fresh run: old output would otherwise be replayed as if it just happened.
            // 但**不能直接删**：上一次如果是崩溃退出，dsh.err.log 里就留着崩溃原因，
            // 删掉等于把现场毁了（2026-11-06 就是这么查不到 node 为什么退出的）。
            // 先归档，再清空。
            ArchivePreviousLogs();
            foreach (string f in new[] { _logOut, _logErr, _pidFile }) TryDelete(f);

            WriteConfig(fileName, arguments, workDir, config, port);
            WriteHostScript();
            WriteVbs();
            WriteLaunchScript();
            WriteStopScript();

            string label = PrivilegeLevelText.Label(level);
            Log($"[提权] 以「{label}」身份启动；提权进程无法用管道读输出，改为尾随日志文件。", false);
            Log($"[提权] 工作目录: {LaunchDir}", false);

            if (!await InvokePowerShellAsync(_launchScript, $"-Mode {(level == PrivilegeLevel.System ? "system" : "admin")} -Dir \"{LaunchDir}\"",
                    needElevation: !LauncherIsElevated, timeoutMs: 60000))
            {
                Log($"[错误] 提权启动脚本未能完成（{label}）。若刚拒绝了 UAC，请重新启动并允许。", true);
                return false;
            }

            StartTailing();

            // Wait for evidence that the child actually came up: the host script writes
            // its PID, and the port starts listening. Neither alone is sufficient --
            // the PID can appear and the process still die on startup.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 30000)
            {
                await Task.Delay(400);
                if (TryReadPid(out int pid)) ChildPid = pid;
                if (IsPortListening(port))
                {
                    Log($"[系统] DSH 已以「{label}」身份启动" + (ChildPid.HasValue ? $" (PID: {ChildPid})" : "") + "。", false);
                    if (level != PrivilegeLevel.Normal && !LauncherIsElevated)
                        Log("[提示] 停止该实例同样需要提权，届时会再弹一次 UAC。", false);
                    return true;
                }
            }

            Log($"[警告] 30 秒内未检测到端口 {port} 监听。上方日志与 {_logErr} 可查看原因。", true);
            // Not fatal: the tailer keeps running so the user can still see why.
            return ChildPid.HasValue;
        }
        catch (Exception ex)
        {
            Log($"[提权异常] {ex.Message}", true);
            return false;
        }
    }

    // ─────────────────────────────── stop ────────────────────────────────

    public async Task<bool> StopAsync()
    {
        // 只要启动器本身没提权、而目标可能是提权实例，就需要 UAC。
        // 旧实现用「是否记录了 ChildPid」来判断，于是兜底场景（ChildPid 为空，
        // 例如状态被误判后再次停止、或换了新的 PrivilegeLauncher 来善后）会以
        // 非提权方式执行停止脚本 —— 而脚本里 $ErrorActionPreference="SilentlyContinue"，
        // 杀不掉 SYSTEM 进程也不报错，表现为「点了停止没反应」。
        bool needUac = !LauncherIsElevated;

        if (File.Exists(_stopScript))
        {
            Log("[提权] 正在通过提权停止脚本结束实例（按端口 + PID）...", false);
            await InvokePowerShellAsync(_stopScript, $"-Dir \"{LaunchDir}\"", needElevation: needUac, timeoutMs: 30000);
        }

        // Give the tailer a moment to flush the dying process's last lines.
        await Task.Delay(600);
        StopTailing();
        ChildPid = null;
        return true;
    }

    // ───────────────────── generated PowerShell scripts ──────────────────
    // All generated files are deliberately pure ASCII: Windows PowerShell 5.1 reads a
    // BOM-less .ps1 as ANSI, so a stray Chinese comment would corrupt the script.

    private void WriteConfig(string fileName, string arguments, string workDir, LauncherConfig config, int port)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config.EnableProxy && !string.IsNullOrWhiteSpace(config.ProxyAddress))
        {
            foreach (string k in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
                env[k] = config.ProxyAddress;
            env["NO_PROXY"] = "localhost,127.0.0.1,api.deepseek.com";
            env["no_proxy"] = "localhost,127.0.0.1,api.deepseek.com";
        }
        if (config.CustomEnv != null)
            foreach (var kv in config.CustomEnv)
                if (!string.IsNullOrWhiteSpace(kv.Key)) env[kv.Key] = kv.Value;

        // Node 堆上限：用户要求「内存无上限」，把约束交给操作系统。
        // 放在 CustomEnv 之后，用户自己设的 NODE_OPTIONS 会被尊重（只追加不覆盖）。
        env.TryGetValue(NodeMemoryOptions.EnvName, out var existingNodeOpts);
        env[NodeMemoryOptions.EnvName] = NodeMemoryOptions.BuildValue(existingNodeOpts);

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dshHome = Environment.GetEnvironmentVariable("DSH_HOME") ?? "";
        if (dshHome.Length == 0) dshHome = Path.Combine(userProfile, ".dsh");

        // Hand-rolled JSON: System.Text.Json escapes non-ASCII as \uXXXX, which
        // ConvertFrom-Json on PowerShell 5.1 handles fine, but this keeps the file
        // readable when someone opens it to debug.
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"fileName\": {J(fileName)},\n");
        sb.Append($"  \"arguments\": {J(arguments)},\n");
        sb.Append($"  \"workDir\": {J(workDir)},\n");
        sb.Append($"  \"userProfile\": {J(userProfile)},\n");
        sb.Append($"  \"dshHome\": {J(dshHome)},\n");
        sb.Append($"  \"path\": {J(Environment.GetEnvironmentVariable("PATH") ?? "")},\n");
        sb.Append($"  \"port\": {port},\n");
        sb.Append($"  \"hideConsole\": {(config.HideConsole ? "true" : "false")},\n");
        sb.Append($"  \"logOut\": {J(_logOut)},\n");
        sb.Append($"  \"logErr\": {J(_logErr)},\n");
        sb.Append($"  \"pidFile\": {J(_pidFile)},\n");
        sb.Append("  \"env\": {");
        bool first = true;
        foreach (var kv in env)
        {
            if (!first) sb.Append(',');
            sb.Append($"\n    {J(kv.Key)}: {J(kv.Value)}");
            first = false;
        }
        sb.Append(first ? "}\n" : "\n  }\n");
        sb.Append("}\n");

        File.WriteAllText(_cfgFile, sb.ToString(), new UTF8Encoding(false));
    }

    private static string J(string? s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private void WriteHostScript()
    {
        const string ps = """
$ErrorActionPreference = "SilentlyContinue"
$dir = Split-Path $MyInvocation.MyCommand.Path -Parent
$cfgPath = Join-Path $dir "launch.json"
$c = $null
if (Test-Path $cfgPath) { try { $c = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch {} }
if (-not $c) { exit 1 }

# Restore the real user environment. A SYSTEM session has none of these, and without
# them dsh resolves the wrong ~/.dsh and node is not on PATH at all.
if ($c.userProfile -ne "") { $env:USERPROFILE = [string]$c.userProfile; $env:USERNAME = Split-Path ([string]$c.userProfile) -Leaf }
if ($c.dshHome -ne "") { $env:DSH_HOME = [string]$c.dshHome }
if ($c.path -ne "") { $env:PATH = [string]$c.path + ";" + $env:PATH }
if ($c.env) { foreach ($p in $c.env.PSObject.Properties) { Set-Item -Path ("env:" + $p.Name) -Value ([string]$p.Value) } }

$port = 3085
try { $port = [int]$c.port } catch {}

# Release the port if a previous run is still holding it, otherwise node dies with
# EADDRINUSE before it can print anything useful.
Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 700

Remove-Item ([string]$c.logOut), ([string]$c.logErr) -Force -ErrorAction SilentlyContinue

$exe = [string]$c.fileName
$argList = [string]$c.arguments
$origExe = $exe
$wd = [string]$c.workDir
if ($wd -eq "" -or -not (Test-Path $wd)) { $wd = [string]$c.userProfile }
$psExe = Join-Path $env:windir "System32\WindowsPowerShell\v1.0\powershell.exe"

# A .cmd/.bat shim (dsh.cmd) cannot be started with redirects, so route it via cmd.exe.
if ($exe -match '\.(cmd|bat)$') { $argList = '/c "' + $exe + '" ' + $argList; $exe = "$env:windir\System32\cmd.exe" }

try { Set-Location $wd } catch {}

# Hidden: redirect straight into the log files.
# Visible: Tee into the log file AND the console. A redirected stream is gone from the
# console, so "watch it live" and "capture it for the launcher" cannot both hold without
# an intermediary -- without Tee the visible window would just sit there empty.
if ([bool]$c.hideConsole) {
    $p = Start-Process -FilePath $exe -ArgumentList $argList -WorkingDirectory $wd -WindowStyle Hidden `
         -RedirectStandardOutput ([string]$c.logOut) -RedirectStandardError ([string]$c.logErr) -PassThru
} else {
    $ps1 = Join-Path $dir "run-visible.ps1"
    $inner = "& '" + $origExe + "' " + $c.arguments + " 2>&1 | Tee-Object -FilePath '" + [string]$c.logOut + "'"
    [System.IO.File]::WriteAllText($ps1, $inner, (New-Object System.Text.UTF8Encoding($false)))
    $p = Start-Process -FilePath $psExe -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$ps1`"") `
         -WorkingDirectory $wd -PassThru
}

# Record the PID so Stop can kill exactly this process tree even if the port check fails.
if ($p) { try { [System.IO.File]::WriteAllText([string]$c.pidFile, [string]$p.Id) } catch {} }
""";
        File.WriteAllText(_hostScript, ps, new UTF8Encoding(false));
    }

    private void WriteVbs()
    {
        // WScript.Shell.Run with 0 = hidden window. This is what keeps an elevated
        // launch from flashing a console the user cannot close without killing DSH.
        string body = "CreateObject(\"WScript.Shell\").Run \"powershell.exe -NoProfile -WindowStyle Hidden " +
                      "-ExecutionPolicy Bypass -File \"\"" + _hostScript + "\"\"\", 0, False";
        File.WriteAllText(_vbsFile, body, Encoding.ASCII);
    }

    private void WriteLaunchScript()
    {
        const string ps = """
param([string]$Mode = "admin", [string]$Dir = "")
$ErrorActionPreference = "Stop"
if ($Dir -eq "") { $Dir = Split-Path $MyInvocation.MyCommand.Path -Parent }
$c = Get-Content (Join-Path $Dir "launch.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$hostScript = Join-Path $Dir "run-dsh.ps1"
$vbs = Join-Path $Dir "run-dsh.vbs"
$psExe = Join-Path $env:windir "System32\WindowsPowerShell\v1.0\powershell.exe"
$wscript = Join-Path $env:windir "System32\wscript.exe"

if ($Mode -eq "system") {
    # One-shot scheduled task: create -> run -> delete immediately. Nothing is left
    # behind to auto-start on boot, and the task never restarts the process.
    $taskName = "DshLauncher-" + [string]$c.port + "-" + ([guid]::NewGuid().ToString("N").Substring(0, 8))
    if ([bool]$c.hideConsole) {
        $tr = '"' + $wscript + '" "' + $vbs + '"'
    } else {
        $tr = '"' + $psExe + '" -NoProfile -ExecutionPolicy Bypass -File "' + $hostScript + '"'
    }
    $st = (Get-Date).AddMinutes(2).ToString("HH:mm")
    $null = schtasks /create /tn $taskName /tr $tr /sc once /st $st /ru SYSTEM /rl HIGHEST /f 2>&1
    if ($LASTEXITCODE -ne 0) { Write-Error "schtasks /create failed: administrator rights are required"; exit 2 }
    $null = schtasks /run /tn $taskName 2>&1
    Start-Sleep -Seconds 3
    $null = schtasks /delete /tn $taskName /f 2>&1
    exit 0
}

# Administrator mode. We are already elevated here (the launcher either started us
# with -Verb RunAs, or was itself elevated), so no further UAC is involved.
if ([bool]$c.hideConsole) {
    (New-Object -ComObject WScript.Shell).Run("wscript.exe `"$vbs`"", 0, $false)
} else {
    Start-Process -FilePath $psExe -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$hostScript`"")
}
exit 0
""";
        File.WriteAllText(_launchScript, ps, new UTF8Encoding(false));
    }

    private void WriteStopScript()
    {
        const string ps = """
param([string]$Dir = "")
$ErrorActionPreference = "SilentlyContinue"
if ($Dir -eq "") { $Dir = Split-Path $MyInvocation.MyCommand.Path -Parent }
$c = Get-Content (Join-Path $Dir "launch.json") -Raw -Encoding UTF8 | ConvertFrom-Json

# Kill by port first: it is the only signal that is correct even if the PID file was
# never written (e.g. the host script died mid-way).
# NOTE: Get-NetTCPConnection 在提权/SYSTEM 上下文里会漏报（实测端口明明在监听却返回空），
# 改用 netstat -ano 解析，命中率最高。
$port = 3085
try { $port = [int]$c.port } catch {}
$suffix = ":$port"
netstat -ano | ForEach-Object {
    $t = $_.Trim()
    if ($t -notmatch '^TCP') { return }
    $cols = $t -split '\s+'
    if ($cols.Count -lt 5) { return }
    if (-not $cols[1].EndsWith($suffix)) { return }
    if ($cols[3] -ne 'LISTENING') { return }
    $procId = 0
    if ([int]::TryParse($cols[4], [ref]$procId) -and $procId -gt 0) {
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
    }
}

# Then by recorded PID, with /T so node's own children go too.
$pidFile = Join-Path $Dir "node.pid"
if (Test-Path $pidFile) {
    $m = [regex]::Match((Get-Content $pidFile -Raw -ErrorAction SilentlyContinue), "(\d+)")
    if ($m.Success) { taskkill /T /F /PID $m.Groups[1].Value 2>&1 | Out-Null }
}
Start-Sleep -Milliseconds 500
exit 0
""";
        File.WriteAllText(_stopScript, ps, new UTF8Encoding(false));
    }

    // ─────────────────────── invocation helpers ──────────────────────────

    private static async Task<bool> InvokePowerShellAsync(string scriptPath, string arguments,
                                                          bool needElevation, int timeoutMs)
    {
        string psExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                    @"WindowsPowerShell\v1.0\powershell.exe");

        ProcessStartInfo psi;
        if (needElevation)
        {
            // UseShellExecute + RunAs is the only way to raise the UAC prompt.
            // -WindowStyle Hidden keeps the elevated helper itself invisible; the
            // actual DSH window is decided by hideConsole inside the script.
            psi = new ProcessStartInfo
            {
                FileName = psExe,
                Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\" {arguments}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = psExe,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {arguments}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!needElevation)
            {
                string err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                    throw new InvalidOperationException(err.Trim());
            }
            else
            {
                await p.WaitForExitAsync();
            }
            return needElevation || p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            // 1223 = ERROR_CANCELLED: the user clicked No on the UAC prompt.
            bool cancelled = ex.Message.Contains("1223") || ex.Message.Contains("cancell", StringComparison.OrdinalIgnoreCase);
            throw new InvalidOperationException(cancelled
                ? "UAC 提权被拒绝（用户取消）。"
                : ex.Message, ex);
        }
    }

    // ─────────────────────────── log tailing ─────────────────────────────

    private void StartTailing()
    {
        StopTailing();
        _tailers.Add(new FileTailer(_logOut, false, (line, isErr) => OnLog?.Invoke(line, isErr)));
        _tailers.Add(new FileTailer(_logErr, true, (line, isErr) => OnLog?.Invoke(line, isErr)));
        foreach (var t in _tailers) t.Start();
    }

    private void StopTailing()
    {
        foreach (var t in _tailers) t.Dispose();
        _tailers.Clear();
    }

    // ───────────────────────────── utilities ─────────────────────────────

    private bool TryReadPid(out int pid)
    {
        pid = 0;
        try
        {
            if (!File.Exists(_pidFile)) return false;
            string text = File.ReadAllText(_pidFile).Trim();
            return int.TryParse(text, out pid) && pid > 0;
        }
        catch { return false; }
    }

    /// <summary>公开的 PID 读取（供外部对比「端口监听者是不是我自己」）。</summary>
    public bool TryGetPid(out int pid) => TryReadPid(out pid);

    /// <summary>
    /// 查出正在 LISTEN 指定端口的进程 PID；查不到返回 null。
    ///
    /// 为什么不用 IPGlobalProperties / Get-NetTCPConnection：
    ///   · GetActiveTcpListeners() 只给 endpoint，不带 PID；
    ///   · Get-NetTCPConnection 在提权/SYSTEM 上下文里会漏报（实测 19:44 该端口
    ///     明明在监听，它却返回空）。
    /// netstat -ano 是命中率最高的一个。
    /// </summary>
    public static int? FindPortOwnerPid(int port)
    {
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
            if (p == null) return null;

            string output = p.StandardOutput.ReadToEnd();
            try { p.WaitForExit(4000); } catch { }

            string suffix = ":" + port;
            int? establishedPid = null;

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;

                string[] cols = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 5) continue;
                if (!cols[1].EndsWith(suffix, StringComparison.Ordinal)) continue;

                // 本环境下 node 的监听可能只出现在 ESTABLISHED 行、没有 LISTENING 行，
                // 所以两种状态都收；LISTENING 优先，ESTABLISHED 作为回退。
                bool isListening = cols[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase);
                bool isEstablished = cols[3].Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase);
                if (!isListening && !isEstablished) continue;

                if (int.TryParse(cols[4], out int pid) && pid > 0)
                {
                    if (isListening) return pid;
                    establishedPid ??= pid;
                }
            }

            return establishedPid;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 提权实例是否仍然活着：读 node.pid 并确认该 PID 对应进程还在。
    ///
    /// 这是「端口监听」之外的第二存活信号。个别环境下
    /// IPGlobalProperties.GetActiveTcpListeners() 会漏掉目标端口，
    /// 若只凭端口判死，活着的实例会被误标成「已停止」且无法再停止
    /// （Stopped 状态会把停止按钮禁用）。
    /// </summary>
    public bool IsProcessAlive()
    {
        if (!TryReadPid(out int pid)) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    /// <summary>
    /// 探测端口是否真的在服务。
    ///
    /// **不能只用 GetActiveTcpListeners()**：2026-11-06 实测，在当前环境下 node 的
    /// 监听对它返回空，`netstat -ano` 也只有 ESTABLISHED、**没有 LISTENING 行**，
    /// 但端口完全可用（HTTP 请求能拿到 401 鉴权响应）。据此判活会导致
    /// 「明明在跑却判死 → 反复重启」的死循环。
    ///
    /// **也不能用 BeginConnect + 超时后 Dispose**：超时那一刻 socket 被关，
    /// 未完成的异步连接抛 SocketException(995 WSA_OPERATION_ABORTED)，
    /// 落到线程池成为未观察异常（实测 crash 日志里刷了一堆）。
    ///
    /// 这里用「非阻塞 connect + Poll 等待可写」：全程同步，不产生挂起的异步操作。
    /// </summary>
    public static bool IsPortListening(int port)
    {
        // ① 主动连接探测（最可靠，不受 netstat 显示问题影响）
        try
        {
            using var sock = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);

            sock.Blocking = false;
            try
            {
                sock.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
            }
            catch (System.Net.Sockets.SocketException se)
            {
                // 非阻塞 connect 的"进行中"不是错误
                if (se.SocketErrorCode != System.Net.Sockets.SocketError.TimedOut &&
                    se.SocketErrorCode != System.Net.Sockets.SocketError.InProgress &&
                    se.SocketErrorCode != System.Net.Sockets.SocketError.WouldBlock &&
                    se.SocketErrorCode != System.Net.Sockets.SocketError.AlreadyInProgress)
                {
                    // 连接被拒绝等：端口确实没在服务
                    return false;
                }
            }

            // 可写 = 三次握手完成
            if (sock.Poll(400_000, System.Net.Sockets.SelectMode.SelectWrite)) return true;
        }
        catch { }

        // ② 回退：监听表里有该端口，或存在**已建立**连接（才算真的有实例在服务）
        try
        {
            var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();

            foreach (var ep in props.GetActiveTcpListeners())
                if (ep.Port == port) return true;

            // ⚠ 必须限定 Established：GetActiveTcpConnections 同时包含 TIME_WAIT / Closed，
            // 而进程崩溃后留下的 TIME_WAIT 是内核持有、没有进程在服务的。
            // 之前不区分状态，导致「崩溃残留 TIME_WAIT」被误判成「端口仍被占用」，
            // 于是明明能 bind 却被拒绝启动（2026-11-06 22:16 实测日志）。
            foreach (var c in props.GetActiveTcpConnections())
                if (c.LocalEndPoint.Port == port
                    && c.State == System.Net.NetworkInformation.TcpState.Established)
                    return true;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 归档上一次运行的输出日志。
    ///
    /// 在此之前每次启动都会直接删除 dsh.log / dsh.err.log —— 如果上一次是**崩溃**退出，
    /// 崩溃原因就随文件一起没了，事后完全无法定位（2026-11-06 实测：查不到 node 退出原因）。
    /// 归档到 logs\sessions\ 并只保留最近若干份，既能留下现场又不会无限增长。
    /// </summary>
    private void ArchivePreviousLogs()
    {
        try
        {
            string archiveDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshLauncher", "logs", "sessions");
            Directory.CreateDirectory(archiveDir);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            foreach (var item in new[] { (_logOut, "dsh.log"), (_logErr, "dsh.err.log") })
            {
                try
                {
                    string path = item.Item1;
                    if (!File.Exists(path)) continue;
                    if (new FileInfo(path).Length == 0) continue;   // 空文件没有归档价值

                    File.Copy(path, Path.Combine(archiveDir, $"{stamp}-{item.Item2}"), overwrite: true);
                }
                catch { }
            }

            // 只保留最近 40 个文件（约 20 次启动）
            try
            {
                var stale = Directory.GetFiles(archiveDir)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(40)
                    .ToList();

                foreach (var f in stale)
                {
                    try { f.Delete(); } catch { }
                }
            }
            catch { }
        }
        catch
        {
            // 归档失败绝不能影响启动
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void Log(string message, bool isError) => OnLog?.Invoke(message, isError);

    public void Dispose() => StopTailing();
}
