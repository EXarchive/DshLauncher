using System.Diagnostics;
using System.IO;
using System.Text;
using DshLauncher.Models;

namespace DshLauncher.Services;

public class ProcessRunnerService
{
    private Process? _currentProcess;
    private readonly object _lock = new();

    /// <summary>提权 / SYSTEM 模式的启动器；普通模式为 null。两者互斥。</summary>
    private PrivilegeLauncher? _privilege;
    private PrivilegeLevel _activePrivilege = PrivilegeLevel.Normal;

    public event Action<string, bool>? OnLogReceived; // message, isError
    public event Action<ProcessStatus>? OnStatusChanged;
    public event Action<string>? OnAuthenticatedUrlDetected; // detected authenticated url

    public ProcessStatus Status { get; private set; } = ProcessStatus.Stopped;
    public int? CurrentProcessId => _currentProcess?.Id;
    public string? LastAuthenticatedUrl { get; private set; }

    public Task<bool> StartAsync(LauncherConfig config) => StartAsync(config, null);

    /// <summary>
    /// 按指定实例（profile + 端口）启动 DSH。
    /// instance 为 null 时走单实例旧路径（使用 config.CustomArguments 与 config.WebPort）。
    /// </summary>
    public async Task<bool> StartAsync(LauncherConfig config, InstanceConfig? instance)
    {
        lock (_lock)
        {
            if (Status == ProcessStatus.Running || Status == ProcessStatus.Starting)
            {
                // 这是保护性提示（防止重复启动），不是错误 —— 标成 [错误] 会误导
                Log("服务已在运行或正在启动中，已忽略本次启动请求。", false);
                return false;
            }
            SetStatus(ProcessStatus.Starting);
        }

        try
        {
            // 0. 端口占用检查 ────────────────────────────────────────────────
            // 端口被占有两种情况，必须区别对待：
            //   ① 本启动器上次残留的实例（node.pid 里的 PID 正是监听者）→ 可以清理
            //   ② 另一个实例正在正常使用（例如用户已有一个 DSH 在跑）→ 绝不能杀
            // 之前只要端口被占就按端口执行停止脚本，会把正在用的实例一起干掉，
            // 还会和「新实例 bind 失败」串成死循环。
            int portToCheck = instance?.Port ?? (config.WebPort > 0 ? config.WebPort : 3080);

            // 用「能不能真的 bind」判断，而不是「有没有连接」。
            // 崩溃后留下的 TIME_WAIT 是内核持有、没有进程在服务的，
            // 新实例可以直接复用该端口（已实测验证）——把它当成"被占用"会拒绝掉
            // 本来完全能成功的启动（2026-11-06 22:16 日志：明明能起却提示端口被占用）。
            if (!PortKiller.CanBind(portToCheck))
            {
                int? ownerPid = PrivilegeLauncher.FindPortOwnerPid(portToCheck);

                bool ownStale;
                var probe = new PrivilegeLauncher();
                try
                {
                    if (ownerPid.HasValue && ownerPid.Value > 0)
                    {
                        // 能查到归属：必须与 node.pid 记录一致，才认定是自己的残留
                        ownStale = probe.TryGetPid(out int recordedPid) && recordedPid == ownerPid.Value;
                    }
                    else
                    {
                        // 归属未知（SYSTEM 进程监听端口时 netstat 常报 PID 0）：
                        // 用「node.pid 记录的进程还活着」来判定是不是自己的残留。
                        ownStale = probe.IsProcessAlive();
                    }
                }
                catch { ownStale = false; }
                finally { probe.Dispose(); }

                if (ownStale)
                {
                    Log($"[提示] 端口 {portToCheck} 被上次残留的实例占用（PID {ownerPid}），先结束它再启动…", false);
                    var stale = new PrivilegeLauncher();
                    try { await stale.StopAsync(); }
                    catch (Exception ex) { Log($"[提示] 清理残留实例失败：{ex.Message}", true); }
                    finally { stale.Dispose(); }
                    await Task.Delay(800);
                }
                else
                {
                    Log($"[提示] 端口 {portToCheck} 已被占用（PID {ownerPid?.ToString() ?? "未知"}），"
                        + "它不是本启动器的残留实例，不会强行结束。", true);
                    Log("[提示] 请先停止该实例，或把端口改成别的值后再启动。", false);
                    SetStatus(ProcessStatus.Stopped);
                    return false;
                }
            }

            // 1. Run Pre-start Extensions
            // 实例模式下刻意跳过：这些扩展来自启动器的全局配置，若为某个非主实例执行，
            // 就可能运行到面向主工作区（:3080）的命令——实例隔离里唯一可能越过边界的地方。
            if (instance == null)
            {
                await RunExtensionsAsync(config.Extensions.Where(e => e.IsEnabled && e.RunBeforeStart));
            }
            else if (config.Extensions.Any(e => e.IsEnabled && e.RunBeforeStart))
            {
                Log("[系统] 实例模式：已跳过全局启动扩展（可能影响主工作区）。", false);
            }

            // 2. Prepare executable and arguments
            var (fileName, arguments, workDir) = ResolveExecutionTarget(config, instance);

            Log($"[系统] 正在启动 DSH 服务...", false);
            Log($"[系统] 工作目录: {workDir}", false);
            Log($"[系统] 执行程序: {fileName}", false);
            Log($"[系统] 启动参数: {arguments}", false);

            // 提权 / SYSTEM 模式没有管道可用（UseShellExecute=true 就不能重定向），
            // 交给 PrivilegeLauncher：输出落文件 + 尾随，auth token 捕获照旧。
            if (config.Privilege != PrivilegeLevel.Normal)
            {
                return await StartPrivilegedAsync(config, instance, fileName, arguments, workDir);
            }

            // 普通模式 + 不隐藏控制台：既要「窗口里看得见」又要「启动器也捕获」，
            // 只能 Tee 到文件再尾随 —— 直接重定向会把输出从控制台拿走，窗口只剩一片空白。
            if (!config.HideConsole)
            {
                return await StartVisibleConsoleAsync(config, instance, fileName, arguments, workDir);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Setup Environment Variables
            SetupEnvironmentVariables(startInfo, config);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Log(args.Data, false);
                    CheckForAuthenticatedUrl(args.Data);
                }
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Log(args.Data, true);
                    CheckForAuthenticatedUrl(args.Data);
                }
            };

            process.Exited += async (sender, args) =>
            {
                int exitCode = -1;
                try { exitCode = process.ExitCode; } catch { }

                Log($"[系统] DSH 进程已退出 (ExitCode: {exitCode})", exitCode != 0);

                lock (_lock)
                {
                    _currentProcess = null;
                    SetStatus(ProcessStatus.Stopped);
                }

                // Run post-stop extensions（实例模式同样跳过全局配置，理由同启动前扩展）
                if (instance == null)
                {
                    await RunExtensionsAsync(config.Extensions.Where(e => e.IsEnabled && e.RunAfterStop));
                }
                else if (config.Extensions.Any(e => e.IsEnabled && e.RunAfterStop))
                {
                    Log("[系统] 实例模式：已跳过全局停止扩展（可能影响主工作区）。", false);
                }

                // Auto restart check
                if (config.AutoRestartOnCrash && exitCode != 0)
                {
                    Log("[系统] 检测到非正常退出且已开启崩溃自启，3秒后尝试重启...", true);
                    await Task.Delay(3000);
                    _ = StartAsync(config, instance);
                }
            };

            bool started = process.Start();
            if (!started)
            {
                Log("[错误] 进程启动失败", true);
                SetStatus(ProcessStatus.Error);
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 加入 Job Object：启动器被强杀时，这个实例不会被留成孤儿进程占着端口和 profile。
            // 失败不致命（宿主可能不允许嵌套 Job），只记一次原因。
            if (!JobObjectService.Assign(process))
            {
                Log($"[警告] 未能把子进程纳入生命周期 Job（{JobObjectService.LastFailure}）：启动器被强杀时该实例可能残留。", true);
            }

            _currentProcess = process;
            SetStatus(ProcessStatus.Running);
            Log($"[系统] DSH 进程已成功启动 (PID: {process.Id})", false);
            Log("[系统] 已通过 --no-open 接管浏览器交接，认证地址将自动加载到内嵌视图。", false);

            return true;
        }
        catch (Exception ex)
        {
            Log($"[启动异常] {ex.Message}", true);
            SetStatus(ProcessStatus.Error);
            return false;
        }
    }

    /// <summary>普通模式「显示控制台」时的输出尾随器（Tee 写出的文件）。</summary>
    private readonly List<FileTailer> _visibleTailers = new();

    /// <summary>
    /// 普通模式 + 显示控制台：开一个真实控制台窗口，同时输出仍然进启动器面板。
    ///
    /// 关键点：一个输出流只能去一个地方。若把 PowerShell 的 stdout 重定向到管道，
    /// 控制台就空了；若不管道，启动器就收不到 auth token。所以用 Tee-Object
    /// 同时写「控制台」和「文件」，启动器尾随那个文件。
    ///
    /// 这条路仍然是启动器自己拉起的进程，所以 Job Object、退出事件、崩溃自启都还在。
    /// </summary>
    private async Task<bool> StartVisibleConsoleAsync(LauncherConfig config, InstanceConfig? instance,
                                                      string fileName, string arguments, string workDir)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                  "DshLauncher", "visible");
        Directory.CreateDirectory(dir);
        string logOut = Path.Combine(dir, "dsh.log");
        string ps1 = Path.Combine(dir, "run-visible.ps1");
        try { if (File.Exists(logOut)) File.Delete(logOut); } catch { }

        // 写成 .ps1 再 -File 执行：把整条命令塞进 -Command 会掉进引号地狱。
        File.WriteAllText(ps1, $"& '{fileName}' {arguments} 2>&1 | Tee-Object -FilePath '{logOut}'",
                          new UTF8Encoding(false));

        string psExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                    @"WindowsPowerShell\v1.0\powershell.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = psExe,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = false,   // ← 这一项就是「显示控制台」
        };
        SetupEnvironmentVariables(startInfo, config);

        StopVisibleTailing();
        var tailer = new FileTailer(logOut, false, (line, isErr) =>
        {
            Log(line, isErr);
            CheckForAuthenticatedUrl(line);
        });
        _visibleTailers.Add(tailer);
        tailer.Start();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += async (sender, args) =>
        {
            int exitCode = -1;
            try { exitCode = process.ExitCode; } catch { }
            Log($"[系统] DSH 进程已退出 (ExitCode: {exitCode})", exitCode != 0);
            StopVisibleTailing();

            lock (_lock) { _currentProcess = null; SetStatus(ProcessStatus.Stopped); }

            if (instance == null) await RunExtensionsAsync(config.Extensions.Where(e => e.IsEnabled && e.RunAfterStop));
            if (config.AutoRestartOnCrash && exitCode != 0)
            {
                Log("[系统] 检测到非正常退出且已开启崩溃自启，3秒后尝试重启...", true);
                await Task.Delay(3000);
                _ = StartAsync(config, instance);
            }
        };

        if (!process.Start())
        {
            Log("[错误] 进程启动失败", true);
            StopVisibleTailing();
            SetStatus(ProcessStatus.Error);
            return false;
        }

        if (!JobObjectService.Assign(process))
        {
            Log($"[警告] 未能把子进程纳入生命周期 Job（{JobObjectService.LastFailure}）。", true);
        }

        _currentProcess = process;
        SetStatus(ProcessStatus.Running);
        Log($"[系统] DSH 进程已启动 (PID: {process.Id})，控制台窗口可见。", false);
        Log("[系统] 输出经 Tee 同时写入窗口与本面板；认证地址将自动加载到内嵌视图。", false);
        await Task.CompletedTask;
        return true;
    }

    private void StopVisibleTailing()
    {
        foreach (var t in _visibleTailers) t.Dispose();
        _visibleTailers.Clear();
    }

    /// <summary>
    /// 以管理员 / SYSTEM 身份启动。提权进程无法纳入 Job Object（它由 UAC 或计划任务
    /// 创建，不在我们的作业里），所以停止改由「按端口 + 按 PID 文件」的提权脚本负责；
    /// 崩溃自启在这里也做不到（没有 Process 对象可订阅 Exited），改为轮询端口。
    /// </summary>
    private async Task<bool> StartPrivilegedAsync(LauncherConfig config, InstanceConfig? instance,
                                                  string fileName, string arguments, string workDir)
    {
        int port = instance?.Port ?? (config.WebPort > 0 ? config.WebPort : 3080);

        _privilege?.Dispose();
        var priv = new PrivilegeLauncher();
        priv.OnLog += (line, isError) =>
        {
            Log(line, isError);
            CheckForAuthenticatedUrl(line);
        };
        _privilege = priv;
        _activePrivilege = config.Privilege;

        if (config.AutoRestartOnCrash)
        {
            Log("[提示] 提权 / SYSTEM 模式不提供崩溃自启（没有可订阅的子进程句柄），状态改为轮询端口。", false);
        }

        bool ok = await priv.StartAsync(config.Privilege, fileName, arguments, workDir, config, port);
        if (!ok)
        {
            SetStatus(ProcessStatus.Error);
            return false;
        }

        SetStatus(ProcessStatus.Running);
        Log("[系统] 已通过 --no-open 接管浏览器交接，认证地址将自动加载到内嵌视图。", false);
        WatchPrivileged(port);
        return true;
    }

    /// <summary>
    /// 提权模式的实例监测：进程句柄在 UAC / 计划任务边界之外，只能用「间接存活信号」。
    ///
    /// 两个信号，任一为真即视为活着：
    ///   ① 端口监听 —— IPGlobalProperties.GetActiveTcpListeners()
    ///   ② node.pid 里的 PID 仍存活 —— PrivilegeLauncher.IsProcessAlive()
    ///
    /// 旧实现只用 ①，且 8.5 秒就判死。个别环境下 ① 会漏报目标端口，于是活着的实例
    /// 被标成「已停止」：UI 显示"服务未运行"，停止按钮同时被禁用（Stopped 分支会
    /// 关掉 BtnStop），表现就是"显示未运行、又停不掉"。加上 ② 和逐段诊断日志后，
    /// 既不会再误判，真出问题时也能从日志看出是哪一环没等到。
    /// </summary>
    private void WatchPrivileged(int port)
    {
        var priv0 = _privilege;
        if (priv0 == null) return;

        // 用 Safe.RunBackground 而不是裸 Task.Run：监测循环里的任何异常
        // （端口查询失败、PID 读取失败、状态被并发改动）都不会变成未观察异常。
        _ = Safe.RunBackground("WatchPrivileged", async () =>
        {
            // ── 阶段一：启动宽限期 ──────────────────────────────────────────
            // DSH 从进程启动到端口 bind 需要十几秒（插件多时更久），
            // 这段时间「还没有信号」是正常的，不能当成「实例已结束」。
            const int startupGraceMs = 120_000;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool everListened = false;
            bool everAlive = false;
            var nextDiag = TimeSpan.FromSeconds(15);

            while (sw.ElapsedMilliseconds < startupGraceMs)
            {
                if (!ReferenceEquals(priv0, _privilege))
                {
                    Log("[监测] 实例对象已被替换，本次监控退出。", false);
                    return;
                }
                if (Status != ProcessStatus.Running)
                {
                    Log($"[监测] 状态已变为 {Status}（非运行中），本次监控退出。", false);
                    return;
                }

                // 就绪的判据优先是「**我自己的进程**正在监听这个端口」。
                // 只看 IsPortListening(port) 会把「端口被上一个还没退干净的实例占着」
                // 误判成「已就绪」——紧接着 DSH 自己报 EADDRINUSE 退出，形成
                // 「就绪 → 判死 → 清理 → 再启动 → 再 EADDRINUSE」的死循环。
                bool aliveOk = priv0.IsProcessAlive();
                int? ownerPid = PrivilegeLauncher.FindPortOwnerPid(port);
                int? myPid = null;
                if (priv0.TryGetPid(out int tmpPid)) myPid = tmpPid;

                bool ownListening;
                if (ownerPid.HasValue && ownerPid.Value > 0 && myPid.HasValue)
                {
                    // 能查到归属：必须是自己，才算就绪
                    ownListening = ownerPid.Value == myPid.Value;
                }
                else
                {
                    // 查不到归属（SYSTEM 进程监听端口时 netstat 常报 PID 0）：
                    // 不能因为"归属未知"就判死，回退到「端口在听 + 自己的进程还活着」。
                    ownListening = aliveOk && PrivilegeLauncher.IsPortListening(port);
                }

                if (ownListening) { everListened = true; everAlive = true; break; }
                if (aliveOk) everAlive = true;

                // 每 15 秒留一条诊断，避免"起来了却判死"时无从判断是哪一环失败
                if (sw.Elapsed >= nextDiag)
                {
                    string ownerText = ownerPid.HasValue ? ownerPid.Value.ToString() : "无";
                    string myPidText = myPid.HasValue ? myPid.Value.ToString() : "无";
                    Log($"[监测] 等待实例就绪 {sw.Elapsed.TotalSeconds:F0}s：端口监听者={ownerText}，"
                        + $"本实例PID={myPidText}，进程存活={aliveOk}", false);
                    nextDiag += TimeSpan.FromSeconds(15);
                }
                await Task.Delay(400);
            }

            if (!everListened && !everAlive)
            {
                Log($"[监测] {startupGraceMs / 1000} 秒内既没等到本实例监听端口 {port}，也没等到进程存活：判定启动失败。", true);
                lock (_lock)
                {
                    if (ReferenceEquals(priv0, _privilege)) SetStatus(ProcessStatus.Stopped);
                }
                return;
            }

            if (!everListened)
            {
                // 进程活着但始终没自己监听：典型是 DSH 启动后因端口冲突/配置错误自行退出。
                Log($"[监测] 本实例进程曾存活但始终未监听端口 {port}：判定启动失败。", true);
                lock (_lock)
                {
                    if (ReferenceEquals(priv0, _privilege)) SetStatus(ProcessStatus.Stopped);
                }
                return;
            }

            string okPidText = "?";
            if (priv0.TryGetPid(out int okPid)) okPidText = okPid.ToString();
            Log($"[监测] 本实例已就绪（PID {okPidText} 正在监听 {port}），转入掉线监控。", false);

            // ── 阶段二：掉线监控 ────────────────────────────────────────────
            while (true)
            {
                await Task.Delay(2500);
                if (!ReferenceEquals(priv0, _privilege)) return;
                if (Status != ProcessStatus.Running) continue;

                // 进入阶段二时已确认过「本实例曾经在监听」，所以掉线只需看
                // 自己那个 PID 还在不在 —— 端口会被别的进程复用，拿端口判断会误判。
                if (priv0.IsProcessAlive()) continue;

                Log($"[监测] 本实例进程已退出：提权实例已结束。", true);
                lock (_lock)
                {
                    if (ReferenceEquals(priv0, _privilege)) SetStatus(ProcessStatus.Stopped);
                }
                return;
            }
        });
    }

    /// <summary>
    /// 外部已经把实例结束掉了（例如「强制释放端口」按端口强杀）——
    /// 把内部状态拉回 Stopped，让界面与事实一致，避免停在「运行中」却连不上。
    /// </summary>
    public void NotifyExternallyStopped()
    {
        lock (_lock)
        {
            try { _privilege?.Dispose(); } catch { }
            _privilege = null;
            _currentProcess = null;
            SetStatus(ProcessStatus.Stopped);
        }
    }

    /// <summary>
    /// 实例是否**实际**还活着（提权 PID 存活，或普通模式子进程未退出）。
    ///
    /// 用途：即使 UI 状态被误判成 Stopped，也据此保留「停止」按钮可用性，
    /// 避免出现「显示未运行、又停不掉」的死局；关闭软件时也靠它决定要不要善后。
    /// </summary>
    public bool IsInstanceAlive()
    {
        var priv = _privilege;
        if (priv != null && priv.IsProcessAlive()) return true;

        var p = _currentProcess;
        if (p != null)
        {
            try { return !p.HasExited; } catch { }
        }

        // 提权模式兜底：_privilege 可能已被置空（停止过 / 启动器重启过），
        // 但固定路径的 node.pid 仍指向存活进程。探测一次，避免"活着却判死"。
        try
        {
            var probe = new PrivilegeLauncher();
            try { return probe.IsProcessAlive(); }
            finally { probe.Dispose(); }
        }
        catch { return false; }
    }

    public async Task<bool> StopAsync()
    {        // 提权 / SYSTEM：普通路径走不通（taskkill 无权结束提权进程），必须用提权脚本。
        var privToStop = _privilege;
        if (privToStop != null)
        {
            lock (_lock) { SetStatus(ProcessStatus.Stopping); }
            try
            {
                await privToStop.StopAsync();
                privToStop.Dispose();
            }
            catch (Exception ex)
            {
                Log($"[停止异常] {ex.Message}", true);
                SetStatus(ProcessStatus.Error);
                return false;
            }
            finally
            {
                if (ReferenceEquals(privToStop, _privilege)) _privilege = null;
            }
            Log("[系统] 提权实例已停止。", false);
            SetStatus(ProcessStatus.Stopped);
            return true;
        }

        // ── 兜底：没有提权句柄，但实例可能仍活着 ──
        // 场景：状态被误判后用户再点停止、上次停止中途失败、启动器重启过
        // （_privilege 丢了，但固定路径的 node.pid 还在）。
        // 新建一个 PrivilegeLauncher 善后：它按 launch.json 的端口 + node.pid 结束进程。
        if (IsInstanceAlive())
        {
            Log("[停止] 未持有提权句柄，但检测到实例仍在运行，改用 PID/端口兜底停止…", false);
            lock (_lock) { SetStatus(ProcessStatus.Stopping); }

            var recover = new PrivilegeLauncher();
            try { await recover.StopAsync(); }
            catch (Exception ex) { Log($"[停止异常] {ex.Message}", true); }
            finally { recover.Dispose(); }

            if (IsInstanceAlive())
            {
                Log("[停止] 兜底停止后实例仍在运行，请在任务管理器结束 node.exe。", true);
                SetStatus(ProcessStatus.Error);
                return false;
            }
            Log("[系统] 兜底停止成功。", false);
            SetStatus(ProcessStatus.Stopped);
            return true;
        }

        Process? procToKill = null;
        lock (_lock)
        {
            if (_currentProcess == null || _currentProcess.HasExited)
            {
                SetStatus(ProcessStatus.Stopped);
                return true;
            }
            SetStatus(ProcessStatus.Stopping);
            procToKill = _currentProcess;
        }

        try
        {
            Log("[系统] 正在停止 DSH 进程树...", false);
            await Task.Run(() =>
            {
                try
                {
                    // Kill process tree
                    KillProcessTree(procToKill.Id);
                }
                catch (Exception ex)
                {
                    Log($"[警告] 终止进程时遇到提示: {ex.Message}", true);
                }
            });

            Log("[系统] DSH 进程已成功停止", false);
            StopVisibleTailing();
            SetStatus(ProcessStatus.Stopped);
            return true;
        }
        catch (Exception ex)
        {
            Log($"[停止异常] {ex.Message}", true);
            SetStatus(ProcessStatus.Error);
            return false;
        }
    }

    private void KillProcessTree(int pid)
    {
        try
        {
            // Use taskkill /T /F /PID to reliably kill node child processes
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/T /F /PID {pid}",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                proc.Kill(true);
            }
            catch { }
        }
    }

    private (string fileName, string arguments, string workDir) ResolveExecutionTarget(LauncherConfig config, InstanceConfig? instance = null)
    {
        string workDir = Directory.Exists(config.WorkingDirectory)
            ? config.WorkingDirectory
            : Environment.CurrentDirectory;

        string dshPath = config.DshPath.Trim('\"', ' ');

        // 实例模式：按实例的 profile 启动（`dsh --profile <name>`）
        // 单实例模式：沿用原有自定义参数（默认 `web`）
        string args = (instance != null && !string.IsNullOrWhiteSpace(instance.Profile))
            ? $"--profile {instance.Profile}"
            : config.CustomArguments.Trim();

        if (instance != null && !string.IsNullOrWhiteSpace(instance.ExtraArguments))
        {
            args += " " + instance.ExtraArguments.Trim();
        }

        int port = instance != null ? instance.Port : config.WebPort;

        // 是否为 web 应用调用（`dsh web` 或 `dsh --profile <name>`）
        bool isWebInvocation =
            System.Text.RegularExpressions.Regex.IsMatch(args, @"(^|\s)web(\s|$)") ||
            System.Text.RegularExpressions.Regex.IsMatch(args, @"--profile\s+\S+");

        if (isWebInvocation)
        {
            // ① 端口：把界面上的端口真正传给 DSH。同时识别 `--port 9222` / `--port=9222` / `-p 9222`：
            // 漏掉 -p 会让命令行上出现两个端口参数，最终谁生效取决于 DSH 的解析顺序。
            bool portAlreadyGiven = HasArgToken(args, "--port") || HasArgToken(args, "-p");
            if (port > 0 && !portAlreadyGiven)
            {
                args += $" --port {port}";
            }

            // ② --no-open：禁止 DSH 自己拉起系统默认浏览器（由启动器接管内嵌视图）
            if (!System.Text.RegularExpressions.Regex.IsMatch(args, @"(^|\s)--no-open(\s|$)") &&
                !System.Text.RegularExpressions.Regex.IsMatch(args, @"(^|\s)--open(\s|$)"))
            {
                args += " --no-open";
            }
        }

        // 指定 profile 目录时补 --profile（web 调用已隐含 --profile，不重复追加）
        if (!isWebInvocation && !string.IsNullOrWhiteSpace(config.ConfigPath))
        {
            var confPath = config.ConfigPath.Trim('\"', ' ');
            if (Directory.Exists(confPath) &&
                !args.Contains("--profile") && !args.Contains("-c") && !args.Contains("cordis.yml"))
            {
                args += $" --profile \"{confPath}\"";
            }
        }

        // Case 1: dshPath is a package directory (contains package.json)
        if (Directory.Exists(dshPath))
        {
            string nodeExe = !string.IsNullOrWhiteSpace(config.NodePath) && File.Exists(config.NodePath)
                ? config.NodePath
                : "node";

            // package.json 的 bin 字段才是权威答案。猜固定路径会猜错：@deepseek-ai/dsh 的
            // 真实入口是 lib/bin.js，而 bin/dsh.js、dist/bin/dsh.js、index.js 一个都不存在。
            // 猜错的代价不是报错，而是静默退到 `cmd.exe /c dsh` —— 变成依赖 PATH 上恰好有
            // dsh 这个命令。在 SYSTEM 会话里那纯属运气（PATH 是启动器自己注入的）。
            foreach (string entry in EnumerateDshEntries(dshPath))
            {
                return (nodeExe, $"\"{entry}\" {args}", workDir);
            }
        }

        // Case 2: dshPath is a .cmd or .bat or .ps1 file
        if (File.Exists(dshPath))
        {
            var ext = Path.GetExtension(dshPath).ToLowerInvariant();
            if (ext == ".cmd" || ext == ".bat" || ext == ".exe")
            {
                return (dshPath, args, workDir);
            }
            if (ext == ".js")
            {
                string nodeExe = !string.IsNullOrWhiteSpace(config.NodePath) && File.Exists(config.NodePath)
                    ? config.NodePath
                    : "node";
                return (nodeExe, $"\"{dshPath}\" {args}", workDir);
            }
        }

        // Fallback: run via cmd.exe /c
        return ("cmd.exe", $"/c dsh {args}", workDir);
    }

    /// <summary>
    /// 按可靠性从高到低给出 DSH 包目录内的候选入口，返回第一个真实存在的。
    /// 顺序：package.json 的 bin 字段 → 已知布局。
    /// </summary>
    private static IEnumerable<string> EnumerateDshEntries(string packageDir)
    {
        // 1) package.json 的 bin：这是包自己声明的入口，不猜。
        //    yield 不能出现在带 catch 的 try 块里（C# 迭代器限制），所以先算出来再 yield。
        string? declared = null;
        try
        {
            string pj = Path.Combine(packageDir, "package.json");
            if (File.Exists(pj))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pj));
                if (doc.RootElement.TryGetProperty("bin", out var bin))
                {
                    string? rel = bin.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => bin.GetString(),
                        System.Text.Json.JsonValueKind.Object when bin.TryGetProperty("dsh", out var d) => d.GetString(),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(rel))
                    {
                        string full = Path.GetFullPath(Path.Combine(packageDir, rel));
                        if (File.Exists(full)) declared = full;
                    }
                }
            }
        }
        catch
        {
            // package.json 缺失或损坏：退回已知布局，不要因此拒绝启动。
        }
        if (declared != null) yield return declared;

        // 2) 已知布局。lib/bin.js 排最前，因为当前的 @deepseek-ai/dsh 就是它。
        foreach (string rel in new[] { @"lib\bin.js", @"bin\dsh.js", @"dist\bin\dsh.js", "index.js" })
        {
            string full = Path.Combine(packageDir, rel);
            if (File.Exists(full)) yield return full;
        }
    }

    /// <summary>
    /// 命令行里是否已经出现与 <paramref name="token"/> 完全相同的参数。
    /// 判定按参数边界（行首/空白 起，空白/'='/行尾 止），而不是子串包含——
    /// 否则 <c>-p</c> 会在 <c>--profile</c>、<c>--port</c> 里被误命中，导致端口参数被漏掉。
    /// </summary>
    private static bool HasArgToken(string args, string token)
    {
        if (string.IsNullOrWhiteSpace(args) || string.IsNullOrEmpty(token)) return false;

        int index = args.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            bool leftBoundary = index == 0 || char.IsWhiteSpace(args[index - 1]);
            int end = index + token.Length;
            bool rightBoundary = end >= args.Length
                || char.IsWhiteSpace(args[end])
                || args[end] == '=';

            if (leftBoundary && rightBoundary) return true;

            index = args.IndexOf(token, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex UrlRegex =
        new(@"https?://[^\s""'<>]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 从 DSH 输出中捕获带进程 token 的认证地址（`dsh web: http://127.0.0.1:PORT/?auth=...`）。
    /// 只有该地址才能通过 web 的浏览器信任围栏，直接访问裸端口会提示 authentication required。
    /// </summary>
    private void CheckForAuthenticatedUrl(string line)
    {
        try
        {
            var match = UrlRegex.Match(line);
            if (!match.Success) return;

            string url = match.Value.TrimEnd(')', ']', '.', ',', ';', '。', '，');
            if (!url.Contains("127.0.0.1") && !url.Contains("localhost")) return;

            bool isWebUrlLine = line.TrimStart().StartsWith("dsh web:", StringComparison.OrdinalIgnoreCase);
            bool hasToken = url.Contains("auth=", StringComparison.OrdinalIgnoreCase)
                         || url.Contains("token=", StringComparison.OrdinalIgnoreCase);

            // 优先采信带 token 的地址或 `dsh web:` 行；首次兜底任何本地地址
            if (!hasToken && !isWebUrlLine && LastAuthenticatedUrl != null) return;
            if (string.Equals(LastAuthenticatedUrl, url, StringComparison.Ordinal)) return;

            LastAuthenticatedUrl = url;
            OnAuthenticatedUrlDetected?.Invoke(url);
        }
        catch { }
    }

    private void SetupEnvironmentVariables(ProcessStartInfo psi, LauncherConfig config)
    {
        // 1. Proxy
        if (config.EnableProxy && !string.IsNullOrWhiteSpace(config.ProxyAddress))
        {
            psi.Environment["HTTP_PROXY"] = config.ProxyAddress;
            psi.Environment["HTTPS_PROXY"] = config.ProxyAddress;
            psi.Environment["http_proxy"] = config.ProxyAddress;
            psi.Environment["https_proxy"] = config.ProxyAddress;
            psi.Environment["NO_PROXY"] = "localhost,127.0.0.1,api.deepseek.com";
            psi.Environment["no_proxy"] = "localhost,127.0.0.1,api.deepseek.com";
        }

        // 2. Custom Environment Variables
        if (config.CustomEnv != null)
        {
            foreach (var kv in config.CustomEnv)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                {
                    psi.Environment[kv.Key] = kv.Value;
                }
            }
        }

        // 3. Node 堆上限：用户要求「内存无上限」，把约束交给操作系统。
        //    放在 CustomEnv 之后，这样用户自己设的 NODE_OPTIONS 会被尊重（只追加不覆盖）。
        try
        {
            psi.Environment.TryGetValue(NodeMemoryOptions.EnvName, out var existingNodeOpts);
            psi.Environment[NodeMemoryOptions.EnvName] = NodeMemoryOptions.BuildValue(existingNodeOpts);
        }
        catch (Exception ex)
        {
            Safe.Report("SetupEnvironmentVariables.NodeHeap", ex);
        }
    }

    private async Task RunExtensionsAsync(IEnumerable<ExtensionItem> extensions)
    {
        foreach (var ext in extensions)
        {
            try
            {
                Log($"[扩展] 正在执行扩展任务: {ext.Name} ({ext.Command})", false);
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {ext.Command}",
                    WorkingDirectory = Directory.Exists(ext.WorkingDirectory) ? ext.WorkingDirectory : Environment.CurrentDirectory,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var outText = await proc.StandardOutput.ReadToEndAsync();
                    var errText = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    if (!string.IsNullOrWhiteSpace(outText)) Log($"[{ext.Name} 输出] {outText.Trim()}", false);
                    if (!string.IsNullOrWhiteSpace(errText)) Log($"[{ext.Name} 错误] {errText.Trim()}", true);
                }
            }
            catch (Exception ex)
            {
                Log($"[扩展执行失败] {ext.Name}: {ex.Message}", true);
            }
        }
    }

    private void SetStatus(ProcessStatus newStatus)
    {
        Status = newStatus;
        OnStatusChanged?.Invoke(newStatus);
    }

    private void Log(string message, bool isError)
    {
        OnLogReceived?.Invoke(message, isError);
    }
}
