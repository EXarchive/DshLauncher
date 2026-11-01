using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher;

/// <summary>
/// 「会话管理」——清点 / 核对 / 归档 / 隔离 DSH 会话。
///
/// 设计边界（与 tools/session-manager.mjs 的分工）：
///   Node 侧拥有全部事实：zstd 串联帧容器只有 Node 能读（.NET 无内置 zstd，本机离线不装 NuGet），
///   归档状态也是 DSH 自己的 workspace.json。所以本窗口只负责：发命令、渲染 JSON、把关破坏性操作。
///   任何一条 CLI 的输出都是 stdout 上一个 JSON 对象，诊断在 stderr —— stderr 一律照抄到日志，
///   绝不吞掉；退出码非 0 或 ok:false 一律当失败处理并显式提示。
///
/// 破坏性操作的三道闸门：
///   1) 删除只移动到隔离区（CLI 的行为，窗口如实告知）；
///   2) 批量操作先跑 --dry-run，把命中数量写进确认框，用户点“是”才真正执行；
///   3) 永久销毁（会话或隔离批次）必须再点一次确认框，且框里列出将被销毁的内容 ——
///      不可恢复的动作不该只靠一次点击，但也不该逼用户手抄一串批次名。
///
/// 线程模型：每个 CLI 调用都是 async，stderr/stdout 并发读取，UI 线程只做渲染与确认框；
/// 所有回调经 PostToUi 编组，并用 _closed 守卫窗口关闭后不再触碰控件。
/// </summary>
public partial class SessionsWindow : Window
{
    // ─────────────────────────── 状态 ───────────────────────────

    private readonly LauncherConfig _config;

    private readonly StringBuilder _logBuffer = new();
    private readonly object _logLock = new();

    private readonly SemaphoreSlim _cliGate = new(1, 1);

    /// <summary>
    /// 重启 DSH 实例的委托，由 MainWindow 注入（它才持有 ProcessRunnerService）。
    /// 为 null 表示没有重启能力（例如窗口被单独打开），此时横幅只提示、不给按钮。
    /// </summary>
    private readonly Func<Task<bool>>? _restartDsh;

    private List<SessionRow> _allRows = new();
    private readonly ObservableCollection<SessionRow> _rows = new();
    private readonly ObservableCollection<BatchRow> _batches = new();
    private List<EntryRow> _entries = new();

    private bool _closed;
    private int _busy;
    private int _showSeq;
    // ── 排序状态（默认：修改时间，新 → 旧） ──
    /// <summary>
    /// SessionList 的 GridView 列顺序与这两个数组一一对应，所以点击表头时
    /// 只要拿到列下标就能确定排序列（无需给每个列挂事件）。
    /// </summary>
    private static readonly string[] SortKeys =
        { "title", "kind", "workspace", "bytes", "modified", "archived", "health" };

    private static readonly string[] SortHeaders =
        { "标题", "类型", "工作区", "大小", "修改时间", "已归档", "健康" };

    private string _sortKey = "modified";
    private bool _sortAsc;   // false = 降序
    private string _root = "";
    private string? _scriptPath;
    private string? _nodePath;
    private string _lastCommand = "";
    private int _lastTotalsArchived, _lastTotalsDamaged, _lastTotalsUnlisted, _lastStaleRefs;
    /// <summary>重建列表时挂起 SelectionChanged，避免“清空→重选”触发两次记录载入。</summary>
    private bool _suppressSelection;

    /// <summary>单条记录正文的渲染上限：几万字符的助手长文不整段塞进 TextBlock。</summary>
    private const int EntryBodyCap = 6000;
    /// <summary>一次渲染的回合条目上限，防止 tail=0 时把两万条事件铺进可视树。</summary>
    private const int EntryRenderCap = 4000;

    public SessionsWindow(LauncherConfig config, Func<Task<bool>>? restartDsh = null)
    {
        InitializeComponent();

        _config = config ?? new LauncherConfig();
        _restartDsh = restartDsh;

        SessionList.ItemsSource = _rows;
        BatchList.ItemsSource = _batches;
        DetailList.ItemsSource = _entries;

        // 初始值在控件建好之后设置，避免 TextChanged 在 InitializeComponent 期间打到尚未创建的控件。
        TxtSearch.Text = string.Empty;
        TxtTail.Text = "200";
        TxtDetailEmpty.Visibility = Visibility.Visible;

        TxtSearch.TextChanged += TxtSearch_TextChanged;
        // GridView 的列头不是 Button，XAML 里没有 Click 可挂；它冒泡的 Click 事件在这里接。
        SessionList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(SessionList_HeaderClick));
        Loaded += SessionsWindow_Loaded;

        UpdateSortHeaders();
        AppendLog("[系统]", "会话管理器已就绪。", false);

        string? script = ResolveScriptPath();
        if (script == null)
        {
            AppendLog("[错误]", "未找到 tools\\session-manager.mjs（在程序目录向上 6 层内查找）。会话管理不可用。", true);
        }
        else
        {
            AppendLog("[环境]", $"node = {ResolveNodePath()}", false);
            AppendLog("[环境]", $"CLI  = {script}", false);
            AppendLog("[提示]", "首次读取会为每个会话建立元数据索引（一次性，约 20 秒）；之后刷新约 0.1 秒。", false);
        }

        UpdateActionButtons();
        UpdateBusyUi();
    }

    // ─────────────────────────── 窗口控制 ───────────────────────────

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) { WindowState = WindowState.Normal; TxtMaxIcon.Text = "□"; }
        else { WindowState = WindowState.Maximized; TxtMaxIcon.Text = "❐"; }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void SessionsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy > 0)
        {
            var r = MessageBox.Show(this,
                "有 CLI 命令正在执行（逐条解码会话日志可能较慢）。\n关闭窗口不会杀掉它，命令会自己跑完，但界面不再更新。\n\n确定关闭？",
                "命令执行中", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        try { Loaded -= SessionsWindow_Loaded; } catch { }
        try { TxtSearch.TextChanged -= TxtSearch_TextChanged; } catch { }
        // 正在执行的 CLI 不杀：archive/delete/repair 都在写状态，中途杀掉比让它跑完更危险。
        base.OnClosed(e);
    }

    private void SessionsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SessionsWindow_Loaded;
        _ = RefreshAllAsync();
    }

    private async Task RefreshAllAsync()
    {
        // 隔离区先刷：quarantine-list 只需读清单，而 list --all 要逐条解码（本机约 20 秒）
        await RefreshQuarantineAsync();
        await RefreshListAsync();
    }

    // ─────────────────────────── 进程 / 路径解析 ───────────────────────────

    /// <summary>
    /// 定位 tools\session-manager.mjs：从 exe 目录向上逐层找（dist\ 或单文件 exe 都能命中仓库根）。
    /// </summary>
    private string? ResolveScriptPath()
    {
        if (_scriptPath != null) return _scriptPath.Length == 0 ? null : _scriptPath;

        // 内嵌资源优先 —— 单文件发布时程序旁边根本没有 tools\ 目录，
        // 而 CLI 是以 EmbeddedResource 打进 exe、运行时解包到
        // %LOCALAPPDATA%\DshLauncher\assets\<内容哈希>\tools\ 的。
        // 少了这一步，单文件 exe 里的会话管理会直接不可用。
        try
        {
            string embedded = EmbeddedAssets.SessionManager;
            if (File.Exists(embedded)) { _scriptPath = embedded; return embedded; }
        }
        catch (InvalidOperationException)
        {
            // 资源缺失时 EmbeddedAssets 抛出并已在异常里列出可用资源名；
            // 这里不吞掉问题，继续走下面的仓库目录兜底（开发态）。
        }

        // 开发态兜底：从程序目录向上找仓库里的 tools\。
        var dirs = new List<string>();
        try
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && d != null; i++) { dirs.Add(d.FullName); d = d.Parent; }
        }
        catch { }

        foreach (var dir in dirs)
        {
            string candidate = Path.Combine(dir, "tools", "session-manager.mjs");
            if (File.Exists(candidate)) { _scriptPath = candidate; return candidate; }
        }

        _scriptPath = "";
        return null;
    }

    /// <summary>
    /// node 的解析规则与 ProcessRunnerService 完全一致：优先配置里的 NodePath，否则交给 PATH。
    /// 不在这里复制第二份硬编码路径。
    /// </summary>
    private string ResolveNodePath()
    {
        if (_nodePath != null) return _nodePath;

        foreach (string? candidate in new[] { _config.NodePath, LoadConfigNodePath() })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                _nodePath = candidate;
                return _nodePath;
            }
        }

        _nodePath = "node";
        return _nodePath;
    }

    private static string LoadConfigNodePath()
    {
        try { return ConfigService.LoadConfig().NodePath ?? ""; }
        catch { return ""; }
    }

    // ─────────────────────────── CLI 调用 ───────────────────────────

    private sealed class CliResult
    {
        public bool Ok;
        public int ExitCode;
        public string Command = "";
        public string StdOut = "";
        public string StdErr = "";
        public string Error = "";
        public JsonElement? Json;
        public long ElapsedMs;
    }

    /// <summary>
    /// 调用 CLI 并解析 stdout 的 JSON。串行化执行：archive/delete 会写 workspace.json，
    /// 并发跑只会互相踩，所以全部经 _cliGate 排队。UI 线程只 await，不阻塞。
    /// </summary>
    private async Task<CliResult> RunCliAsync(string args, string purpose)
    {
        var result = new CliResult { Command = "session-manager.mjs " + args };

        string? script = ResolveScriptPath();
        if (script == null)
        {
            result.ExitCode = -1;
            result.Error = "未找到 tools\\session-manager.mjs";
            AppendLog("[错误]", $"{purpose} 无法执行：{result.Error}", true);
            return result;
        }

        await _cliGate.WaitAsync().ConfigureAwait(true);
        BeginBusy();
        var sw = Stopwatch.StartNew();
        string node = ResolveNodePath();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = node,
                Arguments = $"\"{script}\" {args}",
                WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            AppendLog("[CLI]", $"{purpose} → node \"{Path.GetFileName(script)}\" {args}", false);
            _lastCommand = "node session-manager.mjs " + args;

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();

            // stdout / stderr 并发读取，避免任一管道写满导致死锁
            Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(outTask, errTask).ConfigureAwait(true);
            await proc.WaitForExitAsync().ConfigureAwait(true);

            result.StdOut = outTask.Result ?? "";
            result.StdErr = errTask.Result ?? "";
            result.ExitCode = proc.ExitCode;
        }
        catch (Exception ex)
        {
            result.ExitCode = -1;
            result.Error = $"{node} 启动/读取失败: {ex.Message}";
            AppendLog("[错误]", $"{purpose} 调用异常（node={node}）: {ex.Message}", true);
        }
        finally
        {
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            EndBusy();
            _cliGate.Release();
        }

        // ── stderr：原样照抄，永不吞掉 ──
        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            foreach (string line in SplitLines(result.StdErr))
            {
                AppendLog("[stderr]", line, true);
            }
        }

        // ── stdout：解析成 JSON ──
        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            try
            {
                using var doc = JsonDocument.Parse(result.StdOut);
                result.Json = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                result.Error = $"stdout 不是合法 JSON（{ex.Message}）";
                AppendLog("[错误]", result.Error, true);
                AppendLog("[stdout]", FirstLines(result.StdOut, 20), true);
            }
        }

        bool jsonOk = false;
        if (result.Json.HasValue && result.Json.Value.ValueKind == JsonValueKind.Object)
        {
            jsonOk = GetBool(result.Json.Value, "ok");
            if (!jsonOk)
            {
                string msg = GetString(result.Json.Value, "error") ?? "CLI 返回 ok:false";
                result.Error = msg;
                AppendLog("[错误]", $"{purpose} 失败: {msg}", true);
            }
        }

        if (result.ExitCode != 0 && result.Error.Length == 0)
        {
            result.Error = $"退出码 {result.ExitCode}";
            AppendLog("[错误]", $"{purpose} 退出码 {result.ExitCode}，且 stdout 未给出可读的 ok:true JSON。", true);
        }

        result.Ok = result.ExitCode == 0 && jsonOk && result.Error.Length == 0;

        AppendLog("[CLI]", $"{purpose} 结束：退出码 {result.ExitCode}，用时 {result.ElapsedMs} ms，stdout {result.StdOut.Length} 字符",
                  !result.Ok);

        UpdateFooterRight(result.Command);
        return result;
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n')
               .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string FirstLines(string text, int max)
    {
        var lines = SplitLines(text).Take(max).ToList();
        return lines.Count == 0 ? "" : string.Join("\n", lines);
    }

    // ─────────────────────────── 会话列表 ───────────────────────────

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy > 0) { AppendLog("[提示]", "已有命令在执行，请稍候。", true); return; }
        await RefreshListAsync();
    }

    private void FilterToggle_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    /// <summary>
    /// 一次 list --all 拿回全部会话，三个过滤器在本地按 CLI 的同一套规则判定。
    /// 理由：CLI 的 listSessions() 无论带不带 flag 都会解码全部会话（本机约 20 秒），
    /// 每次点过滤器都重跑一遍 20 秒是不可接受的；而过滤规则本身只有四行，且 kind/archived
    /// 都是 CLI 自己给的值，本地复现不会与它产生分歧。
    /// </summary>
    private async Task RefreshListAsync()
    {
        var res = await RunCliAsync("list --all", "列出全部会话");
        if (!res.Ok || !res.Json.HasValue)
        {
            PostToUi(() => TxtStatus.Text = "列表刷新失败：" + (res.Error.Length > 0 ? res.Error : "未知错误"));
            return;
        }

        JsonElement root = res.Json.Value;

        var loaded = new List<SessionRow>();
        int archived = 0, damaged = 0, unlisted = 0;
        if (root.TryGetProperty("sessions", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var row = SessionRow.From(s);
                if (row.Id.Length == 0) continue;
                loaded.Add(row);
                if (row.Archived) archived++;
                if (row.Damaged) damaged++;
                if (!row.ListedInWorkspace) unlisted++;
            }
        }

        _root = GetString(root, "root") ?? "";
        _allRows = loaded;
        long totalAll = root.TryGetProperty("totals", out var t) ? GetLong(t, "all") : loaded.Count;
        _lastTotalsArchived = archived;
        _lastTotalsDamaged = damaged;
        _lastTotalsUnlisted = unlisted;
        _lastStaleRefs = 0;
        if (root.TryGetProperty("staleArchiveRefs", out var stale) && stale.ValueKind == JsonValueKind.Array)
        {
            _lastStaleRefs = stale.GetArrayLength();
        }

        ApplyFilter(reloadTranscript: true);

        AppendLog("[结果]",
            $"list --all：CLI 报告总计 {totalAll} 条（本次载入 {loaded.Count}；归档 {archived}，损坏 {damaged}，未分组 {unlisted}），本地显示 {_rows.Count} 条。",
            false);

        if (_lastStaleRefs > 0)
        {
            AppendLog("[提示]",
                $"DSH 归档表里有 {_lastStaleRefs} 个已不存在的会话 id（staleArchiveRefs）：这会让 DSH 看起来缺会话，但那不是日志损坏。",
                false);
        }
    }

    // ─────────────────────────── 排序 ───────────────────────────

    /// <summary>
    /// GridView 列头点击：同一列再点一次反向，换列则用该列的默认方向。
    /// 文本列默认升序（A→Z 符合直觉），大小/时间默认降序（先看最新、最大的）。
    /// </summary>
    private void SessionList_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column == null) return;
        int index = ((GridView)SessionList.View).Columns.IndexOf(header.Column);
        if (index < 0 || index >= SortKeys.Length) return;

        string key = SortKeys[index];
        if (key == _sortKey)
        {
            _sortAsc = !_sortAsc;
        }
        else
        {
            _sortKey = key;
            _sortAsc = key is "title" or "kind" or "workspace" or "health";
        }

        UpdateSortHeaders();
        ApplyFilter();
    }

    /// <summary>把当前排序列与方向写回列头和提示行，让用户随时看得见。</summary>
    private void UpdateSortHeaders()
    {
        int active = Array.IndexOf(SortKeys, _sortKey);
        var columns = ((GridView)SessionList.View).Columns;
        for (int i = 0; i < columns.Count && i < SortHeaders.Length; i++)
        {
            columns[i].Header = i == active
                ? SortHeaders[i] + (_sortAsc ? " ▲" : " ▼")
                : SortHeaders[i];
        }
        if (TxtSortHint != null)
        {
            TxtSortHint.Text = active >= 0
                ? $"· 按「{SortHeaders[active]}」{(_sortAsc ? "升序" : "降序")}"
                : "· 点击列标题排序";
        }
    }

    /// <summary>
    /// 排序比较器。键值相同时用修改时间兜底，否则同批会话在每次刷新后可能换位，
    /// 看上去像列表自己在跳动。
    /// </summary>
    private int CompareRows(SessionRow a, SessionRow b)
    {
        int r = _sortKey switch
        {
            "title" => string.Compare(a.TitleDisplay, b.TitleDisplay, StringComparison.CurrentCultureIgnoreCase),
            "kind" => string.Compare(a.KindLabel, b.KindLabel, StringComparison.CurrentCultureIgnoreCase),
            "workspace" => string.Compare(a.WorkspaceShort, b.WorkspaceShort, StringComparison.CurrentCultureIgnoreCase),
            "bytes" => a.Bytes.CompareTo(b.Bytes),
            "modified" => string.Compare(a.ModifiedIso ?? "", b.ModifiedIso ?? "", StringComparison.Ordinal),
            "archived" => a.Archived.CompareTo(b.Archived),
            "health" => string.Compare(a.HealthText, b.HealthText, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };
        if (!_sortAsc) r = -r;
        if (r == 0 && _sortKey != "modified")
        {
            r = string.Compare(b.ModifiedIso ?? "", a.ModifiedIso ?? "", StringComparison.Ordinal);
        }
        return r;
    }

    /// <summary>本地过滤：复刻 CLI 的 include 规则 + 标题/cwd/id 子串搜索。</summary>
    private void ApplyFilter(bool reloadTranscript = false)
    {
        bool all = TglAll.IsChecked == true;
        bool sub = TglSubagent.IsChecked == true || all;
        bool arc = TglArchived.IsChecked == true || all;
        bool imp = all;
        bool sys = all;
        string q = (TxtSearch.Text ?? "").Trim();

        var next = new List<SessionRow>();
        foreach (var r in _allRows)
        {
            if (r.Kind == "subagent" && !sub) continue;
            if (r.Kind == "imported" && !imp) continue;
            if (r.Kind == "system" && !sys) continue;
            if (r.Archived && !arc) continue;
            if (q.Length > 0 && !r.Matches(q)) continue;
            next.Add(r);
        }

        next.Sort(CompareRows);
        RebuildRows(next, reloadTranscript);
    }

    /// <summary>
    /// 重建可见行：保持选中项，并且只在需要时重新拉取逐回合记录
    /// （“清空 → 重选”如果任由 SelectionChanged 跑，会白白多打一次 show）。
    /// </summary>
    private void RebuildRows(List<SessionRow> next, bool reloadTranscript)
    {
        string keepId = (SessionList.SelectedItem as SessionRow)?.Id ?? "";

        _suppressSelection = true;
        try
        {
            _rows.Clear();
            foreach (var r in next) _rows.Add(r);

            SessionRow? again = null;
            if (keepId.Length > 0)
            {
                again = next.FirstOrDefault(r => r.Id == keepId);
                if (again != null)
                {
                    SessionList.SelectedItem = again;
                    SessionList.ScrollIntoView(again);
                }
            }

            if (again == null && keepId.Length > 0)
            {
                // 之前选中的会话已被过滤掉：记录面板跟着清空，避免标题与内容对不上
                _showSeq++;
                _entries = new List<EntryRow>();
                DetailList.ItemsSource = _entries;
                TxtDetailTitle.Text = "未选择会话";
                TxtDetailEmpty.Text = "选择左侧任意会话，此处按回合顺序展示 用户 / 助手 / 工具 / 计划 记录";
                TxtDetailEmpty.Visibility = Visibility.Visible;
            }
            else if (again != null && reloadTranscript)
            {
                _ = LoadTranscriptAsync(again);
            }

            TxtListHint.Text = _allRows.Count == 0
                ? ""
                : $"共 {_allRows.Count} 条 · 归档 {_lastTotalsArchived} · 损坏 {_lastTotalsDamaged} · 未分组 {_lastTotalsUnlisted}";
            TxtSummary.Text = string.IsNullOrEmpty(_root) ? "会话清点 · 逐回合核对 · 隔离可回滚" : _root;
            TxtStatus.Text = _rows.Count == _allRows.Count
                ? $"显示全部 {_rows.Count} 条"
                : $"显示 {_rows.Count} / {_allRows.Count} 条（过滤器或搜索命中）";

            UpdateSelectionInfo();
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private List<SessionRow> SelectedSessions()
    {
        var list = new List<SessionRow>();
        if (SessionList.SelectedItems == null) return list;
        foreach (object item in SessionList.SelectedItems)
        {
            if (item is SessionRow r) list.Add(r);
        }
        return list;
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        var sel = SelectedSessions();
        UpdateSelectionInfo();

        if (sel.Count != 1)
        {
            if (sel.Count == 0)
            {
                _showSeq++;
                _entries = new List<EntryRow>();
                PostToUi(() =>
                {
                    DetailList.ItemsSource = _entries;
                    TxtDetailTitle.Text = "未选择会话";
                    TxtDetailEmpty.Visibility = Visibility.Visible;
                });
            }
            else
            {
                _showSeq++;
                PostToUi(() =>
                {
                    TxtDetailTitle.Text = $"已选 {sel.Count} 条（逐回合记录只针对单条会话）";
                    TxtDetailEmpty.Text = "多选时不显示记录：请在左侧只选一条会话，或用下方批量按钮对多选执行归档/删除/修复。";
                    TxtDetailEmpty.Visibility = Visibility.Visible;
                });
            }
            return;
        }

        _ = LoadTranscriptAsync(sel[0]);
    }

    private void BtnReloadTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionRow row) _ = LoadTranscriptAsync(row);
    }

    private void TranscriptOption_Changed(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionRow row) _ = LoadTranscriptAsync(row);
    }

    /// <summary>
    /// show --id … —— 逐回合记录。用 _showSeq 丢弃过期结果：用户快速切换会话时，
    /// 先发的慢命令回来也不许覆盖后发的结果。
    /// </summary>
    private async Task LoadTranscriptAsync(SessionRow row)
    {
        int seq = ++_showSeq;
        int tail = ParseTail();
        bool userOnly = ChkUserOnly.IsChecked == true;

        string args = $"show --id {Q(row.Id)}"
                    + (tail > 0 ? $" --tail {tail}" : "")
                    + (userOnly ? " --user-only" : "");

        PostToUi(() =>
        {
            TxtDetailTitle.Text = $"载入中… {row.TitleDisplay}";
            TxtDetailEmpty.Visibility = Visibility.Collapsed;
        });

        var res = await RunCliAsync(args, $"读取会话记录 {row.Id}");
        if (_closed || seq != _showSeq) return;

        if (!res.Ok || !res.Json.HasValue)
        {
            _entries = new List<EntryRow>();
            PostToUi(() =>
            {
                DetailList.ItemsSource = _entries;
                TxtDetailTitle.Text = "载入失败：" + row.Id;
                TxtDetailEmpty.Text = "载入失败：" + (res.Error.Length > 0 ? res.Error : "CLI 未返回有效 JSON") + "\n\n详见下方 CLI 日志。";
                TxtDetailEmpty.Visibility = Visibility.Visible;
            });
            return;
        }

        JsonElement root = res.Json.Value;
        var built = new List<EntryRow>();
        int turnNo = 0;
        int skipped = 0;
        int truncated = 0;

        if (root.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in turns.EnumerateArray())
            {
                turnNo++;
                string turnLabel = GetString(turn, "turn") ?? turnNo.ToString(CultureInfo.InvariantCulture);
                if (!turn.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    if (built.Count >= EntryRenderCap) { skipped++; continue; }

                    var er = EntryRow.From(turnLabel, entry);
                    if (er.Truncated) truncated++;
                    built.Add(er);
                }
            }
        }

        if (skipped > 0)
        {
            built.Add(new EntryRow
            {
                Role = "note",
                RoleLabel = "截断",
                RoleBrush = BrWarn,
                Meta = $"已渲染上限 {EntryRenderCap} 条",
                Body = $"其余 {skipped} 条未渲染。请把「回合数」收窄（例如 50）后重载，或用 --user-only 只看你自己的回合。",
                RowBackground = BrBgWarn,
                RowBorder = BrWarn
            });
        }

        _entries = built;
        string title = GetString(root, "session", out var sessionEl) ? (GetString(sessionEl, "title") ?? "(无标题)") : "(无标题)";
        long expectTurns = GetLong(root, "turnCount");
        int inboxTurns = 0, delegatedTurns = 0, systemTurns = 0;
        if (root.TryGetProperty("subagentSpliced", out var spliced) && spliced.ValueKind == JsonValueKind.Object)
        {
            inboxTurns = (int)GetLong(spliced, "inboxTurns");
            delegatedTurns = (int)GetLong(spliced, "delegatedTurns");
            systemTurns = (int)GetLong(spliced, "systemTurns");
        }

        string subDesc = $"回合 {built.Count} 条记录"
            + (userOnly ? "（--user-only：已剔除系统注入与委派任务回合）" : "")
            + $"，inbox 注入回合 {inboxTurns}（委派 {delegatedTurns} / 系统 {systemTurns}）";

        PostToUi(() =>
        {
            DetailList.ItemsSource = _entries;
            TxtDetailTitle.Text = $"{title}｜{subDesc}";
            TxtFooterRight.Text = _lastCommand;
            if (_entries.Count == 0)
            {
                TxtDetailEmpty.Text = userOnly
                    ? "该会话没有属于你自己的回合（其余都是系统注入或委派任务）。取消「只看我的输入」可查看全部。"
                    : "该会话没有任何可渲染的回合记录。";
                TxtDetailEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                TxtDetailEmpty.Visibility = Visibility.Collapsed;
            }
        });

        AppendLog("[结果]", $"show {row.Id}：{built.Count} 条记录（CLI 报告回合 {expectTurns}）"
            + (truncated > 0 ? $"，{truncated} 条正文被截断显示" : ""), false);
    }

    private int ParseTail()
    {
        if (int.TryParse((TxtTail.Text ?? "").Trim(), out int n) && n > 0) return n;
        return 0; // 0 = 全部
    }

    // ─────────────────────────── 单条会话动作 ───────────────────────────

    private void BtnArchive_Click(object sender, RoutedEventArgs e) => _ = RunSelectedActionAsync("archive");
    private void BtnUnarchive_Click(object sender, RoutedEventArgs e) => _ = RunSelectedActionAsync("unarchive");
    private void BtnDelete_Click(object sender, RoutedEventArgs e) => _ = RunSelectedActionAsync("delete");
    private void BtnRepair_Click(object sender, RoutedEventArgs e) => _ = RunSelectedActionAsync("repair");
    private void BtnPurge_Click(object sender, RoutedEventArgs e) => _ = RunSelectedActionAsync("purge");

    private async Task RunSelectedActionAsync(string action)
    {
        if (_busy > 0) { AppendLog("[提示]", "已有命令在执行，请稍候。", true); return; }

        var targets = SelectedSessions();
        if (targets.Count == 0) { AppendLog("[提示]", "未选择会话。", true); return; }

        if (action == "purge")
        {
            var answer = MessageBox.Show(this,
                $"将【永久删除】{targets.Count} 个会话，同时清掉它们的隔离区副本与 DSH 归档引用。\n"
                + "删除后无法恢复。\n\n" + PreviewTargets(targets),
                "确认永久删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) { AppendLog("[操作]", "已取消永久删除。", false); return; }
        }
        else if (action is "delete" or "repair")
        {
            string extra = action == "delete"
                ? "\n（delete 只是移动到隔离区，之后可以用「恢复所选批次」搬回来）"
                : "\n（repair 会先备份原文件，再丢弃不可读的尾部帧；原始文件不会消失）";
            var answer = MessageBox.Show(this,
                $"对 {targets.Count} 个会话执行「{ActionLabel(action)}」？{extra}\n\n{PreviewTargets(targets)}",
                "确认操作", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) { AppendLog("[操作]", "已取消。", false); return; }
        }

        string args = action + " " + string.Join(' ', targets.Select(t => "--id " + Q(t.Id)));
        var res = await RunCliAsync(args, ActionLabel(action));
        if (res.Ok && res.Json.HasValue)
        {
            AppendLog("[结果]", $"{ActionLabel(action)} 完成：{Summarize(action, res.Json.Value)}", false);
        }

        await RefreshListAsync();
        // purge destroys quarantine copies too, so the 隔离区 tab is stale after it
        // exactly as it is after delete.
        if (action is "delete" or "purge") await RefreshQuarantineAsync();
    }

    // ─────────────────────────── 批量动作（--select） ───────────────────────────

    /// <summary>
    /// 批量的唯一入口。两个下拉框取代了原来的「4 个动作 ToggleButton × 3 个选择器 Button」：
    /// 那是 11 个控件编码两个互相独立的维度，必须并排读才知道会执行什么。
    /// </summary>
    private void BtnBulkRun_Click(object sender, RoutedEventArgs e)
        => _ = RunBulkAsync(ComboTag(CmbBulkSelector, "subagent"), ComboTag(CmbBulkAction, "archive"));

    /// <summary>选中下拉项的 Tag，即传给 CLI 的值。</summary>
    private static string ComboTag(ComboBox box, string fallback)
        => (box.SelectedItem as ComboBoxItem)?.Tag as string is { Length: > 0 } tag ? tag : fallback;

    /// <summary>
    /// 批量：先 --dry-run 拿到命中数量，把数量写进确认框，再执行同一条命令（不带 --dry-run）。
    /// 绝不盲删 —— 这是窗口对 CLI 的唯一硬性约束。
    /// </summary>
    private async Task RunBulkAsync(string selector, string action)
    {
        if (_busy > 0) { AppendLog("[提示]", "已有命令在执行，请稍候。", true); return; }

        var dry = await RunCliAsync($"{action} --select {selector} --dry-run", $"{ActionLabel(action)} 预演（--dry-run）");
        if (!dry.Ok || !dry.Json.HasValue)
        {
            AppendLog("[错误]", "预演失败，未执行任何操作。", true);
            return;
        }

        int count = CountTargets(dry.Json.Value);
        AppendLog("[预演]", $"{action} --select {selector} --dry-run → 命中 {count} 个会话。", false);
        if (count == 0)
        {
            AppendLog("[提示]", $"「{SelectorLabel(selector)}」命中 0 个，无需执行。", false);
            return;
        }

        string extra = action == "delete"
            ? "\n（delete 只是移动到隔离区，之后可恢复）"
            : action == "repair"
                ? "\n（repair 会先备份原文件再截断）"
                : "\n（归档只改 DSH 的 archivedSessionIds，随时可取消归档）";

        var answer = MessageBox.Show(this,
            $"批量「{ActionLabel(action)}」：--select {selector} 预演命中 {count} 个「{SelectorLabel(selector)}」。{extra}\n\n预演已完成，确认执行？",
            "确认批量操作", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) { AppendLog("[操作]", "已取消批量操作。", false); return; }

        var res = await RunCliAsync($"{action} --select {selector}", $"{ActionLabel(action)}（{SelectorLabel(selector)}）");
        if (res.Ok && res.Json.HasValue)
        {
            AppendLog("[结果]", $"{ActionLabel(action)} × {SelectorLabel(selector)} 完成：{Summarize(action, res.Json.Value)}", false);
        }

        await RefreshListAsync();
        // purge destroys quarantine copies too, so the 隔离区 tab is stale after it
        // exactly as it is after delete.
        if (action is "delete" or "purge") await RefreshQuarantineAsync();
    }

    /// <summary>dry-run 的命中数量：优先用 ids 数组，其次用各类 would* 计数。</summary>
    private static int CountTargets(JsonElement json)
    {
        if (json.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array) return ids.GetArrayLength();
        foreach (string key in new[] { "wouldChange", "wouldDelete", "wouldRepair", "count" })
        {
            if (json.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        }
        return 0;
    }

    /// <summary>
    /// 归档类改动为什么需要重启 DSH —— 三条源码证据：
    ///   1) dsh-storage-json 只在打开存储单元时 readFile 一次，注释明写
    ///      "The in-memory state is authoritative"，全文无 watch/stat/reload；
    ///   2) DSH 只暴露 Remote("archiveSession")，且为「只增不改」；
    ///   3) 全树搜索 unarchive/removeArchived 无命中，界面也只有「归档会话」。
    /// 即：**取消归档是 DSH 本身不具备的能力**，我们只能改文件，改完必须重启 DSH 才生效。
    /// 把这件事显式说出来，否则用户会以为按钮失灵。
    /// </summary>
    private void LogDshRestartHint(JsonElement json)
    {
        if (!GetBool(json, "requiresDshRestart")) return;
        string note = GetString(json, "note") ?? "归档状态需重启 DSH 才会在其界面生效。";
        AppendLog("[重要]", note, true);
    }

    /// <summary>把每个动作的 JSON 结果压成一行可读结论。</summary>
    private string Summarize(string action, JsonElement json)
    {
        switch (action)
        {
            case "archive":
            case "unarchive":
            {
                int archived = 0, unarchived = 0, unchanged = 0;
                if (json.TryGetProperty("changed", out var ch) && ch.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in ch.EnumerateArray())
                    {
                        string a = GetString(c, "action") ?? "";
                        if (a == "archived") archived++;
                        else if (a == "unarchived") unarchived++;
                        else unchanged++;
                    }
                }
                long total = GetLong(json, "archivedCount");
                string head = $"变更 {archived + unarchived} 条（新归档 {archived}，新取消 {unarchived}），无变化 {unchanged} 条；DSH 归档表现有 {total} 条";
                return GetBool(json, "requiresDshRestart")
                    ? head + "。⚠ 需重启 DSH 才在其界面生效（原因见 CLI 日志）"
                    : head;
            }
            case "delete":
            {
                int removed = 0, skipped = 0;
                if (json.TryGetProperty("removed", out var rm) && rm.ValueKind == JsonValueKind.Array) removed = rm.GetArrayLength();
                if (json.TryGetProperty("skipped", out var sk) && sk.ValueKind == JsonValueKind.Array)
                {
                    skipped = sk.GetArrayLength();
                    foreach (var s in sk.EnumerateArray())
                    {
                        AppendLog("[跳过]", $"{GetString(s, "id")}: {GetString(s, "error")}", true);
                    }
                }
                return $"移入隔离区 {removed} 条（批量 {(GetString(json, "batch") ?? "-")}），跳过 {skipped} 条";
            }
            case "purge":
            {
                int removed = 0, skipped = 0;
                if (json.TryGetProperty("purged", out var pg) && pg.ValueKind == JsonValueKind.Array) removed = pg.GetArrayLength();
                if (json.TryGetProperty("skipped", out var sk2) && sk2.ValueKind == JsonValueKind.Array)
                {
                    skipped = sk2.GetArrayLength();
                    foreach (var s in sk2.EnumerateArray())
                    {
                        AppendLog("[跳过]", $"{GetString(s, "id")}: {GetString(s, "error")}", true);
                    }
                }
                long bytes = GetLong(json, "purgedBytes");
                int quarantine = 0;
                if (json.TryGetProperty("quarantineCopiesDestroyed", out var qc) && qc.ValueKind == JsonValueKind.Array) quarantine = qc.GetArrayLength();
                long refs = GetLong(json, "archiveRefsRemoved");
                return $"永久删除 {removed} 条，释放 {FormatBytes(bytes)}；"
                     + $"同时清掉隔离区副本 {quarantine} 个、归档引用 {refs} 个；跳过 {skipped} 条";
            }
            case "repair":
            {
                int repaired = 0, failed = 0;
                if (json.TryGetProperty("results", out var rs) && rs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in rs.EnumerateArray())
                    {
                        if (GetBool(r, "repaired")) repaired++;
                        else failed++;
                    }
                }
                return $"修复成功 {repaired} 条，未修复 {failed} 条（未修复的原因见 CLI 日志）";
            }
            default:
                return "已完成（详见 stdout 与 CLI 日志）";
        }
    }

    private static string PreviewTargets(List<SessionRow> targets)
    {
        var lines = targets.Take(6).Select(t => "· " + t.TitleDisplay + "  [" + t.Id + "]").ToList();
        if (targets.Count > lines.Count) lines.Add($"· …其余 {targets.Count - lines.Count} 条");
        return string.Join("\n", lines);
    }

    // ─────────────────────────── 体检扫描 ───────────────────────────

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy > 0) { AppendLog("[提示]", "已有命令在执行，请稍候。", true); return; }

        PostToUi(() => { TxtStatus.Text = "正在体检扫描（逐条校验帧边界）…"; });
        var res = await RunCliAsync("scan", "体检扫描（帧边界与可解码性）");
        if (!res.Ok || !res.Json.HasValue) return;

        JsonElement root = res.Json.Value;
        var bad = new Dictionary<string, (string health, string? reason, double lost)>(StringComparer.Ordinal);
        if (root.TryGetProperty("damaged", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in arr.EnumerateArray())
            {
                string id = GetString(d, "id") ?? "";
                if (id.Length == 0) continue;
                bad[id] = (GetString(d, "health") ?? "damaged",
                           GetString(d, "reason") ?? GetString(d, "error"),
                           GetDouble(d, "lostPercent"));
            }
        }

        int flagged = 0;
        foreach (var row in _allRows)
        {
            if (bad.TryGetValue(row.Id, out var v))
            {
                string? note = v.lost > 0
                    ? $"丢失约 {v.lost.ToString("0.0", CultureInfo.InvariantCulture)}%：{v.reason}"
                    : v.reason;
                row.SetHealth(v.health, note, v.reason);
                flagged++;
            }
            else
            {
                row.SetHealth("ok", null, null);
            }
        }

        long scanned = 0, okCount = 0, damaged = 0, unreadable = 0, missing = 0;
        if (root.TryGetProperty("totals", out var totals) && totals.ValueKind == JsonValueKind.Object)
        {
            scanned = GetLong(totals, "scanned");
            okCount = GetLong(totals, "ok");
            damaged = GetLong(totals, "damaged");
            unreadable = GetLong(totals, "unreadable");
            missing = GetLong(totals, "missing");
        }

        AppendLog("[结果]", $"scan：扫描 {scanned} 条，正常 {okCount}，损坏 {damaged}，不可读 {unreadable}，缺日志 {missing}；已就地刷新列表中的健康列（{flagged} 条异常）。", damaged + unreadable > 0);
        foreach (var kv in bad)
        {
            AppendLog(kv.Value.health == "missing-log" ? "[缺日志]" : "[损坏]", $"{kv.Key}：{kv.Value.reason ?? kv.Value.health}", true);
        }
    }

    // ─────────────────────────── 隔离区 ───────────────────────────

    private async void BtnQuarantineRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy > 0) { AppendLog("[提示]", "已有命令在执行，请稍候。", true); return; }
        await RefreshQuarantineAsync();
    }

    private void BatchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BatchList.SelectedItem is BatchRow row)
        {
            TxtQuarantineHint.Text = $"已选批次：{row.Batch}\n{row.ItemsText} 个条目 · {row.SizeText}\n永久销毁需要输入该批次名确认。";
        }
        else
        {
            TxtQuarantineHint.Text = "删除只移动到隔离区；永久销毁是第二步，需要二次确认。";
        }
        UpdateBusyUi();
    }

    private async Task RefreshQuarantineAsync()
    {
        var res = await RunCliAsync("quarantine-list", "列出隔离批次");
        if (!res.Ok || !res.Json.HasValue) return;

        JsonElement root = res.Json.Value;
        var loaded = new List<BatchRow>();
        if (root.TryGetProperty("batches", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in arr.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object) continue;
                loaded.Add(new BatchRow
                {
                    Batch = GetString(b, "batch") ?? "",
                    CreatedAtIso = GetString(b, "createdAt") ?? "",
                    ManifestPath = GetString(b, "manifest") ?? "",
                    Items = GetLong(b, "items"),
                    Bytes = GetLong(b, "bytes")
                });
            }
        }

        string keep = (BatchList.SelectedItem as BatchRow)?.Batch ?? "";
        PostToUi(() =>
        {
            _batches.Clear();
            foreach (var b in loaded) _batches.Add(b);
            if (keep.Length > 0)
            {
                var again = _batches.FirstOrDefault(x => x.Batch == keep);
                if (again != null) BatchList.SelectedItem = again;
            }
            TxtQuarantineHint.Text = _batches.Count == 0
                ? "隔离区为空。删除会话时会在这里生成一个批次，可恢复或永久销毁。"
                : "删除只移动到隔离区；永久销毁是第二步，需要二次确认。";
        });

        AppendLog("[结果]", $"quarantine-list：{loaded.Count} 个批次，共 {loaded.Sum(x => x.Items)} 个条目、{FormatBytes(loaded.Sum(x => x.Bytes))}。", false);
    }

    private async void BtnRestoreBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy > 0) { AppendLog("[提示]", "已有命令在执行，请稍候。", true); return; }
        if (BatchList.SelectedItem is not BatchRow row)
        {
            AppendLog("[提示]", "请先选择一个隔离批次。", true);
            return;
        }

        var answer = MessageBox.Show(this,
            $"恢复批次「{row.Batch}」的 {row.ItemsText} 个会话到原路径？\n\n若原路径已被占用，CLI 会拒绝覆盖并如实报告（不会静默丢数据）。",
            "确认恢复", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) { AppendLog("[操作]", "已取消恢复。", false); return; }

        var res = await RunCliAsync($"restore --manifest {Q(row.ManifestPath)}", "恢复隔离批次");
        if (res.Ok && res.Json.HasValue)
        {
            JsonElement root = res.Json.Value;
            var restored = new List<string>();
            var failed = new List<(string id, string err)>();
            if (root.TryGetProperty("restored", out var r1) && r1.ValueKind == JsonValueKind.Array)
                foreach (var x in r1.EnumerateArray()) restored.Add(x.GetString() ?? "");
            if (root.TryGetProperty("failed", out var r2) && r2.ValueKind == JsonValueKind.Array)
                foreach (var x in r2.EnumerateArray())
                    failed.Add((GetString(x, "id") ?? "?", GetString(x, "error") ?? ""));

            AppendLog("[结果]", $"restore：成功 {restored.Count} 条，失败 {failed.Count} 条。", failed.Count > 0);
            foreach (var f in failed) AppendLog("[失败]", $"{f.id}：{f.err}", true);
        }

        await RefreshQuarantineAsync();
        await RefreshListAsync();
    }

    private async void BtnPurgeBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy > 0) { AppendLog("[提示]", "已有命令在执行，请稍候。", true); return; }
        if (BatchList.SelectedItem is not BatchRow row)
        {
            AppendLog("[提示]", "请先选择一个隔离批次。", true);
            return;
        }

        var info = ReadManifest(row.ManifestPath);
        if (info.Error.Length > 0)
        {
            AppendLog("[错误]", $"无法读取批次清单 {row.ManifestPath}：{info.Error}", true);
            return;
        }
        if (info.Items.Count == 0)
        {
            AppendLog("[提示]", "该批次清单里没有条目，无需销毁。", false);
            return;
        }

        var mine = info.Items.Select(i => i.Kind).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        // CLI 的 purge 只按 --kind 过滤，无法只销毁某一个批次；如果别的批次也有同样的 kind，
        // 用它就会连带销毁不属于本次操作的副本。这种情况下改为按清单逐项删除。
        var others = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in _batches)
        {
            if (b.Batch == row.Batch) continue;
            var other = ReadManifest(b.ManifestPath);
            foreach (var it in other.Items) if (it.Kind.Length > 0) others.Add(it.Kind);
        }
        bool kindExclusive = mine.Count > 0 && !mine.Any(k => others.Contains(k));

        string mechanism = kindExclusive
            ? $"机制：purge --kind {string.Join(", ", mine)}（这些类型只存在于本批次）"
            : "机制：按清单逐项销毁（本批次类型与其他批次重叠，若用 purge --kind 会误删别的批次）";

        var purgeAnswer = MessageBox.Show(this,
            $"即将永久销毁隔离批次「{row.Batch}」中的 {info.Items.Count} 个副本（{row.SizeText}）。\n"
            + "此操作不可恢复：隔离副本会被真正删除（原会话在删除时就已经不在原位了）。\n"
            + $"批次内类型：{(mine.Count == 0 ? "未知" : string.Join(", ", mine))}\n{mechanism}",
            "确认永久销毁", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (purgeAnswer != MessageBoxResult.Yes)
        {
            AppendLog("[操作]", "永久销毁已取消。", false);
            return;
        }

        if (kindExclusive)
        {
            int purged = 0;
            foreach (string kind in mine)
            {
                var res = await RunCliAsync($"purge --kind {Q(kind)}", $"永久销毁 --kind {kind}");
                if (res.Ok && res.Json.HasValue) purged += (int)GetLong(res.Json.Value, "purgedCount");
            }
            AppendLog("[结果]",
                $"purge 完成：销毁 {purged} 个副本。注意：CLI 只删除副本文件，批次目录与 manifest.json 保留，所以该批次仍会出现在列表里。",
                purged < info.Items.Count);
        }
        else
        {
            string batchDir = Path.GetDirectoryName(row.ManifestPath) ?? "";
            var failures = new List<(string id, string err)>();
            int deleted = 0;
            await Task.Run(() =>
            {
                foreach (var it in info.Items)
                {
                    if (it.To.Length == 0) { failures.Add((it.Id, "清单缺少 to 路径")); continue; }
                    try
                    {
                        if (Directory.Exists(it.To)) { Directory.Delete(it.To, true); deleted++; }
                        else failures.Add((it.Id, "副本已不存在"));
                    }
                    catch (Exception ex) { failures.Add((it.Id, ex.Message)); }
                }
                try
                {
                    if (batchDir.Length > 0 && Directory.Exists(batchDir)) Directory.Delete(batchDir, true);
                }
                catch (Exception ex) { failures.Add((batchDir, ex.Message)); }
            }).ConfigureAwait(true);

            AppendLog("[结果]", $"按清单销毁完成：删除 {deleted} / {info.Items.Count} 个副本，批次目录已移除。", deleted < info.Items.Count);
            foreach (var f in failures) AppendLog("[失败]", $"{f.id}：{f.err}", true);
        }

        await RefreshQuarantineAsync();
        await RefreshListAsync();
    }

    private sealed class ManifestItem
    {
        public string Id = "";
        public string Kind = "";
        public string To = "";
    }

    private sealed class ManifestInfo
    {
        public List<ManifestItem> Items = new();
        public string Error = "";
    }

    /// <summary>只读解析隔离批次清单（不修改任何文件）。</summary>
    private static ManifestInfo ReadManifest(string path)
    {
        var info = new ManifestInfo();
        try
        {
            if (!File.Exists(path)) { info.Error = "清单文件不存在"; return info; }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    info.Items.Add(new ManifestItem
                    {
                        Id = GetString(it, "id") ?? "",
                        Kind = GetString(it, "kind") ?? "",
                        To = GetString(it, "to") ?? ""
                    });
                }
            }
        }
        catch (Exception ex) { info.Error = ex.Message; }
        return info;
    }

    // ─────────────────────────── 日志 / 状态 ───────────────────────────

    /// <summary>UI 线程编组 + 窗口已关闭守卫。</summary>
    private void PostToUi(Action action)
    {
        if (_closed) return;
        try
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.Invoke(action);
        }
        catch (TaskCanceledException) { }
        // ObjectDisposedException 派生自 InvalidOperationException，一并由这里兜住（关窗时可能随手抛）
        catch (InvalidOperationException) { }
    }

    private void AppendLog(string tag, string message, bool isError)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {tag} {message}";

        lock (_logLock)
        {
            _logBuffer.AppendLine(line);
            if (_logBuffer.Length > 200_000) _logBuffer.Remove(0, 50_000);
        }

        PostToUi(() =>
        {
            TxtLog.AppendText(line + Environment.NewLine);
            if (ChkLogAutoScroll.IsChecked == true) TxtLog.ScrollToEnd();

            if (isError)
            {
                TxtStatus.Text = message.Length > 160 ? message.Substring(0, 160) + "…" : message;
                // 失败必须被看见：自动切到日志页，而不是让它在后台静静躺着
                TabBottom.SelectedIndex = 1;
            }
        });
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        lock (_logLock) { _logBuffer.Clear(); }
        TxtLog.Clear();
    }

    private void BeginBusy()
    {
        _busy++;
        PostToUi(UpdateBusyUi);
    }

    private void EndBusy()
    {
        if (_busy > 0) _busy--;
        PostToUi(UpdateBusyUi);
    }

    private void UpdateBusyUi()
    {
        bool busy = _busy > 0;
        bool hasSelection = (SessionList.SelectedItems?.Count ?? 0) > 0;
        bool hasBatch = BatchList.SelectedItem is BatchRow;

        BtnRefresh.IsEnabled = !busy;
        BtnScan.IsEnabled = !busy;
        CmbBulkSelector.IsEnabled = !busy;
        CmbBulkAction.IsEnabled = !busy;
        BtnBulkRun.IsEnabled = !busy;
        BtnQuarantineRefresh.IsEnabled = !busy;
        BtnRestoreBatch.IsEnabled = !busy && hasBatch;
        BtnPurgeBatch.IsEnabled = !busy && hasBatch;

        BtnArchive.IsEnabled = !busy && hasSelection;
        BtnUnarchive.IsEnabled = !busy && hasSelection;
        BtnDelete.IsEnabled = !busy && hasSelection;
        BtnRepair.IsEnabled = !busy && hasSelection;
        BtnPurge.IsEnabled = !busy && hasSelection;

        if (busy) TxtStatus.Text = "正在执行…";
    }

    private void UpdateActionButtons()
    {
        bool hasSelection = (SessionList.SelectedItems?.Count ?? 0) > 0 && _busy == 0;
        BtnArchive.IsEnabled = hasSelection;
        BtnUnarchive.IsEnabled = hasSelection;
        BtnDelete.IsEnabled = hasSelection;
        BtnRepair.IsEnabled = hasSelection;
        BtnPurge.IsEnabled = hasSelection;
    }

    private void UpdateSelectionInfo()
    {
        var sel = (SessionList.SelectedItems ?? new System.Collections.IList[0]).Cast<object>().OfType<SessionRow>().ToList();
        TxtSelInfo.Text = sel.Count == 0
            ? $"未选择 · 共 {_rows.Count} 条"
            : $"已选 {sel.Count} 条" + (sel.Count == 1 ? $" · {sel[0].KindLabel}" : "");
        UpdateActionButtons();
    }

    private void UpdateFooterRight(string command)
    {
        PostToUi(() => TxtFooterRight.Text = command);
    }

    // ─────────────────────────── JSON / 格式化工具 ───────────────────────────

    private static string Q(string s)
        => s.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;

    private static string? GetString(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool GetString(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
        {
            value = v;
            return true;
        }
        value = default;
        return false;
    }

    private static bool GetBool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.True) return true;
        return v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static long GetLong(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n2)) return n2;
        return 0;
    }

    private static double GetDouble(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
        return 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "-";
        if (bytes < 1024) return bytes + " B";
        double v = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return v.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[i];
    }

    private static string FormatIso(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) return iso;
        var local = dto.LocalDateTime;
        return local.Year == DateTime.Now.Year ? local.ToString("MM-dd HH:mm") : local.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>事件时间：DSH 存的是毫秒 epoch，兼容字符串形式的 ISO。</summary>
    private static string FormatEventTime(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("time", out var t)) return "";
        if (t.ValueKind == JsonValueKind.String)
        {
            return DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
                ? dto.LocalDateTime.ToString("HH:mm:ss")
                : (t.GetString() ?? "");
        }
        if (t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out long ms)) return "";
        if (ms > 100_000_000_000L) return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("HH:mm:ss");
        if (ms > 1_000_000_000L) return DateTimeOffset.FromUnixTimeSeconds(ms).LocalDateTime.ToString("HH:mm:ss");
        return "";
    }

    private static string ActionLabel(string action) => action switch
    {
        "archive" => "归档",
        "unarchive" => "取消归档",
        "delete" => "删除（移入隔离区）",
        "purge" => "永久删除",
        "repair" => "修复",
        _ => action
    };

    private static string SelectorLabel(string selector) => selector switch
    {
        "subagent" => "子agent会话",
        "probe" => "探针会话",
        "empty" => "空会话",
        "unlisted" => "未分组会话",
        "archived" => "已归档会话",
        "imported" => "导入的会话",
        "system" => "系统会话",
        "all" => "全部会话",
        _ => selector
    };

    private static string KindLabelOf(string kind) => kind switch
    {
        "user" => "用户",
        "subagent" => "子agent",
        "imported" => "导入",
        "system" => "系统",
        _ => kind
    };

    private static string HealthTextOf(string health) => health switch
    {
        "ok" => "正常",
        "damaged" => "损坏",
        "unreadable" => "不可读",
        "missing-log" => "缺日志",
        "" => "?",
        _ => health
    };

    // ─────────────────────────── 配色（冻结的静态画刷） ───────────────────────────

    private static readonly Brush BrOk = Frozen("#3FB950");
    private static readonly Brush BrBad = Frozen("#F85149");
    private static readonly Brush BrWarn = Frozen("#C9A227");
    private static readonly Brush BrMuted = Frozen("#8B949E");
    private static readonly Brush BrBgNormal = Frozen("#18191B");
    private static readonly Brush BrBorderNormal = Frozen("#26282B");
    private static readonly Brush BrBgDelegated = Frozen("#2A1A1C");
    private static readonly Brush BrBorderDelegated = Frozen("#7D2B1F");
    private static readonly Brush BrBgInbox = Frozen("#1B2230");
    private static readonly Brush BrBorderInbox = Frozen("#1F6FEB");
    private static readonly Brush BrBgWarn = Frozen("#2A2416");
    private static readonly Brush BrRoleUser = Frozen("#1F6FEB");
    private static readonly Brush BrRoleAssistant = Frozen("#238636");
    private static readonly Brush BrRoleTool = Frozen("#6E40C9");
    private static readonly Brush BrRolePlan = Frozen("#9E6A03");
    private static readonly Brush BrRoleNote = Frozen("#30363D");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    // ─────────────────────────── 行模型 ───────────────────────────

    /// <summary>会话列表的一行：字段全部来自 list 的 JSON，UI 只做格式化。</summary>
    private sealed class SessionRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id = "";
        public string Title = "";
        public string Kind = "";
        public string Workspace = "";
        public string Cwd = "";
        public string LogPath = "";
        public string ModifiedIso = "";
        public string CreatedIso = "";
        public string? SubagentLabel;
        public string? CorruptionText;
        public string? ErrorText;
        public long Bytes;
        public bool Archived;
        public bool ListedInWorkspace;
        public bool Empty;
        public int Events, UserMessages, AssistantMessages, ToolCalls;
        public string Health = "";

        public bool Damaged => Health.Length > 0 && Health != "ok";

        public string KindLabel => KindLabelOf(Kind);

        /// <summary>
        /// The workspace column binds WorkspaceShort, which the XAML referenced but
        /// the class never defined — so that column rendered empty for every row.
        ///
        /// The raw value is a URL-encoded directory name from the sessions tree, e.g.
        /// "--C-Users-Administrator-PycharmProjects--" or
        /// "--D-Program~0020Files-nodejs-node_global-...-~0040deepseek-ai--".
        /// Decode it back to a path and show only the last segment, which is what a
        /// human actually identifies the workspace by.
        /// </summary>
        public string WorkspaceShort
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Workspace)) return "";
                // The synthetic buckets have no path to decode.
                if (Workspace.StartsWith("_")) return Workspace;

                string w = Workspace.Trim('-');
                w = w.Replace("~0020", " ").Replace("~0040", "@");
                w = w.Replace('-', '\\');
                int cut = w.LastIndexOf('\\');
                string tail = cut >= 0 && cut + 1 < w.Length ? w.Substring(cut + 1) : w;
                return tail.Length > 0 ? tail : Workspace;
            }
        }

        public string TitleDisplay
        {
            get
            {
                string baseTitle = string.IsNullOrWhiteSpace(Title)
                    ? (string.IsNullOrWhiteSpace(SubagentLabel) ? "(无标题)" : $"[{SubagentLabel}]")
                    : Title!;
                var marks = new List<string>();
                if (Empty) marks.Add("空会话");
                if (!ListedInWorkspace) marks.Add("未分组");
                return marks.Count == 0 ? baseTitle : baseTitle + " ・" + string.Join("/", marks);
            }
        }

        public string SizeText => FormatBytes(Bytes);
        public string ModifiedText => FormatIso(ModifiedIso);
        public string ArchivedText => Archived ? "是" : "";
        public string HealthText => HealthTextOf(Health);

        public Brush HealthBrush => Health switch
        {
            "ok" => BrOk,
            "damaged" => BrBad,
            "unreadable" => BrBad,
            "missing-log" => BrWarn,
            _ => BrMuted
        };

        public string Tip
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append("id: ").Append(Id).Append('\n');
                sb.Append("类型: ").Append(KindLabel).Append("    健康: ").Append(HealthText).Append('\n');
                sb.Append("大小: ").Append(SizeText).Append("    修改: ").Append(FormatIso(ModifiedIso)).Append('\n');
                sb.Append("创建: ").Append(FormatIso(CreatedIso)).Append('\n');
                sb.Append("cwd: ").Append(string.IsNullOrEmpty(Cwd) ? "-" : Cwd).Append('\n');
                sb.Append("工作区: ").Append(Workspace).Append('\n');
                sb.Append("事件: ").Append(Events).Append("（用户 ").Append(UserMessages)
                  .Append(" / 助手 ").Append(AssistantMessages).Append(" / 工具 ").Append(ToolCalls).Append("）\n");
                sb.Append("在 DSH 列表中: ").Append(ListedInWorkspace ? "是" : "否（未分组）").Append('\n');
                sb.Append("归档: ").Append(Archived ? "是" : "否");
                if (!string.IsNullOrEmpty(CorruptionText)) sb.Append("\ncorruption: ").Append(CorruptionText);
                if (!string.IsNullOrEmpty(ErrorText)) sb.Append("\n错误: ").Append(ErrorText);
                return sb.ToString();
            }
        }

        public bool Matches(string q) =>
            Contains(Title, q) || Contains(Id, q) || Contains(Cwd, q) || Contains(Workspace, q) || Contains(SubagentLabel, q);

        private static bool Contains(string? haystack, string needle)
            => !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

        public void SetHealth(string health, string? corruption, string? error)
        {
            Health = health ?? "";
            CorruptionText = corruption;
            ErrorText = error;
            Raise(nameof(Health));
            Raise(nameof(Damaged));
            Raise(nameof(HealthText));
            Raise(nameof(HealthBrush));
            Raise(nameof(Tip));
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static SessionRow From(JsonElement s)
        {
            var row = new SessionRow
            {
                Id = GetString(s, "id") ?? "",
                Title = GetString(s, "title") ?? "",
                Kind = GetString(s, "kind") ?? "",
                Workspace = GetString(s, "workspace") ?? "",
                Cwd = GetString(s, "cwd") ?? "",
                LogPath = GetString(s, "logPath") ?? "",
                ModifiedIso = GetString(s, "modified") ?? "",
                CreatedIso = GetString(s, "createdAt") ?? "",
                SubagentLabel = GetString(s, "subagentLabel"),
                Bytes = GetLong(s, "bytes"),
                Archived = GetBool(s, "archived"),
                ListedInWorkspace = GetBool(s, "listedInWorkspace"),
                Empty = GetBool(s, "empty"),
                Health = GetString(s, "health") ?? ""
            };

            if (s.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Object)
            {
                row.Events = (int)GetLong(counts, "events");
                row.UserMessages = (int)GetLong(counts, "userMessages");
                row.AssistantMessages = (int)GetLong(counts, "assistantMessages");
                row.ToolCalls = (int)GetLong(counts, "toolCalls");
            }

            if (s.TryGetProperty("corruption", out var corr) && corr.ValueKind == JsonValueKind.Object)
            {
                double lost = GetDouble(corr, "lostPercent");
                string reason = GetString(corr, "reason") ?? "";
                row.CorruptionText = (lost > 0 ? $"丢失约 {lost.ToString("0.0", CultureInfo.InvariantCulture)}%：" : "") + reason;
            }
            row.ErrorText = GetString(s, "error");
            return row;
        }
    }

    /// <summary>隔离批次的一行（quarantine-list 的输出）。</summary>
    private sealed class BatchRow
    {
        // Properties, not fields — the quarantine grid binds Batch and
        // ManifestPath directly (DisplayMemberBinding="{Binding Batch}"), and
        // WPF's binding engine ignores public fields without raising anything.
        // Declared as fields those two columns rendered EMPTY while the three
        // property-backed columns beside them showed values.
        public string Batch { get; set; } = "";
        public string CreatedAtIso { get; set; } = "";
        public string ManifestPath { get; set; } = "";
        public long Items { get; set; }
        public long Bytes { get; set; }

        public string CreatedText => FormatIso(CreatedAtIso);
        public string ItemsText => Items.ToString(CultureInfo.InvariantCulture);
        public string SizeText => FormatBytes(Bytes);
        public bool Damaged => false;
        public string Tip => $"批次: {Batch}\n创建: {CreatedText}\n条目: {ItemsText} · {SizeText}\n清单: {ManifestPath}";
    }

    /// <summary>逐回合记录里的一条（user / assistant / tool / plan）。</summary>
    private sealed class EntryRow
    {
        // These MUST be properties, not fields.
        //
        // WPF's binding engine resolves only public PROPERTIES. A public field is
        // invisible to {Binding X} and fails silently - no exception, no trace
        // warning by default. Declared as fields, every binding in EntryTemplate
        // resolved to nothing, so each row rendered as a Border with no text and
        // no background: the transcript pane looked empty even though it held
        // items (a scrollbar was visible, which is what gave it away).
        //
        // A template smoke test cannot catch this: LoadContent() only parses the
        // template, it never evaluates a binding.
        public string Role { get; set; } = "";
        public string RoleLabel { get; set; } = "";
        public string Meta { get; set; } = "";
        public string Body { get; set; } = "";
        public bool ViaInbox { get; set; }
        public bool Truncated { get; set; }
        public string TruncatedNote { get; set; } = "";
        public string ViaBadgeText { get; set; } = "";
        public Brush RoleBrush { get; set; } = BrMuted;
        public Brush ViaBadgeBrush { get; set; } = BrBorderDelegated;
        public Brush RowBackground { get; set; } = BrBgNormal;
        public Brush RowBorder { get; set; } = BrBorderNormal;

        public Visibility ViaBadgeVisibility => ViaInbox && ViaBadgeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TruncatedVisibility => Truncated ? Visibility.Visible : Visibility.Collapsed;

        public static EntryRow From(string turnLabel, JsonElement entry)
        {
            string role = GetString(entry, "role") ?? "?";
            var row = new EntryRow { Role = role };
            string seq = GetString(entry, "seq") ?? "";
            string time = FormatEventTime(entry);

            switch (role)
            {
                case "user":
                {
                    row.RoleLabel = "用户";
                    row.RoleBrush = BrRoleUser;
                    string text = GetString(entry, "text") ?? "";
                    row.ViaInbox = GetBool(entry, "viaInbox");
                    string inboxKind = GetString(entry, "inboxKind") ?? (row.ViaInbox ? "user" : "");
                    bool delegated = GetBool(entry, "delegated") || inboxKind == "delegated";

                    // 徽章说的是“这条文本是什么”，不是“它算不算用户的话”：
                    // CLI 把 inbox 注入分成 user / system / delegated 三类，
                    // 只有后两类不是用户本人写的（--user-only 也只剔除这两类）。
                    if (delegated)
                    {
                        row.RowBackground = BrBgDelegated;
                        row.RowBorder = BrBorderDelegated;
                        row.ViaBadgeBrush = BrBorderDelegated;
                        row.ViaBadgeText = "⚠ 委派任务文本（另一个 agent 交给本会话的任务）";
                    }
                    else if (inboxKind == "system")
                    {
                        row.RowBackground = BrBgWarn;
                        row.RowBorder = BrWarn;
                        row.ViaBadgeBrush = BrWarn;
                        row.ViaBadgeText = "⚙ 系统注入（非你输入）";
                    }
                    else if (row.ViaInbox)
                    {
                        row.RowBackground = BrBgInbox;
                        row.RowBorder = BrBorderInbox;
                        row.ViaBadgeBrush = BrBorderInbox;
                        row.ViaBadgeText = "↓ 运行中注入（仍是你本人输入）";
                    }

                    row.Meta = BuildMeta(turnLabel, seq, time, "", $"{text.Length} 字");
                    row.Body = text;
                    break;
                }
                case "assistant":
                {
                    row.RoleLabel = "助手";
                    row.RoleBrush = BrRoleAssistant;
                    string text = GetString(entry, "text") ?? "";
                    row.Meta = BuildMeta(turnLabel, seq, time, "", $"{text.Length} 字");
                    row.Body = text;
                    break;
                }
                case "tool":
                {
                    row.RoleLabel = "工具";
                    row.RoleBrush = BrRoleTool;
                    string name = GetString(entry, "name") ?? "(未命名工具)";
                    string args = RenderRaw(entry, "args");
                    string argsNote = args.Length > 1200 ? "（参数已截断显示）" : "";
                    row.Meta = BuildMeta(turnLabel, seq, time, "", "tool/call");
                    row.Body = args.Length == 0 ? $"调用工具：{name}" : $"调用工具：{name}\n参数：{Truncate(args, 1200)}{argsNote}";
                    break;
                }
                case "plan":
                {
                    row.RoleLabel = "计划";
                    row.RoleBrush = BrRolePlan;
                    string todos = RenderTodos(entry);
                    row.Meta = BuildMeta(turnLabel, seq, time, "", "todo/write");
                    row.Body = string.IsNullOrWhiteSpace(todos) ? "（计划为空）" : todos;
                    break;
                }
                default:
                {
                    row.RoleLabel = role;
                    row.RoleBrush = BrRoleNote;
                    row.Meta = BuildMeta(turnLabel, seq, time, "", "");
                    row.Body = RenderRaw(entry, "text");
                    break;
                }
            }

            if (row.Body.Length > EntryBodyCap)
            {
                int original = row.Body.Length;
                row.Body = row.Body.Substring(0, EntryBodyCap);
                row.Truncated = true;
                row.TruncatedNote = $"…正文过长，只显示前 {EntryBodyCap} 字符（原始 {original} 字符）";
            }

            return row;
        }

        private static string BuildMeta(string turnLabel, string seq, string time, string badge, string extra)
        {
            var parts = new List<string> { $"回合 {turnLabel}" };
            if (seq.Length > 0) parts.Add("#" + seq);
            if (time.Length > 0) parts.Add(time);
            if (badge.Length > 0) parts.Add(badge);
            if (extra.Length > 0) parts.Add(extra);
            return string.Join("  ·  ", parts);
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

        private static string RenderRaw(JsonElement entry, string name)
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(name, out var v)) return "";
            return v.ValueKind switch
            {
                JsonValueKind.Undefined or JsonValueKind.Null => "",
                JsonValueKind.String => v.GetString() ?? "",
                _ => v.GetRawText()
            };
        }

        private static string RenderTodos(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array)
                return "";
            var sb = new StringBuilder();
            foreach (var item in todos.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) { sb.Append("• ").Append(item.GetString()).Append('\n'); continue; }
                if (item.ValueKind == JsonValueKind.Object)
                {
                    string content = GetString(item, "content") ?? GetString(item, "text") ?? GetString(item, "subject") ?? item.GetRawText();
                    string status = GetString(item, "status") ?? "";
                    sb.Append(status.Length > 0 ? $"• [{status}] {content}" : "• " + content).Append('\n');
                    continue;
                }
                sb.Append("• ").Append(item.GetRawText()).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }
    }
}
