using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher;

/// <summary>
/// 「DSH 救援」内嵌侧栏 —— 最小聊天界面，取代原来的按钮农场。
/// 用户直接对一个独立的、一次性的 DSH 救援实例说话；会话修复由 agent 自己跑工具
/// （pwsh / run_code / session-manager.mjs）完成，而不是点「修复」按钮。
///
/// 关键事实（已验证）：
///   救援快照（DSH harness 的代码副本）在 %LOCALAPPDATA%\DshLauncher\assets\rescue\，
///   入口 = assets\rescue\dsh\dsh\lib\bin.js，隔离的 DSH_HOME = assets\rescue\home\。
///   每次发送起一个一次性 headless 子进程（node bin.js --profile rescue "&lt;prompt&gt;"），
///   跑完即退：从不常驻、不监听端口、不启动/停止主 DSH。多轮上下文靠把历史拼进 prompt。
///   救援 home 必须含 settings.yaml 与 .credentials.yaml（缺则从 %USERPROFILE%\.dsh 复制）。
///
/// 线程模型：每个回合/脚本命令都是 async，stdout/stderr 行级异步读取，
/// 经 PostToUi 编组 + _detached 守卫；所有绑定目标都是属性（WPF 只认属性，不认字段）。
/// </summary>
public partial class RescuePanel : UserControl, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private const int HistoryCap = 20;

    /// <summary>
    /// 救援脚本的执行上限。没有它的话，脚本一旦卡住，IsBusy 永远为真、面板看起来就是"死"了。
    /// 8 分钟足够 create/verify/repair 这类重活跑完，又不至于让用户无限期干等。
    /// </summary>
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(8);

    /// <summary>
    /// 救援对话回合的上限。一次 LLM 往返本来就可能好几分钟，所以给得比脚本宽，
    /// 但必须有：模型或网络停住时，没有超时会让面板永远停在"正在思考…"。
    /// </summary>
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromMinutes(20);

    // ─────────────────────────── 状态 ───────────────────────────

    private LauncherConfig _config = new();

    /// <summary>串行化：同一时刻只允许一个回合或一条脚本命令。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>宿主窗口已关闭：之后一切 UI 操作直接丢弃。</summary>
    private bool _detached;

    private bool _attached;

    private int _busy;

    private string? _nodeExe;
    private string? _pwshExe;

    private string _rescueRoot = "";
    private string _rescueHome = "";
    private string _rescueBin = "";
    private string _sessionManager = "";

    private readonly ObservableCollection<ChatMessage> _messages = new();
    private readonly List<(string User, string Assistant)> _history = new();
    private readonly object _historyLock = new();

    private bool _isBusy;
    private string _busyHint = "救援 DSH 正在思考…";
    private string _snapshotStateText = "快照: 读取中…";
    private string _modelRouteText = "模型: 读取中…";

    // ─────────────────────────── 绑定目标（全是属性，绝不字段） ───────────────────────────

    public ObservableCollection<ChatMessage> Messages => _messages;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanSend));
        }
    }

    public bool CanSend => !_isBusy;

    public bool ShowEmptyHint => _messages.Count == 0;

    public string BusyHint
    {
        get => _busyHint;
        private set { if (_busyHint == value) return; _busyHint = value; OnPropertyChanged(nameof(BusyHint)); }
    }

    public string SnapshotStateText
    {
        get => _snapshotStateText;
        private set { if (_snapshotStateText == value) return; _snapshotStateText = value; OnPropertyChanged(nameof(SnapshotStateText)); }
    }

    public string ModelRouteText
    {
        get => _modelRouteText;
        private set { if (_modelRouteText == value) return; _modelRouteText = value; OnPropertyChanged(nameof(ModelRouteText)); }
    }

    public RescuePanel()
    {
        InitializeComponent();
        DataContext = this;
    }

    // ─────────────────────────── 宿主生命周期 ───────────────────────────

    /// <summary>绑定宿主配置并做一次性初始化（解析路径 / 同步凭据 / 刷新状态行，无模型调用）。</summary>
    public void Attach(LauncherConfig config)
    {
        _config = config ?? new LauncherConfig();
        _nodeExe = null;
        _attached = true;

        ResolvePaths();
        EnsureCredentials();
        RefreshStatusLine();
    }

    /// <summary>侧栏首次展开时调用：重读一次状态行（纯本地，无模型、无进程）。</summary>
    public Task ActivateAsync()
    {
        if (_attached) RefreshStatusLine();
        return Task.CompletedTask;
    }

    /// <summary>宿主窗口关闭：置守卫，之后一切回调直接丢弃（正在跑的子进程不杀，让它跑完）。</summary>
    public void Detach() => _detached = true;

    // ─────────────────────────── 路径解析 ───────────────────────────

    private void ResolvePaths()
    {
        try
        {
            string assetsRoot = EmbeddedAssets.EnsureAssets();
            _rescueRoot = Path.Combine(assetsRoot, "rescue");
            _rescueHome = Path.Combine(_rescueRoot, "home");
            _rescueBin = Path.Combine(_rescueRoot, "dsh", "dsh", "lib", "bin.js");
            _sessionManager = EmbeddedAssets.SessionManager;
        }
        catch (Exception ex)
        {
            _rescueRoot = ""; _rescueHome = ""; _rescueBin = ""; _sessionManager = "";
            SnapshotStateText = "快照: 资源解包失败（" + ex.Message + "）";
        }
    }

    /// <summary>
    /// node 的解析规则与 ProcessRunnerService / SessionsWindow 完全一致：
    /// 配置里的 NodePath（存在才算），否则 "node"（PATH）。
    /// </summary>
    private string ResolveNodePath()
    {
        if (_nodeExe != null) return _nodeExe;

        foreach (string? candidate in new[] { _config.NodePath, LoadConfigNodePath() })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                _nodeExe = candidate;
                return _nodeExe;
            }
        }

        _nodeExe = "node";
        return _nodeExe;
    }

    private static string LoadConfigNodePath()
    {
        try { return ConfigService.LoadConfig().NodePath ?? ""; }
        catch { return ""; }
    }

    /// <summary>pwsh 的解析：PowerShell 7 常规安装位 → 退回 PATH 的 "pwsh"。</summary>
    private string ResolvePwshPath()
    {
        if (_pwshExe != null) return _pwshExe;

        var candidates = new List<string>();
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) candidates.Add(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData)) candidates.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps", "pwsh.exe"));

        foreach (string c in candidates)
        {
            if (File.Exists(c)) { _pwshExe = c; return c; }
        }

        _pwshExe = "pwsh";
        return _pwshExe;
    }

    /// <summary>救援脚本只有一个来源：EmbeddedAssets.RescueScript（单文件 exe 下解包到磁盘的真实路径）。</summary>
    private static bool TryResolveScript(out string path, out string error)
    {
        path = "";
        error = "";
        try
        {
            string p = EmbeddedAssets.RescueScript;
            if (string.IsNullOrWhiteSpace(p)) { error = "EmbeddedAssets.RescueScript 返回了空路径。"; return false; }
            if (!File.Exists(p)) { error = $"救援脚本不在磁盘上：{p}"; return false; }
            path = p;
            return true;
        }
        catch (Exception ex)
        {
            error = $"解析救援脚本路径失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>脚本的工作目录：<root>\tools\rescue-dsh.ps1 → <root>。</summary>
    private static string RescueRootOf(string scriptPath)
    {
        try
        {
            string? toolsDir = Path.GetDirectoryName(scriptPath);
            string? root = toolsDir == null ? null : Path.GetDirectoryName(toolsDir);
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root)) return root!;
            if (!string.IsNullOrWhiteSpace(toolsDir) && Directory.Exists(toolsDir)) return toolsDir!;
        }
        catch { }
        return Environment.CurrentDirectory;
    }

    // ─────────────────────────── 状态行（快照 + 模型，纯本地读取） ───────────────────────────

    private void RefreshStatusLine()
    {
        string manifest = Path.Combine(_rescueRoot, "manifest.json");

        if (!File.Exists(_rescueBin) || !File.Exists(manifest))
        {
            // 快照已内嵌进 exe（懒解包），不再是「缺失需重建」——首次对话会自动解压。
            SnapshotStateText = "快照: 内嵌（首次对话自动解压）";
        }
        else
        {
            var (mb, pkgs) = ReadSnapshotStats(manifest);
            SnapshotStateText = mb.Length > 0
                ? $"快照: 存在 {mb} / {pkgs} 包"
                : "快照: 存在";
        }

        string model = ReadModelRoute();
        ModelRouteText = model.Length > 0 ? $"模型: {model}" : "模型: 未配置";
    }

    /// <summary>manifest.json 里 trees[] 的体积合计 + core 的 packageCount。</summary>
    private static (string Mb, long Packages) ReadSnapshotStats(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            long totalBytes = 0, pkgs = 0;
            if (doc.RootElement.TryGetProperty("trees", out var trees) && trees.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in trees.EnumerateArray())
                {
                    totalBytes += GetLong(t, "bytes");
                    if (GetString(t, "name") == "core") pkgs = GetLong(t, "packageCount");
                }
            }
            string mb = (totalBytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + "MB";
            return (mb, pkgs);
        }
        catch
        {
            return ("", 0);
        }
    }

    /// <summary>救援 home 的 settings.yaml 里 agent-default-model 的 provider / model。</summary>
    private string ReadModelRoute()
    {
        try
        {
            string path = Path.Combine(_rescueHome, "settings.yaml");
            if (!File.Exists(path)) return "";

            bool inBlock = false;
            string model = "", provider = "";
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.TrimEnd('\r');
                if (!inBlock)
                {
                    if (Regex.IsMatch(line, @"^agent-default-model\s*:")) { inBlock = true; continue; }
                    continue;
                }
                if (line.Length > 0 && !char.IsWhiteSpace(line[0])) break; // 块结束

                Match m = Regex.Match(line, @"^\s*(provider|model)\s*:\s*(.+?)\s*$");
                if (m.Success)
                {
                    if (m.Groups[1].Value == "provider") provider = m.Groups[2].Value;
                    else model = m.Groups[2].Value;
                }
            }

            if (model.Length == 0) return "";
            return provider.Length > 0 ? $"{model}（{provider}）" : model;
        }
        catch { return ""; }
    }

    // ─────────────────────────── 凭据同步 ───────────────────────────

    /// <summary>
    /// 保证救援 home 里有 settings.yaml 与 .credentials.yaml（缺则从 %USERPROFILE%\.dsh 复制）。
    /// 注意：这会把 API key 复制进救援目录 —— 上面的永久提示里必须如实告诉用户。
    /// </summary>
    private void EnsureCredentials()
    {
        try
        {
            string realHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            if (!Directory.Exists(_rescueHome)) Directory.CreateDirectory(_rescueHome);

            foreach (string name in new[] { "settings.yaml", ".credentials.yaml" })
            {
                string dest = Path.Combine(_rescueHome, name);
                string src = Path.Combine(realHome, name);
                if (File.Exists(src) && !File.Exists(dest))
                {
                    File.Copy(src, dest, overwrite: false);
                }
            }
        }
        catch (Exception ex)
        {
            AppendSystem("凭据同步失败：" + ex.Message);
        }
    }

    // ─────────────────────────── 对话回合 ───────────────────────────

    private async void BtnSend_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private void TxtInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return; // Shift+Enter = 换行
        e.Handled = true;
        _ = SendAsync();
    }

    private async Task SendAsync()
    {
        if (_busy > 0) return;

        string text = (TxtInput.Text ?? "").Trim();
        if (text.Length == 0) return;

        if (!_attached) { AppendSystem("面板尚未初始化（未 Attach），无法发送。"); return; }

        // 凭据同步：第一次发送前（以及任何 MISSING_CREDENTIAL 失败后）保证两份文件在位。
        EnsureCredentials();

        // 快照缺失时懒解包内嵌 zip（首次约 20–40 秒）；已在位则跳过（EnsureRescueSnapshot 幂等）。
        if (!File.Exists(_rescueBin) || !File.Exists(Path.Combine(_rescueRoot, "manifest.json")))
        {
            if (!await EnsureSnapshotAsync()) return;
        }

        TxtInput.Clear();

        AddMessage(new ChatMessage(ChatRole.User) { Text = text });
        lock (_historyLock)
        {
            _history.Add((text, ""));
            if (_history.Count > HistoryCap) _history.RemoveAt(0);
        }

        await RunTurnAsync(text);
    }

    /// <summary>
    /// 懒解包内嵌的救援快照（首次约需 20–40 秒）。解包在后台线程跑，UI 显示忙碌态。
    /// 成功返回 true 并刷新路径/凭据/状态行；失败返回 false 且已提示系统消息（不继续对话）。
    /// </summary>
    private async Task<bool> EnsureSnapshotAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        BeginBusy("snapshot");
        try
        {
            BusyHint = "正在解压救援快照（首次约需 20–40 秒）…";
            AppendSystem("首次使用需解压内嵌的救援快照（约 61 MB，首次约 20–40 秒），请稍候…");

            string root;
            try
            {
                root = await Task.Run(() => EmbeddedAssets.EnsureRescueSnapshot()).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppendSystem("救援快照解压失败：" + ex.Message);
                return false;
            }

            _rescueRoot = root;
            _rescueHome = Path.Combine(root, "home");
            _rescueBin = Path.Combine(root, "dsh", "dsh", "lib", "bin.js");
            _sessionManager = EmbeddedAssets.SessionManager;

            EnsureCredentials();
            RefreshStatusLine();
            return true;
        }
        finally
        {
            EndBusy();
            _gate.Release();
        }
    }

    private async Task RunTurnAsync(string userText)
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        BeginBusy("chat");
        try
        {
            await RunTurnCoreAsync(userText).ConfigureAwait(true);
        }
        finally
        {
            EndBusy();
            _gate.Release();
        }
    }

    private async Task RunTurnCoreAsync(string userText)
    {
        string prompt = BuildPrompt(userText);
        string node = ResolveNodePath();

        // 助手回答气泡：遇到第一个 text/final 事件才创建。思考/操作块在它之前，
        // 这样消息顺序才和完整 DSH 一致（先思考 → 再操作 → 最后回答）。
        ChatMessage? assMsg = null;
        var answerBuf = new StringBuilder();
        string finalText = "";

        var psi = new ProcessStartInfo
        {
            FileName = node,
            Arguments = $"\"{_rescueBin}\" --profile rescue --json {QuoteArg(prompt)}",
            WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(_rescueBin)) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["DSH_HOME"] = _rescueHome;

        var outBuf = new StringBuilder();
        var errBuf = new StringBuilder();
        int exitCode = -1;
        string error = "";

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                string line = StripAnsi(e.Data);
                outBuf.AppendLine(line);
                HandleJsonEvent(line, ref assMsg, answerBuf, ref finalText);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                string line = StripAnsi(e.Data);
                errBuf.AppendLine(line);
                // 推理/思考现在走 --json 的 thinking 事件；stderr 只剩真正的诊断，折叠进 tooltip
                if (assMsg != null) SetToolTip(assMsg, "stderr（诊断，非答案）：\n" + errBuf);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 同上：对话回合也要有上限，否则模型/网络停住时面板永远停在"正在思考…"。
            var chatExit = proc.WaitForExitAsync();
            var chatDone = await Task.WhenAny(chatExit, Task.Delay(ChatTimeout)).ConfigureAwait(true);
            if (chatDone != chatExit)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                error = $"超过 {ChatTimeout.TotalMinutes:0} 分钟仍未返回，已强制终止";
                exitCode = -1;
            }
            else
            {
                await chatExit.ConfigureAwait(true);
                await Task.Run(() => proc.WaitForExit()).ConfigureAwait(true);
                exitCode = proc.ExitCode;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            exitCode = -1;
        }

        if (exitCode == 0 && error.Length == 0)
        {
            string answer = finalText.Length > 0 ? finalText : answerBuf.ToString().Trim();
            if (assMsg == null)
            {
                assMsg = new ChatMessage(ChatRole.Assistant);
                AddMessage(assMsg);
            }
            if (answer.Length == 0) answer = "（救援 DSH 已结束，但没有返回文本。）";
            SetMessageText(assMsg, answer);
            SetToolTip(assMsg, BuildTooltip(outBuf, errBuf));
            lock (_historyLock) { if (_history.Count > 0) _history[^1] = (userText, answer); }
        }
        else
        {
            string errTail = Tail(errBuf.ToString(), 2000).Trim();
            string outTail = Tail(outBuf.ToString(), 1000).Trim();
            string final;
            // 措辞要覆盖两种情况：启动失败 与 超时被终止（后者不是"启动失败"）
            if (error.Length > 0) final = "救援 DSH 未正常完成：" + error;
            else if (errTail.Length > 0) final = $"救援 DSH 退出码 {exitCode}，stderr 末尾：\n" + errTail;
            else final = $"救援 DSH 退出码 {exitCode}，stdout 末尾：\n" + outTail;
            SetMessageText(assMsg, final);
            SetToolTip(assMsg, BuildTooltip(outBuf, errBuf));

            // 失败不把错误当答案写进历史，只留用户消息占位。
            lock (_historyLock) { if (_history.Count > 0) _history[^1] = (userText, ""); }

            // MISSING_CREDENTIAL → 重新同步凭据并提示重发。
            string combined = errBuf.ToString() + outBuf.ToString();
            if (combined.IndexOf("MISSING_CREDENTIAL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                EnsureCredentials();
                AppendSystem("检测到 MISSING_CREDENTIAL：已重新同步救援目录的凭据，请重发一次。");
            }
        }
    }

    /// <summary>
    /// 解析 headless DSH 的 --json 事件流（换行分隔 JSON），把思考 / 工具操作 / 文本
    /// 分别渲染成独立气泡，顺序与完整 DSH 一致：先思考 → 再操作 → 最后回答。
    /// 事件 schema 见 dsh-headless 的 json-stream：thinking / tool_call / tool_result /
    /// text / final / status / session。status/session 不渲染，保持清爽。
    /// </summary>
    private void HandleJsonEvent(string line, ref ChatMessage? assMsg, StringBuilder answerBuf, ref string finalText)
    {
        if (line.Length == 0) return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch { return; }   // 非 JSON 行（例如启动横幅）直接忽略
        using (doc)
        {
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";

            switch (type)
            {
                case "thinking":
                    {
                        string text = GetString(root, "text");
                        if (text.Length > 0)
                            AddMessage(new ChatMessage(ChatRole.Thinking) { Text = "💭 " + text });
                        break;
                    }
                case "tool_call":
                    {
                        string tool = GetString(root, "tool");
                        string input = root.TryGetProperty("input", out var inp) ? inp.GetRawText() : "";
                        string body = "🔧 " + tool;
                        if (input.Length > 2) body += "\n" + Tail(input, 1500);
                        AddMessage(new ChatMessage(ChatRole.Tool) { Text = body });
                        break;
                    }
                case "tool_result":
                    {
                        string result = GetString(root, "result");
                        string status = GetString(root, "status");
                        AddMessage(new ChatMessage(ChatRole.Tool) { Text = (status == "error" ? "✗ " : "↩ ") + Tail(result, 2000) });
                        break;
                    }
                case "text":
                    {
                        string text = GetString(root, "text");
                        if (text.Length == 0) break;
                        if (assMsg == null) { assMsg = new ChatMessage(ChatRole.Assistant); AddMessage(assMsg); }
                        answerBuf.Append(text);
                        AppendToMessage(assMsg, text);
                        break;
                    }
                case "final":
                    {
                        string text = GetString(root, "text");
                        finalText = text;
                        if (assMsg == null) { assMsg = new ChatMessage(ChatRole.Assistant); AddMessage(assMsg); }
                        // final 是权威答案，覆盖流式累积的中间文本
                        SetMessageText(assMsg, text);
                        break;
                    }
                // session / status / error 等事件：不渲染
            }
        }
    }

    /// <summary>
    /// 提示词（精确形状）：告知 agent 它是救援助手、会话工具在哪、破坏性操作先备份，
    /// 然后带最近 20 轮对话历史，最后是用户最新消息。
    /// </summary>
    private string BuildPrompt(string userText)
    {
        string sm = _sessionManager;

        var sb = new StringBuilder();
        sb.Append("你是 DSH 救援助手，运行在一个独立的 DSH 实例里。你可以用工具（pwsh、run_code）检查和修复 DSH 会话日志。会话工具在 ");
        sb.Append(sm);
        sb.Append("，例如：node \"");
        sb.Append(sm);
        sb.Append("\" scan；node \"");
        sb.Append(sm);
        sb.Append("\" repair --id <id>；破坏性操作前先备份。");
        sb.Append("\n\n以下是本次对话历史：\n");

        List<(string User, string Assistant)> hist;
        lock (_historyLock) { hist = _history.ToList(); }

        if (hist.Count == 0)
        {
            sb.Append("（无）");
        }
        else
        {
            int start = Math.Max(0, hist.Count - HistoryCap);
            for (int i = start; i < hist.Count; i++)
            {
                sb.Append("用户: ").Append(SanitizeForPrompt(hist[i].User)).Append('\n');
                sb.Append("助手: ").Append(hist[i].Assistant.Length > 0 ? SanitizeForPrompt(hist[i].Assistant) : "（无回复）").Append('\n');
            }
        }

        sb.Append("\n用户最新消息：").Append(SanitizeForPrompt(userText));
        return sb.ToString();
    }

    /// <summary>历史/消息里换行会破坏单参数 argv，压成空格（仅影响 prompt，气泡仍保留换行）。</summary>
    private static string SanitizeForPrompt(string s) => s.Replace("\r", " ").Replace("\n", " ");

    // ─────────────────────────── 脚本动作（三个次要按钮） ───────────────────────────

    private async void BtnRebuild_Click(object sender, RoutedEventArgs e)
    {
        if (_busy > 0) return;

        var answer = MessageBox.Show(HostWindow(),
            "将把本机 DSH 安装复制成救援快照（复制代码，约 200+ MB；不含你的会话、工作区、存储与设置）。\n\n继续？",
            "重建快照", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        await RunScriptAsync(BuildArgs("create"), "重建快照（create）", "create");
        RefreshStatusLine();
    }

    private void BtnClearChat_Click(object sender, RoutedEventArgs e)
    {
        if (_busy > 0) return;
        PostToUi(() =>
        {
            _messages.Clear();
            lock (_historyLock) { _history.Clear(); }
            UpdateEmptyHint();
        });
    }

    private async void BtnHealth_Click(object sender, RoutedEventArgs e)
    {
        if (_busy > 0) return;
        await RunScriptAsync(BuildArgs("status"), "体检（status）", "status");
        RefreshStatusLine();
    }

    private string BuildArgs(string command)
    {
        var sb = new StringBuilder(command);
        string node = ResolveNodePath();
        if (File.Exists(node)) sb.Append(" -NodeExe ").Append(QuoteArg(node));
        return sb.ToString();
    }

    private async Task RunScriptAsync(string args, string purpose, string busyKey)
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        BeginBusy(busyKey);
        try
        {
            await RunScriptCoreAsync(args, purpose).ConfigureAwait(true);
        }
        finally
        {
            EndBusy();
            _gate.Release();
        }
    }

    private async Task RunScriptCoreAsync(string args, string purpose)
    {
        if (!TryResolveScript(out string script, out string scriptError))
        {
            AppendSystem($"{purpose} 无法执行：{scriptError}");
            return;
        }

        string pwsh = ResolvePwshPath();
        string workDir = RescueRootOf(script);
        string pwshArgs = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {QuoteArg(script)} {args}";

        var sysMsg = new ChatMessage(ChatRole.System) { Text = "▶ " + purpose + " …" };
        AddMessage(sysMsg);

        var psi = new ProcessStartInfo
        {
            FileName = pwsh,
            Arguments = pwshArgs,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var outBuf = new StringBuilder();
        var errBuf = new StringBuilder();
        int exitCode = -1;
        string error = "";

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                string line = StripAnsi(e.Data);
                outBuf.AppendLine(line);
                AppendToMessage(sysMsg, line + Environment.NewLine);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                string line = StripAnsi(e.Data);
                errBuf.AppendLine(line);
                SetToolTip(sysMsg, "stderr：\n" + errBuf);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 没有超时是结构性缺陷：脚本一旦卡住，IsBusy 永远为真，面板看起来就是"死"了 ——
            // 用户只能干等，也拿不到任何线索。到点强杀整棵进程树并把已捕获输出回显，
            // 让"卡在哪"变得可见。
            var exit = proc.WaitForExitAsync();
            var finished = await Task.WhenAny(exit, Task.Delay(ScriptTimeout)).ConfigureAwait(true);
            if (finished != exit)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                error = $"超过 {ScriptTimeout.TotalMinutes:0} 分钟仍未结束，已强制终止";
                exitCode = -1;
            }
            else
            {
                await exit.ConfigureAwait(true);
                await Task.Run(() => proc.WaitForExit()).ConfigureAwait(true);
                exitCode = proc.ExitCode;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            exitCode = -1;
        }

        if (exitCode == 0)
        {
            AppendToMessage(sysMsg, "\n✔ " + purpose + " 完成");
        }
        else
        {
            string tail = Tail(errBuf.ToString(), 3000).Trim();
            AppendToMessage(sysMsg,
                "\n⚠ " + purpose + " 退出码 " + exitCode
                + (error.Length > 0 ? "（" + error + "）" : "")
                + (tail.Length > 0 ? "\n" + tail : ""));
        }
        SetToolTip(sysMsg, BuildTooltip(outBuf, errBuf));
    }

    // ─────────────────────────── 忙碌态 / UI 编组 ───────────────────────────

    private void BeginBusy(string key)
    {
        _busy++;
        BusyHint = key == "chat" ? "救援 DSH 正在思考…" : "正在执行救援命令…";
        IsBusy = _busy > 0;
    }

    private void EndBusy()
    {
        if (_busy > 0) _busy--;
        if (_busy == 0) IsBusy = false;
    }

    private Window? HostWindow() => Window.GetWindow(this);

    private void PostToUi(Action action)
    {
        if (_detached) return;
        try
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.Invoke(action);
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void UpdateEmptyHint() => OnPropertyChanged(nameof(ShowEmptyHint));

    private void AddMessage(ChatMessage msg)
    {
        PostToUi(() =>
        {
            _messages.Add(msg);
            UpdateEmptyHint();
            ChatScroll.ScrollToEnd();
        });
    }

    private void AppendSystem(string text) => AddMessage(new ChatMessage(ChatRole.System) { Text = text });

    private void AppendToMessage(ChatMessage msg, string text)
    {
        PostToUi(() => { msg.Append(text); ChatScroll.ScrollToEnd(); });
    }

    private void SetMessageText(ChatMessage msg, string text)
    {
        PostToUi(() => { msg.Text = text; ChatScroll.ScrollToEnd(); });
    }

    private void SetToolTip(ChatMessage msg, string tooltip)
    {
        PostToUi(() => msg.ToolTip = tooltip);
    }

    // ─────────────────────────── 小工具 ───────────────────────────

    /// <summary>
    /// Windows 命令行引号/反斜杠转义（标准算法）。整段 prompt 作为单个参数传给 node bin.js，
    /// prompt 内部的引号会被这里正确转义；路径里的普通反斜杠在引号内保持字面量。
    /// </summary>
    private static string QuoteArg(string s)
    {
        if (s.Length == 0) return "\"\"";
        if (s.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '"', '\\' }) < 0) return s;

        var sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        foreach (char ch in s)
        {
            if (ch == '\\') { backslashes++; continue; }
            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }
            if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
            sb.Append(ch);
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private static readonly Regex AnsiPattern = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private static string StripAnsi(string s)
    {
        if (s.IndexOf('\x1B') < 0) return s;
        return AnsiPattern.Replace(s, "").Replace("\x1B", "");
    }

    private static string Tail(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= max) return text.Trim();
        return "…" + text.Substring(text.Length - max).Trim();
    }

    private static string BuildTooltip(StringBuilder outBuf, StringBuilder errBuf)
    {
        var sb = new StringBuilder();
        string o = outBuf.ToString().Trim();
        string e = errBuf.ToString().Trim();
        if (o.Length > 0) sb.Append("stdout:\n").Append(o);
        if (e.Length > 0) { if (sb.Length > 0) sb.Append("\n\n"); sb.Append("stderr:\n").Append(e); }
        return sb.Length == 0 ? "" : Tail(sb.ToString(), 8000);
    }

    private static long GetLong(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    private static string GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
}

/// <summary>一条对话消息的角色。</summary>
public enum ChatRole
{
    User,
    Assistant,
    System,
    Thinking,   // 思考消息（reasoning），与完整 DSH 的「思考」块对应
    Tool        // 操作（工具调用 / 工具结果），与完整 DSH 的「操作」块对应
}

/// <summary>
/// 一条对话气泡。全部成员都是属性（WPF 绑定只认属性，不认字段——这正是隔壁面板空白的根因）。
/// Text / ToolTip 在流式更新时经 setter 触发 PropertyChanged，气泡实时重绘。
/// </summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    public ChatRole Role { get; }

    public bool IsUser => Role == ChatRole.User;
    public bool IsSystem => Role == ChatRole.System;
    public bool IsThinking => Role == ChatRole.Thinking;
    public bool IsTool => Role == ChatRole.Tool;

    private string _text = "";
    private string _tooltip = "";

    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; OnPropertyChanged(nameof(Text)); }
    }

    public string ToolTip
    {
        get => _tooltip;
        set { if (_tooltip == value) return; _tooltip = value; OnPropertyChanged(nameof(ToolTip)); }
    }

    public ChatMessage(ChatRole role) => Role = role;

    public void Append(string s) => Text += s;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
