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
                Log("服务已在运行或正在启动中...", true);
                return false;
            }
            SetStatus(ProcessStatus.Starting);
        }

        try
        {
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
    /// 提权模式下唯一可用的存活信号是端口：进程句柄在 UAC / 计划任务边界之外。
    /// 端口掉了就说明实例没了 —— 否则界面会一直停在「运行中」。
    /// </summary>
    private void WatchPrivileged(int port)
    {
        var priv0 = _privilege;
        if (priv0 == null) return;

        _ = Task.Run(async () =>
        {
            // ── 阶段一：启动宽限期 ──────────────────────────────────────────
            // DSH 从进程启动到端口 bind 需要十几秒（插件多时更久）。
            // 旧实现固定等 6s + 2.5s 就判死，而实测启动要 13s —— 于是每次启动都会
            // 误报「端口已不再监听：提权实例已结束」，把状态错置为 Stopped，
            // 紧接着 WebView2 初始化就撞上这个已被判死的状态（RPC_E_DISCONNECTED）。
            // 正确做法：先等它「首次监听成功」，之后再监控它是否掉线。
            const int startupGraceMs = 90_000;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool everListened = false;

            while (sw.ElapsedMilliseconds < startupGraceMs)
            {
                if (!ReferenceEquals(priv0, _privilege)) return;
                if (Status != ProcessStatus.Running) return;   // 已被别处停止
                if (PrivilegeLauncher.IsPortListening(port)) { everListened = true; break; }
                await Task.Delay(400);
            }

            if (!everListened)
            {
                Log($"[系统] 端口 {port} 在 {startupGraceMs / 1000} 秒内始终未监听：提权实例启动失败。", true);
                lock (_lock)
                {
                    if (ReferenceEquals(priv0, _privilege)) SetStatus(ProcessStatus.Stopped);
                }
                return;
            }

            Log($"[系统] 端口 {port} 已就绪，转入掉线监控。", false);

            // ── 阶段二：掉线监控 ────────────────────────────────────────────
            while (true)
            {
                await Task.Delay(2500);
                if (!ReferenceEquals(priv0, _privilege)) return;
                if (Status != ProcessStatus.Running) continue;
                if (PrivilegeLauncher.IsPortListening(port)) continue;

                Log($"[系统] 端口 {port} 已不再监听：提权实例已结束。", true);
                lock (_lock)
                {
                    if (ReferenceEquals(priv0, _privilege)) SetStatus(ProcessStatus.Stopped);
                }
                return;
            }
        });
    }

    public async Task<bool> StopAsync()
    {
        // 提权 / SYSTEM：普通路径走不通（taskkill 无权结束提权进程），必须用提权脚本。
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
