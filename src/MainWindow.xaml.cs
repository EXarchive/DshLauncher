using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher;

public partial class MainWindow : Window
{
    private readonly ProcessRunnerService _runner = new();
    private LauncherConfig _config = new();
    private readonly StringBuilder _logBuffer = new();
    private bool _webViewReady;
    /// <summary>
    /// 控件回填期间为 false。XAML 里 ChkHideConsole 的 IsChecked="True" 会在
    /// InitializeComponent 阶段就触发 Checked 事件，此时 _config 还是默认值 ——
    /// 不挡住的话，一次启动就会把用户的真实配置覆盖成默认值。
    /// </summary>
    private bool _uiReady;
    /// <summary>
    /// ReleaseEmbeddedCoreAsync 会 Dispose 掉 MainWebView，而 Dispose 过的 WebView2
    /// 无法复活：对它再调 EnsureCoreWebView2Async / 读 CoreWebView2 / 设 Visibility
    /// 都会抛 "Cannot access a disposed object. Object name: 'MainWebView'"。
    /// 所以必须记住"当前实例已废弃"，并在下次需要时新建一个控件替换。
    /// </summary>
    private bool _webViewDisposed;
    private bool _webViewSuspended;

    /// <summary>
    /// 内嵌内核初始化的串行闸。EnsureWebViewReadyAsync 是 async 的，认证地址回调和
    /// 状态回调都可能并发进来；没有它就会对同一个控件重复 EnsureCoreWebView2Async，
    /// 第二次必然失败（表现为 RPC_E_DISCONNECTED / 对象已断开）。
    /// </summary>
    private readonly System.Threading.SemaphoreSlim _webViewGate = new(1, 1);

    // ── 救援侧栏（内嵌，不再是独立窗口）────────────────────────────────────
    /// <summary>侧栏默认宽度（展开时的初始值，之后可被 GridSplitter 改变）。</summary>
    private const double RescueSidebarDefaultWidth = 440;

    /// <summary>侧栏展开后至少要留给内嵌工作区的宽度（低于它就先把窗口加宽）。</summary>
    private const double RescueWebViewMinWidth = 500;

    /// <summary>收起时记住用户拖出来的宽度，下次展开恢复。</summary>
    private double _lastRescueSidebarWidth = RescueSidebarDefaultWidth;

    // 「🛟 救援」按钮的展开态高亮（应用里没有现成的 toggle 样式，用颜色 + 强调边框表达"已展开"）。
    // 这些是普通字段，XAML 不绑定它们；绑定的目标是属性（见下方各 x:Name 控件）。
    private static readonly Brush RescueOnBackground = Frozen("#30363D");
    private static readonly Brush RescueOnBorder = Frozen("#2D72F6");
    private static readonly Brush RescueOnForeground = Frozen("#FFFFFF");
    private static readonly Brush RescueOffBackground = Frozen("#21262D");
    private static readonly Brush RescueOffBorder = Frozen("#30363D");
    private static readonly Brush RescueOffForeground = Frozen("#C9D1D9");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public MainWindow()
    {
        InitializeComponent();

        _runner.OnLogReceived += Runner_OnLogReceived;
        _runner.OnStatusChanged += Runner_OnStatusChanged;
        _runner.OnAuthenticatedUrlDetected += authUrl =>
        {
            Dispatcher.Invoke(() =>
            {
                AppendLogLine($"[认证] 已捕获带 token 的访问地址: {authUrl}");

                // 【自动加载】勾选时把认证地址载入内嵌视图；绝不唤起系统默认浏览器
                if (ChkAutoOpenBrowser.IsChecked == true)
                {
                    LoadEmbeddedBrowser(authUrl);
                }
                else
                {
                    AppendLogLine("[提示] 已关闭自动加载，可点击 [刷新] 载入内嵌视图。");
                }
            });
        };

        LoadConfiguration();

        // 救援面板：绑定配置（面板默认折叠，连接期间不跑任何脚本；首次展开时才读一次 status）
        RescueSidebar.Attach(_config);
        UpdateRescueButtonState();

        // 窗口最小化时挂起内嵌内核以节省内存
        StateChanged += MainWindow_StateChanged;

        // 宿主关闭：置守卫，面板不再触碰控件（正在跑的救援命令不杀，让它跑完）
        Closing += (_, _) => RescueSidebar.Detach();
    }

    /// <summary>
    /// 若当前 WebView2 实例已被 Dispose，就新建一个并放回 WebHost 的最底层
    /// （PnlEmptyState 是它的兄弟节点、叠在上面当空状态遮罩，所以新控件要插到索引 0）。
    /// 没被 Dispose 时什么都不做。
    /// </summary>
    private void EnsureWebViewInstance()
    {
        if (!_webViewDisposed) return;

        // 先把 WebHost 里残留的 WebView2 全部摘掉。
        // 只 Insert 不移除会让同一个 WebHost 里挂着两个 WebView2 控件，
        // 它们抢同一个 profile 目录 —— 第二个必然初始化失败（RPC_E_DISCONNECTED）。
        for (int i = WebHost.Children.Count - 1; i >= 0; i--)
        {
            if (WebHost.Children[i] is Microsoft.Web.WebView2.Wpf.WebView2 old)
            {
                try { WebHost.Children.RemoveAt(i); } catch { }
                try { old.Dispose(); } catch { }
            }
        }

        var view = new Microsoft.Web.WebView2.Wpf.WebView2
        {
            Name = "MainWebView",
            Visibility = Visibility.Collapsed,
            // 与 XAML 的 DefaultBackgroundColor="#141517" 一致，避免重建后白底闪烁
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x14, 0x15, 0x17),
        };

        WebHost.Children.Insert(0, view);
        MainWebView = view;
        _webViewDisposed = false;
        AppendLogLine("[界面] 已重建内嵌内核控件。");
    }

    /// <summary>
    /// 内嵌内核的 profile 根目录，**按运行账户隔离**。
    ///
    /// 为什么必须隔离：SYSTEM 模式启动时 $LOCALAPPDATA 会被注入成 Administrator 的路径，
    /// 于是「管理员」和「SYSTEM」两个权限级别会抢同一个 WebView2 profile 目录。
    /// 切换权限等于重启启动器，旧 WebView2 浏览器进程还在清理中，新实例去抢同一个
    /// profile，就会抛 RPC_E_DISCONNECTED（被调用的对象已与其客户端断开连接）——
    /// 这正是「SYSTEM 能用、切成管理员就启动失败」的机制。
    ///
    /// 按 Environment.UserName 分开后，Administrator 用 WebView2-Administrator、
    /// SYSTEM 用 WebView2-SYSTEM，互不干扰，切换权限不再冲突。
    /// </summary>
    private static string WebViewProfileRoot()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user)) user = "default";

        var sb = new StringBuilder("WebView2-");
        foreach (char c in user)
        {
            // 只留下能做目录名的字符
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }

        return Path.Combine(localAppData, "DshLauncher", sb.ToString());
    }

    /// <summary>
    /// 内嵌内核初始化入口：串行化 + 失败自愈。
    ///
    /// 三层防护：
    ///  1) _webViewGate 串行化 —— 认证地址回调与状态回调会并发进来，对同一控件重复
    ///     EnsureCoreWebView2Async 必然让后一次失败（表现为 RPC_E_DISCONNECTED）。
    ///  2) 首次失败后重建控件 + 换一个全新 profile 目录重试一次 —— WebView2 上次异常
    ///     退出（Crashpad 里有 edge_shutdown_crash.txt）后 profile 可能坏掉，导致每次
    ///     初始化都失败且无法自愈。
    ///  3) 错误信息不再误导成「请确认已安装 WebView2 Runtime」—— 运行时其实是装着的。
    /// </summary>
    private async Task<bool> EnsureWebViewReadyAsync()
    {
        if (_webViewReady) return true;

        await _webViewGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_webViewReady) return true;   // 等锁期间别人已经建好了

            EnsureWebViewInstance();

            // 按运行账户隔离 profile：管理员与 SYSTEM 各用各的目录，
            // 切换权限时不会抢同一个 profile（详见 WebViewProfileRoot 的注释）。
            string baseFolder = WebViewProfileRoot();
            AppendLogLine($"[界面] WebView2 profile: {baseFolder}");

            if (await InitWebViewCoreAsync(baseFolder)) return true;

            // ── 自愈一次：profile 可能已经坏了 ──
            AppendLogLine("[界面] 首次初始化失败，改用全新 profile 目录重试…");
            try { MainWebView.Dispose(); } catch { }
            try { WebHost.Children.Remove(MainWebView); } catch { }
            _webViewDisposed = true;
            EnsureWebViewInstance();

            string freshFolder = baseFolder + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (await InitWebViewCoreAsync(freshFolder))
            {
                AppendLogLine($"[界面] 已切换到全新 profile 恢复内嵌内核：{freshFolder}");
                AppendLogLine("[提示] 原 profile 已弃用，旧目录可手工删除，不影响使用。");
                return true;
            }

            AppendLogLine("[提示] 若持续失败，可删除目录 " + baseFolder + " 后重启启动器。");
            return false;
        }
        finally
        {
            _webViewGate.Release();
        }
    }

    /// <summary>用指定 profile 目录初始化一次 WebView2；成功时置 _webViewReady 并返回 true。</summary>
    private async Task<bool> InitWebViewCoreAsync(string userDataFolder)
    {
        try
        {
            Directory.CreateDirectory(userDataFolder);

            // 位置参数写法：本 SDK 版本的 CreateAsync 重载没有名为 environmentOptions 的形参
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                null,
                userDataFolder,
                WebViewOptions.ForPort(_config.CdpDebugPort));

            await MainWebView.EnsureCoreWebView2Async(env);

            if (_config.CdpDebugPort > 0)
            {
                AppendLogLine($"[界面] 已为内嵌视图打开 Chromium 远程调试端口 :{_config.CdpDebugPort}（dsh-cdp 可附加）。");
            }

            var core = MainWebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsZoomControlEnabled = true;

            // 兜底：「界面极其缩小」最常见的原因就是 WebView2 的缩放被持久化成极小值
            // （Ctrl+滚轮 或缩放控件），而且 Chromium 会把每站点的缩放存进 profile，
            // 重启也不恢复。这里每次内核就绪后强制回到 100%，配合 DPI 清单，
            // 两条「缩小的路」都堵死。用户仍可在浏览器里手动缩放，只是不再被旧值黏住。
            try { MainWebView.ZoomFactor = 1.0; }
            catch { }

            // 强制 Chromium 采用暗黑渲染，杜绝白底闪烁
            try
            {
                core.Profile.PreferredColorScheme =
                    Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark;
            }
            catch { }

            // 每次文档创建时自动注入暗黑兜底样式（无需手动刷新）
            await core.AddScriptToExecuteOnDocumentCreatedAsync(@"
                (function(){
                    var css = 'html,body{background:#141517 !important;color-scheme:dark;}'
                        + '::-webkit-scrollbar{width:10px;height:10px;}'
                        + '::-webkit-scrollbar-track{background:#141517;}'
                        + '::-webkit-scrollbar-thumb{background:#30363D;border-radius:5px;}'
                        + '::-webkit-scrollbar-thumb:hover{background:#484F58;}';
                    var apply = function(){
                        try{
                            var s = document.createElement('style');
                            s.textContent = css;
                            (document.head || document.documentElement).appendChild(s);
                        }catch(e){}
                    };
                    if (document.head) { apply(); }
                    else { document.addEventListener('DOMContentLoaded', apply); }
                })();
            ");

            _webViewReady = true;
            AppendLogLine("[界面] Chromium (WebView2) 内核已就绪。");
            return true;
        }
        catch (Exception ex)
        {
            AppendLogLine($"[错误] WebView2 内核初始化失败（profile={userDataFolder}）: {ex.Message}");
            return false;
        }
    }

    private void LoadConfiguration()
    {
        _config = ConfigService.LoadConfig();

        TxtArgs.Text = _config.CustomArguments;
        TxtPort.Text = _config.WebPort.ToString();
        ChkAutoOpenBrowser.IsChecked = _config.AutoOpenBrowser;
        ChkAutoRestart.IsChecked = _config.AutoRestartOnCrash;
        ChkHideConsole.IsChecked = _config.HideConsole;
        SelectPrivilegeInUi(_config.Privilege);

        UpdatePathStatusText();

        AppendLogLine("[系统] 启动器已就绪，配置已加载完成。");
        AppendLogLine($"[环境] DSH路径: {(_config.DshPath.Length > 0 ? _config.DshPath : "未设置(请在右上角设置中指定)")}");
        AppendLogLine($"[环境] 配置文件: {(_config.ConfigPath.Length > 0 ? _config.ConfigPath : "未设置")}");
        AppendLogLine($"[环境] 启动器自身权限: {(PrivilegeLauncher.LauncherIsElevated ? "管理员" : "普通用户")}"
            + (PrivilegeLauncher.LauncherIsElevated
                ? "（选「管理员 / SYSTEM」不会弹 UAC）"
                : "（选「管理员 / SYSTEM」会弹 UAC）"));
        AppendLogLine($"[启动选项] 权限={PrivilegeLevelText.Label(_config.Privilege)} · 隐藏控制台={(_config.HideConsole ? "是" : "否")}");
        AppendLogLine($"[启动选项] {PrivilegeLevelText.Hint(_config.Privilege, PrivilegeLauncher.LauncherIsElevated)}");

        // 日志面板可见性：在 _uiReady 之前套用，避免这一步触发保存。
        ApplyLogPanelVisibility(!_config.HideLogPanel, persist: false);

        _uiReady = true;
    }

    private void UpdatePathStatusText()
    {
        // 界面精简后状态收纳于日志与设置中
    }

    private void SaveCurrentUiToConfig()
    {
        _config.CustomArguments = TxtArgs.Text.Trim();
        if (int.TryParse(TxtPort.Text.Trim(), out int port))
        {
            _config.WebPort = port;
        }
        _config.AutoOpenBrowser = ChkAutoOpenBrowser.IsChecked == true;
        _config.AutoRestartOnCrash = ChkAutoRestart.IsChecked == true;
        _config.HideConsole = ChkHideConsole.IsChecked == true;
        _config.Privilege = SelectedPrivilege();

        ConfigService.SaveConfig(_config);
    }

    // ─────────────────── 启动选项：权限级别 / 隐藏控制台 ───────────────────

    private PrivilegeLevel SelectedPrivilege()
    {
        if (CmbPrivilege.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && Enum.TryParse(tag, out PrivilegeLevel level))
        {
            return level;
        }
        return PrivilegeLevel.Normal;
    }

    private void SelectPrivilegeInUi(PrivilegeLevel level)
    {
        foreach (var o in CmbPrivilege.Items)
        {
            if (o is ComboBoxItem item && item.Tag is string tag
                && Enum.TryParse(tag, out PrivilegeLevel l) && l == level)
            {
                CmbPrivilege.SelectedItem = item;
                return;
            }
        }
        CmbPrivilege.SelectedIndex = 0;
    }

    private void CmbPrivilege_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        var level = SelectedPrivilege();
        _config.Privilege = level;
        ConfigService.SaveConfig(_config);
        AppendLogLine($"[启动选项] 权限级别 → {PrivilegeLevelText.Label(level)}。"
            + PrivilegeLevelText.Hint(level, PrivilegeLauncher.LauncherIsElevated));
    }

    private void ChkHideConsole_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _config.HideConsole = ChkHideConsole.IsChecked == true;
        ConfigService.SaveConfig(_config);
        AppendLogLine(_config.HideConsole
            ? "[启动选项] 已隐藏 DSH 控制台窗口（输出只进本面板）。"
            : "[启动选项] 将显示 DSH 控制台窗口（可直接看到 node 原始输出）。");
    }

    // ─────────────────── 隐藏 / 显示底部日志面板 ───────────────────

    /// <summary>面板被收起前的高度，用于恢复。用户拖过分隔条时这个值是随手拖出来的。</summary>
    private GridLength _consoleHeight = new(160);

    private void TglShowLog_Click(object sender, RoutedEventArgs e)
        => ApplyLogPanelVisibility(TglShowLog.IsChecked == true, persist: true);

    private void BtnCollapseLog_Click(object sender, RoutedEventArgs e)
        => ApplyLogPanelVisibility(false, persist: true);

    /// <summary>
    /// 收起时把行高设为 0 并且清掉 MinHeight —— 只把内容设成 Collapsed 是不够的：
    /// RowDefinition 上写死的 MinHeight="80" 会继续占着 80 像素。
    /// </summary>
    private void ApplyLogPanelVisibility(bool visible, bool persist)
    {
        if (visible)
        {
            RowConsole.MinHeight = 80;
            RowConsole.Height = _consoleHeight.Value > 0 ? _consoleHeight : new GridLength(160);
            ConsolePanel.Visibility = Visibility.Visible;
            ConsoleSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            // 只在第一次收起时记住高度，避免重复点击把它记成 0。
            if (RowConsole.Height.Value > 0) _consoleHeight = RowConsole.Height;
            RowConsole.MinHeight = 0;
            RowConsole.Height = new GridLength(0);
            ConsolePanel.Visibility = Visibility.Collapsed;
            ConsoleSplitter.Visibility = Visibility.Collapsed;
        }

        if (TglShowLog != null) TglShowLog.IsChecked = visible;
        if (!_uiReady || !persist) return;
        _config.HideLogPanel = !visible;
        ConfigService.SaveConfig(_config);
    }

    private void Runner_OnStatusChanged(ProcessStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            switch (status)
            {
                case ProcessStatus.Stopped:
                    BadgeStatus.Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#475569"));
                    TxtStatusBadge.Text = "已停止 (Stopped)";
                    BtnStart.IsEnabled = true;
                    BtnStop.IsEnabled = false;
                    TxtFooterInfo.Text = "就绪 - 服务未运行";
                    break;
                case ProcessStatus.Starting:
                    BadgeStatus.Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B"));
                    TxtStatusBadge.Text = "正在启动...";
                    BtnStart.IsEnabled = false;
                    BtnStop.IsEnabled = true;
                    TxtFooterInfo.Text = "正在启动 DSH 服务子进程...";
                    break;
                case ProcessStatus.Running:
                    BadgeStatus.Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981"));
                    TxtStatusBadge.Text = $"运行中 (PID: {_runner.CurrentProcessId})";
                    BtnStart.IsEnabled = false;
                    BtnStop.IsEnabled = true;
                    TxtFooterInfo.Text = $"DSH 正在运行 | 端口: {_config.WebPort} | PID: {_runner.CurrentProcessId}";
                    
                    // 启动成功后，自动连接并加载内嵌 Web 视图
                    if (ChkAutoOpenBrowser.IsChecked == true)
                    {
                        LoadEmbeddedBrowser();
                    }
                    break;
                case ProcessStatus.Stopping:
                    BadgeStatus.Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B"));
                    TxtStatusBadge.Text = "正在停止...";
                    BtnStart.IsEnabled = false;
                    BtnStop.IsEnabled = false;
                    TxtFooterInfo.Text = "正在终止 DSH 进程树...";
                    break;
                case ProcessStatus.Error:
                    BadgeStatus.Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
                    TxtStatusBadge.Text = "启动异常 (Error)";
                    BtnStart.IsEnabled = true;
                    BtnStop.IsEnabled = false;
                    TxtFooterInfo.Text = "服务异常终止，请查看控制台日志详情";
                    break;
            }
        });
    }

    private void Runner_OnLogReceived(string message, bool isError)
    {
        Dispatcher.Invoke(() =>
        {
            string prefix = isError ? "[错误] " : "";
            AppendLogLine($"{prefix}{message}");
        });
    }

    private void AppendLogLine(string line)
    {
        string formatted = $"[{DateTime.Now:HH:mm:ss}] {line}\r\n";
        _logBuffer.Append(formatted);

        if (_logBuffer.Length > 200000)
        {
            _logBuffer.Remove(0, 50000);
            TxtLogs.Text = _logBuffer.ToString();
        }
        else
        {
            TxtLogs.AppendText(formatted);
        }

        if (ChkAutoScroll.IsChecked == true)
        {
            TxtLogs.ScrollToEnd();
        }
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentUiToConfig();
        if (string.IsNullOrWhiteSpace(_config.DshPath))
        {
            var result = MessageBox.Show("当前尚未设置 DSH 根路径，是否立即打开设置面板进行配置？", "提示", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                OpenSettings();
            }
            return;
        }

        if (_webViewSuspended)
        {
            ResumeEmbeddedCore();
        }

        await _runner.StartAsync(_config);
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        await _runner.StopAsync();

        // 服务停止后立即释放 Chromium 内嵌内核占用的内存
        await ReleaseEmbeddedCoreAsync();
    }

    /// <summary>
    /// 挂起并销毁内嵌 Chromium 内核，释放其全部分离进程占用的内存（约 150~300MB）。
    /// </summary>
    private async Task ReleaseEmbeddedCoreAsync()
    {
        try
        {
            if (MainWebView.CoreWebView2 != null)
            {
                MainWebView.CoreWebView2.Navigate("about:blank");
                await MainWebView.CoreWebView2.TrySuspendAsync();
            }
        }
        catch { }

        try
        {
            MainWebView.Dispose();
        }
        catch (Exception ex)
        {
            AppendLogLine($"[内存] 释放内嵌内核时出现异常（已忽略）: {ex.Message}");
        }

        // Dispose 之后这个实例就废了：从可视树摘掉，并标记需要重建。
        // 绝不能再读它的属性或设 Visibility —— 那正是 disposed object 异常的来源。
        try { WebHost.Children.Remove(MainWebView); } catch { }
        _webViewDisposed = true;

        _webViewReady = false;
        _webViewSuspended = false;

        PnlEmptyState.Visibility = Visibility.Visible;

        AppendLogLine("[内存] 内嵌内核已释放（下次加载时会自动重建）。");
    }

    /// <summary>
    /// 窗口最小化时挂起渲染进程并切换到低内存模式；还原时恢复。
    /// 依据微软官方《Performance best practices for WebView2 apps》建议：
    /// 非活动 WebView 设置 MemoryUsageTargetLevel = Low，并调用 TrySuspendAsync() 暂停渲染进程。
    /// </summary>
    private async void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_webViewDisposed) return;
        if (MainWebView.CoreWebView2 == null) return;

        try
        {
            if (WindowState == WindowState.Minimized)
            {
                MainWebView.CoreWebView2.MemoryUsageTargetLevel =
                    Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Low;
                await MainWebView.CoreWebView2.TrySuspendAsync();
                _webViewSuspended = true;
                AppendLogLine("[内存] 窗口已最小化，内嵌内核进入低内存模式。");
            }
            else if (_webViewSuspended)
            {
                MainWebView.CoreWebView2.MemoryUsageTargetLevel =
                    Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Normal;
                MainWebView.CoreWebView2.Resume();
                _webViewSuspended = false;
            }
        }
        catch { }
    }

    private void ResumeEmbeddedCore()
    {
        if (_webViewDisposed) { _webViewSuspended = false; return; }
        try
        {
            MainWebView.CoreWebView2?.Resume();
            if (MainWebView.CoreWebView2 != null)
            {
                MainWebView.CoreWebView2.MemoryUsageTargetLevel =
                    Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Normal;
            }
        }
        catch { }
        _webViewSuspended = false;
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    /// <summary>
    /// 打开「会话管理」——驱动 tools/session-manager.mjs 清点会话、逐回合核对、归档与隔离。
    /// 同样是纯新增能力：不启动/停止任何 DSH 进程，也不直接触碰会话日志（一切经 Node CLI）。
    /// </summary>
    private void BtnSessions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new SessionsWindow(_config, RestartDshAsync)
            {
                Owner = this
            };
            win.Show();
        }
        catch (Exception ex)
        {
            AppendLogLine($"[错误] 打开会话管理失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 重启 DSH 实例，让「直接写 workspace.json」的归档变更被装载。
    ///
    /// 为什么非要重启：DSH 的存储后端（dsh-storage-json）只在打开存储单元时把
    /// workspace.json 读一次，注释明写 in-memory state is authoritative，全文没有
    /// watch/stat/reload；而它的 workspace 接口又只有 archiveSession（只增不改），
    /// 全树搜不到 unarchive。所以归档状态的变更只能靠重启拾取。
    ///
    /// ⚠ 会终止该实例上所有正在进行的对话（含调用者正在用的会话）——
    /// 调用方（会话管理器）必须先经用户明确确认再调这里。
    /// </summary>
    private async Task<bool> RestartDshAsync()
    {
        if (_runner.Status != ProcessStatus.Running)
        {
            AppendLogLine("[归档] DSH 当前未在运行，无需重启（下次启动即会读取最新归档状态）。");
            return true;
        }

        AppendLogLine("[归档] 正在重启 DSH 以装载归档状态（该实例上的对话会中断）…");
        await _runner.StopAsync();
        await Task.Delay(1500);
        bool ok = await _runner.StartAsync(_config);
        AppendLogLine(ok
            ? "[归档] DSH 已重启完成，归档状态现已生效。"
            : "[归档] DSH 重启失败，请在主窗口手动点「启动」。");
        return ok;
    }

    /// <summary>
    /// 「🛟 救援」——把内嵌在窗口右侧的救援侧栏**展开 / 收起**（不再是独立窗口）。
    /// 侧栏驱动 tools/rescue-dsh.ps1 的五个命令：
    /// create 快照 / status 状态 / verify 体检 / run 试用 / repair 修复会话日志。
    /// 同样是纯新增能力：面板只发命令并把 stdout/stderr 原样显示，
    /// 不启动/停止任何 DSH 进程，也不绑定端口（repair 在真实 DSH home 上就地修复会话日志，先备份后截断）。
    /// </summary>
    private void BtnRescue_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetRescueSidebarVisible(RescueSidebarHost.Visibility != Visibility.Visible);
        }
        catch (Exception ex)
        {
            AppendLogLine($"[错误] 切换救援侧栏失败: {ex.Message}");
        }
    }

    /// <summary>侧栏标题行上的 ✕：收起侧栏（与再点一次「🛟 救援」等价）。</summary>
    private void BtnRescueCollapse_Click(object sender, RoutedEventArgs e) => SetRescueSidebarVisible(false);

    /// <summary>
    /// 展开 / 收起救援侧栏。展开时同时显示 4px 分隔条并把列宽设回 440（或用户上次拖出来的宽度）；
    /// 若这样会把内嵌工作区挤到 500px 以下，就把窗口加宽（最大化时不动窗口尺寸）。
    /// 收起时隐藏两者并把列宽归零，宽度记在 _lastRescueSidebarWidth 里，下次展开恢复。
    /// </summary>
    private void SetRescueSidebarVisible(bool visible)
    {
        if (visible)
        {
            if (RescueSidebarHost.Visibility == Visibility.Visible) return;

            RescueSidebarHost.Visibility = Visibility.Visible;
            RescueSplitter.Visibility = Visibility.Visible;
            RescueSidebarColumn.Width = new GridLength(
                Math.Max(_lastRescueSidebarWidth, RescueSidebarHost.MinWidth), GridUnitType.Pixel);

            if (WindowState == WindowState.Normal)
            {
                double needed = RescueWebViewMinWidth + RescueSidebarColumn.Width.Value + RescueSplitter.Width;

                // 布局尚未跑过时 ActualWidth 还是 0：那种情况下用 Width（XAML 里的初值）判断，
                // 否则「挤到 500px 以下」会被误判成真，反而把窗口缩窄。
                double current = ActualWidth > 0 ? ActualWidth : Width;

                if (current < needed)
                {
                    double cap = SystemParameters.WorkArea.Width - 24;
                    Width = Math.Min(needed + 24, Math.Max(MinWidth, cap));
                    AppendLogLine($"[界面] 已加宽窗口到 {Width:0} px，保证工作区不被侧栏挤到 {RescueWebViewMinWidth:0} px 以下。");
                }
            }

            AppendLogLine("[界面] 已展开救援侧栏（右侧）。");

            // 首次展开才跑一次 status：面板折叠期间不执行任何脚本。
            _ = RescueSidebar.ActivateAsync();
        }
        else
        {
            if (RescueSidebarHost.Visibility != Visibility.Visible) return;

            double current = RescueSidebarColumn.ActualWidth;
            if (current > 0) _lastRescueSidebarWidth = current;

            RescueSidebarHost.Visibility = Visibility.Collapsed;
            RescueSplitter.Visibility = Visibility.Collapsed;
            RescueSidebarColumn.Width = new GridLength(0);

            AppendLogLine("[界面] 已收起救援侧栏（正在执行的救援命令会继续跑完，输出仍保留在面板日志里）。");
        }

        UpdateRescueButtonState();
    }

    /// <summary>展开时给「🛟 救援」按钮一个已激活的外观（应用里没有现成的 toggle 样式）。</summary>
    private void UpdateRescueButtonState()
    {
        bool open = RescueSidebarHost.Visibility == Visibility.Visible;

        BtnRescue.Background = open ? RescueOnBackground : RescueOffBackground;
        BtnRescue.BorderBrush = open ? RescueOnBorder : RescueOffBorder;
        BtnRescue.Foreground = open ? RescueOnForeground : RescueOffForeground;
        BtnRescue.ToolTip = open
            ? "DSH 救援（侧栏已展开）：再点一次收起"
            : "DSH 救援：在右侧展开侧栏 —— 快照体检 / 刷新快照 / 修复会话 / 试用救援包（机器上的 DSH 装坏了也能修日志）";
    }

    private void OpenSettings()
    {
        var dialog = new SettingsDialog(_config)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _config = dialog.Config;
            ConfigService.SaveConfig(_config);
            UpdatePathStatusText();

            // 配置可能换了 node.exe：把新配置交给救援面板（重复 Attach 只更新配置，不重复写日志）
            RescueSidebar.Attach(_config);

            AppendLogLine("[系统] 路径与设置已更新并保存。");
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        TxtLogs.Clear();
    }

    private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtLogs.Text))
        {
            Clipboard.SetText(TxtLogs.Text);
            MessageBox.Show("控制台日志已复制到剪贴板！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnRefreshWeb_Click(object sender, RoutedEventArgs e)
    {
        LoadEmbeddedBrowser(_runner.LastAuthenticatedUrl);
    }

    private async void LoadEmbeddedBrowser(string? specificUrl = null)
    {
        string? authenticated = !string.IsNullOrWhiteSpace(specificUrl)
            ? specificUrl
            : _runner.LastAuthenticatedUrl;

        if (string.IsNullOrWhiteSpace(authenticated))
        {
            AppendLogLine("[提示] 尚未捕获到 DSH 的认证地址，请先启动服务；");
            AppendLogLine("       直接访问裸端口会触发 authentication required（web 有浏览器信任围栏）。");
            return;
        }

        string url = authenticated;

        try
        {
            // 必须在设 Visibility 之前重建：已 Dispose 的实例连 Visibility 都不能碰
            EnsureWebViewInstance();

            PnlEmptyState.Visibility = Visibility.Collapsed;
            MainWebView.Visibility = Visibility.Visible;

            if (!await EnsureWebViewReadyAsync())
            {
                AppendLogLine("[提示] 内嵌内核不可用，已改用系统默认浏览器打开。");
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return;
            }

            MainWebView.CoreWebView2.Navigate(url);
            AppendLogLine($"[界面] 内嵌工作区正在连接加载: {url}");
        }
        catch (Exception ex)
        {
            AppendLogLine($"[错误] 加载内嵌视图失败: {ex.Message}");
        }
    }

    private void BtnQuickWeb_Click(object sender, RoutedEventArgs e)
    {
        string? url = _runner.LastAuthenticatedUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            AppendLogLine("[提示] 尚未捕获到认证地址，无法在外部浏览器打开。请先启动服务。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开浏览器: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnOpenProfile_Click(object sender, RoutedEventArgs e)
    {
        string path = _config.ConfigPath.Trim();
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? "";
        if (Directory.Exists(path))
        {
            Process.Start("explorer.exe", $"\"{path}\"");
        }
        else
        {
            var defaultProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            if (Directory.Exists(defaultProfile))
            {
                Process.Start("explorer.exe", $"\"{defaultProfile}\"");
            }
            else
            {
                MessageBox.Show("配置目录不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            TxtMaxIcon.Text = "□";
        }
        else
        {
            WindowState = WindowState.Maximized;
            TxtMaxIcon.Text = "❐";
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveCurrentUiToConfig();
        try { MainWebView.Dispose(); } catch { }
        _ = _runner.StopAsync();
        base.OnClosed(e);
    }
}
