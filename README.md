# DeepSeek Harness (DSH) 控制台 & 服务启动管理系统

为 **DeepSeek Harness (DSH)** 设计的独立桌面控制台：单文件便携分发、内嵌浏览器实时预览、会话全生命周期管理、独立救援 DSH。

---

## 📦 交付物

```text
dist-selfcontained/DshLauncher.exe      134.3 MB   ← 唯一文件，双击即用
```

**真·单文件便携**：自包含 .NET 9 运行时（含 NETCore / WindowsDesktop / AspNetCore 三套运行时包），
不含 `hostfxr.dll` / `hostpolicy.dll` 外置文件——superhost 已静态链接它们。
拷到任何 Windows x64 机器上都能直接运行，无需预装 .NET。

---

## 📂 目录结构

```text
DshLauncher/
│
├── 📖 README.md                      本文件
│
├── 🚀 dist-selfcontained/            交付物：单文件便携版
│   └── DshLauncher.exe
│
├── 💻 src/                           完整可修改源码
│   ├── DshLauncher.csproj            单文件发布配置 + 内嵌资产声明
│   ├── App.xaml(.cs)                 应用生命周期
│   ├── MainWindow.xaml(.cs)          主界面：工具栏 / 内嵌浏览器 / 控制台
│   ├── SessionsWindow.xaml(.cs)      会话管理器
│   ├── RescuePanel.xaml(.cs)         救援对话面板
│   ├── SettingsDialog.xaml(.cs)      路径与运行环境设置弹窗
│   ├── lib/                          WebView2 托管 + 原生 DLL（离线随包）
│   ├── Models/
│   │   └── LauncherConfig.cs         配置实体（路径 / 端口 / 实例 / 参数）
│   └── Services/
│       ├── ConfigService.cs          DSH 路径自动探测与 JSON 读写
│       ├── ProcessRunnerService.cs   进程树生命周期、日志流、auth token 捕获
│       ├── JobObjectService.cs       Job Object 防孤儿进程
│       ├── WebViewOptions.cs         WebView2 环境参数（调试端口）
│       └── EmbeddedAssets.cs         内嵌资产释放（脚本 / 救援快照）
│
├── 🧰 tools/                         内嵌进 exe 的脚本资产
│   ├── session-manager.mjs           会话管理 CLI（list/scan/archive/repair/purge）
│   ├── dsh-session-log.mjs           会话日志读写、seq 修复、结构体检
│   ├── test-session-repair.mjs       损坏矩阵测试（8 种损坏 / 179 断言）
│   ├── rescue-dsh.ps1                救援驱动（create/status/verify/run/repair）
│   ├── mine-sessions.mjs             跨会话经验挖掘
│   ├── distill-sessions.mjs          失败簇蒸馏
│   ├── diag-seq-gaps.mjs             seq 空洞诊断
│   └── repair-seq-gap.mjs            seq 空洞修复
│
├── 🛟 rescue/                        内嵌救援资产
│   ├── rescue-snapshot.zip           独立 DSH 完整快照（61.4 MB，主体无法启动时用）
│   ├── profile/                      救援 profile 构建输入
│   └── README.md                     救援机制说明
│
├── 📚 docs/
│   └── SESSION-MANAGER.md            会话管理器文档
│
├── ⚙️ deploy/
│   ├── publish-portable.ps1          一键重新生成单文件便携版
│   └── jobobject-probe.ps1           JobObject 生命周期探针
│
└── 🔧 .runtimepacks/                 离线自包含构建的运行时包（联网 NuGet 不可达时用）
    ├── feed/                         四套 nupkg：3 个 runtime pack + 1 个 apphost pack
    ├── version.txt                   当前选定的 .NET 版本（由生成器写入，发布脚本读取）
    └── build-packs.ps1               从本机 .NET 共享框架 / packs 合成全部四个包
```

---

## 🌟 核心功能

1. **单文件便携**：一个 exe 带走全部，含内嵌浏览器内核与救援 DSH 快照。

2. **内嵌浏览器 + 控制台分栏**：中间是 WebView2 实时页面，底部是可拖拽分栏的原生日志流。
   彻底解决早期换行折叠/竖排字符问题，支持自动滚动、清屏、一键复制。

3. **自动捕获 auth token**：DSH 启动时会打印带临时 token 的 URL。
   启动器从 stdout 正则捕获后，自动导航内嵌浏览器**并**同时打开系统浏览器，
   免去手动复制——直接解决 `dsh web authentication required` 问题。

4. **会话管理器（🗂 会话）**：枚举全部会话并分类（用户 / 子 agent / 导入 / 空壳 / 系统 / 已归档），
   可查看完整对话、归档、恢复、删除、分批清除；内置**损坏检测与自动修复**。
   它能识别 seq 重复、seq 乱序、未闭合 turn / step、无人应答的 tool call 等结构问题。

5. **独立救援 DSH（🛟 救援）**：不是一堆按钮，而是一个**极简聊天框**。
   后台跑一个完全独立的 DSH 实例（自带 `DSH_HOME`），主体起不来时可用它对话排障；
   会话修复也走这个通道，由它自己调用修复工具。

6. **弹窗式设置（⚙ 设置）**：自动探测本机 DSH 目录、`cordis.yml`、Node.js 运行时，
   也可手动指定；配置持久化为 JSON。

7. **进程树生命周期**：Job Object 绑定 `KILL_ON_JOB_CLOSE`，停止时连子孙进程一起清理，不留孤儿。

8. **启动权限级别**：可选 **普通 / 管理员 / SYSTEM** 三种身份启动。
   管理员走 UAC，SYSTEM 走**一次性计划任务**（启动后立即删除，不留自启）；
   两者都会把真实用户环境注入进去，否则会去找错误的 `~/.dsh`。
   因为提权进程**无法用管道读输出**，这两种模式改为「输出落文件 + 尾随」——
   **auth token 捕获照旧**。旁边可勾选**隐藏控制台**：勾选时子进程的控制台
   **完全融进本面板**、不弹任何黑框；取消勾选时用 `Tee` 同时写窗口与文件，
   两边都能看到。细节与代价见 [`docs/PRIVILEGE-LEVELS.md`](docs/PRIVILEGE-LEVELS.md)。

9. **可收起的日志面板**：`📋 日志` 一键收起底部控制台，把纵向空间全让给浏览器；
   面板上也有「⯆ 隐藏」。状态会被记住。

### 工具栏

| 控件 | 作用 |
|---|---|
| `启动` / `停止` | 启停 DSH 实例 |
| `权限` | 普通 / 管理员 / SYSTEM（启动前选择，自动记忆） |
| `隐藏控制台` | 是否显示 DSH 子进程的控制台窗口；勾选时输出全部融进本面板 |
| `📋 日志` | 显示 / 隐藏底部控制台面板（收起后浏览器占满可用空间） |
| `🗂 会话` | 会话管理器 |
| `🛟 救援` | 救援对话面板（可折叠侧栏） |
| `⚙ 设置` | 路径与配置弹窗 |
| `🔄 刷新` | 重载配置 |
| `🌐 浏览器打开` | 用系统浏览器打开当前地址 |
| `📁 配置目录` | 打开配置所在目录 |
| `自动加载` / `崩溃重启` | 启动即跑 / 异常退出自动拉起 |

---

## 🛠️ 二次开发与重新生成

* **直接使用**：双击 `dist-selfcontained\DshLauncher.exe`。
* **改代码**：用 Visual Studio / Rider / VS Code 打开 `src\DshLauncher.csproj`。
* **重新生成单文件版**：

  ```powershell
  pwsh -File deploy\publish-portable.ps1
  ```

  该脚本会清理 `src\obj`、`src\bin`，并以 `SelfContained=true` + 本地 feed 发布，产出
  `dist-selfcontained\DshLauncher.exe`。它会校验两件事：产物确实是**单个文件**，
  且**确实带上了应用图标**（从 exe 里比对 256px 图标图像，不是只看项目引用）。
  运行时包缺失时加 `-RebuildPacks` 从本机 .NET 共享框架重新合成。

* **探针**：`pwsh -File deploy\jobobject-probe.ps1`，退出码 0 即通过。

### 应用图标是自动的

图标由 `design\build-mascot-icon.mjs` 从 `design\mascot-src.png` 生成到
`src\DshLauncher.ico`（9 个尺寸）。构建期由 csproj 的 `EnsureAppIcon` 目标保证：

| 情况 | 行为 |
|---|---|
| 图标已存在 | 跳过，**不调用 node**，构建不受影响 |
| 图标丢失但素材在 | 自动重新生成 |
| 图标丢且生成不出来 | **直接中断构建** —— 宁可失败，也不产出一个没图标的 exe |

之所以要这道闸门：`src\DshLauncher.ico` 是生成产物，它一旦丢失，MSBuild 本身**不会报错**，
只会安静地产出一个无图标的 exe。重新生成图标可手动执行：

```powershell
node design\build-mascot-icon.mjs ico navy      # 生成 src\DshLauncher.ico
node design\build-mascot-icon.mjs show navy     # 生成各尺寸预览图
```

图标的素材来源与授权说明见 [`THIRD-PARTY.md`](THIRD-PARTY.md)。

### 构建前提

- .NET 9 SDK（`net9.0-windows`）
- `.runtimepacks\feed\` 中的**四套** nupkg：3 个 runtime pack（NETCore / WindowsDesktop / AspNetCore）
  加 1 个 **apphost pack**。apphost pack 里的 `singlefilehost.exe` 是单文件发布必需件，
  缺了会直接报 `NU1101 找不到包 Microsoft.NETCore.App.Host.win-x64`。
  全部由 `build-packs.ps1` 从本机已装的 .NET 合成，**构建过程不访问网络**；
  它会自动挑选「四件套齐全」的最高 9.0.x 版本，因此 .NET 服务更新不会让构建静默失效。

---

## 📝 说明

- 配置与内嵌资产释放到 `%LOCALAPPDATA%\DshLauncher\`（脚本在 `assets\`，救援环境在 `assets\rescue\`）。
- 会话日志格式为**多帧拼接的 zstd**（`session.jsonl.zstd`）。
  注意：Node 的 zstd 解压 API 只解**第一帧**——必须自己解析帧头按帧边界遍历，否则只能读到 100 多字节的头部。
- 多 agent 能力由 **DSH 本体的插件**提供（`@nanmicoder/dsh-agent-teams`），不内置于本启动器。
