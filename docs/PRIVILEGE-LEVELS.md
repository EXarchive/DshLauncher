# 启动权限级别（普通 / 管理员 / SYSTEM）

> 启动器工具栏「权限」下拉 + 「隐藏控制台」勾选。
> 实现：`src\Services\PrivilegeLauncher.cs`、`src\Models\PrivilegeLevel.cs`。
> 参考实现在桌面：`SYSTEM权限管理器.ps1`、`OneDrive\Desktop\DSH管理员终端.cmd`。

---

## 一、三种级别各自做了什么

| 级别 | 机制 | 输出读取 | 生命周期 |
|---|---|---|---|
| **普通** | 直接 `Process.Start`，与启动器同权限 | **管道**（实时） | 纳入 **Job Object**，启动器被强杀时子进程一起走 |
| **管理员** | `Start-Process -Verb RunAs`（UAC） | 日志文件 + 尾随 | 不在 Job 里，靠提权脚本按端口/PID 结束 |
| **SYSTEM** | 一次性计划任务 `schtasks /create /ru SYSTEM /rl HIGHEST` → `/run` → **立即 `/delete`** | 日志文件 + 尾随 | 同上；任务不驻留、不自启、不重启 |

### 为什么提权模式读不了管道

这是**平台的硬限制**，不是设计取舍：

- 要弹 UAC 就必须 `UseShellExecute = true`；
- 而 `UseShellExecute = true` 时 **`RedirectStandardOutput` 直接失效**（.NET 会抛异常或静默丢弃）。

SYSTEM 计划任务同理——进程根本不由我们创建，自然没有它的管道。

所以提权模式改成：**子进程把输出写进文件，启动器尾随该文件**。
这不是降级方案：**只有这样才能继续捕获 DSH 打印的 auth token URL**，而那是启动器存在
的主要理由。丢了它，这个功能就没有意义。

尾随实现要注意的细节（`PrivilegeLauncher.Pump`）：

- 用 `FileShare.ReadWrite` 打开，否则会与正在写入的子进程互锁；
- 保留**未完成的行**（没有换行符的尾巴），否则一行分两次写入会被记成两条、或记成半截；
- 文件变小（被截断/轮转）时位置归零重读；
- 停止尾随前做一次**收尾抽取**，否则进程刚死时最后几行会丢。

---

## 二、SYSTEM 会话缺环境变量（必须注入）

SYSTEM 会话里**没有** `USERPROFILE` / `DSH_HOME` / `PATH`。缺了它们 `dsh` 会去找一个
错误的 `~/.dsh`，node 甚至不在 PATH 上。

所以启动前把真实用户环境写进 `launch.json`，由 `run-dsh.ps1` 还原：

```powershell
$env:USERPROFILE = <真实用户的 USERPROFILE>
$env:DSH_HOME    = <真实用户的 DSH_HOME>
$env:PATH        = <真实 PATH> + ";" + $env:PATH
```

**实测证据**：以 SYSTEM 启动后，从外部用 WMI 查该 node 进程的令牌是
`NT AUTHORITY\SYSTEM`；而进程内 `$env:USERPROFILE` 是 `C:\Users\Administrator`。
两者都对——身份是 SYSTEM，环境是用户的。

---

## 三、隐藏控制台 / 把子进程控制台融进启动器

「隐藏控制台」控制的是 **DSH 子进程的控制台窗口**：

- **勾选（默认）**：不显示窗口，输出**全部融进启动器的控制台面板**。
  提权 / SYSTEM 路径用 `wscript.exe` + VBS（`Run ..., 0, False`，第二个参数 0 即隐藏窗口）
  间接启动，避免那个"关不掉、一关就把 DSH 也关了"的黑框。
- **取消勾选**：开一个真实控制台窗口，**同时**输出仍然进启动器面板。

### 为什么"取消勾选"需要 Tee

一个输出流只能去一个地方。这里是硬约束，不是取舍：

- 把子进程 stdout **重定向到管道** → 启动器能捕获，但**控制台窗口是空的**；
- **不重定向** → 窗口里有输出，但**启动器收不到 auth token**，内嵌浏览器就不会自动跳转。

所以取消勾选时走 `Tee-Object`：同时写**控制台**和**文件**，启动器尾随那个文件。
两条路都实测过：

| 模式 | 机制 | 窗口 | 启动器捕获 |
|---|---|---|---|
| 勾选（默认） | 提权→文件尾随；普通→管道 | 无 | ✅ |
| 取消勾选 | `Tee-Object` → 控制台 + 文件尾随 | 有，**能看到实时输出** | ✅ |

> **编码坑**：Windows PowerShell 5.1 的 `Tee-Object` / `Out-File` 默认写 **UTF-16LE（带 BOM）**。
> 而 BOM 只在文件开头，尾随器每轮又从中间位置新建 `StreamReader`——后一轮没有 BOM 可看，
> 就退回 UTF-8，把 `tick` 读成 `t i c k`（字符间夹着 `0x00`）。
> 所以 `FileTailer` 会在文件头**嗅一次 BOM 并记住编码**；文件还没写入内容时**不急着拍板**
> （先创建后写入很常见，此刻定成 UTF-8 会把稍后的 UTF-16 内容读错）。

> SYSTEM 例外：它的窗口在**会话 0**，本来就看不见，勾不勾选都一样。

---

## 四、代价与边界（必须知道）

1. **提权模式没有 Job Object**。提权进程由 UAC 或计划任务创建，不在启动器的作业里。
   停止改由提权脚本负责：**先按端口杀，再按 `node.pid` 用 `taskkill /T /F` 杀进程树**。
   按端口优先，因为即使 PID 文件没写成（host 脚本中途死了）它也是对的。

2. **停提权实例可能要再弹一次 UAC**。启动器自身没提权时，结束管理员 / SYSTEM 进程需要
   提权。**若启动器本身就是管理员启动的，则一次 UAC 都不会弹** —— 界面上会直接告诉你
   当前启动器是什么权限。

3. **提权模式没有崩溃自启**。没有可订阅 `Exited` 的 `Process` 对象，改为轮询端口存活；
   端口掉了就标记为已停止（否则界面会永远停在「运行中」）。

4. **启动器自身权限决定了体验**：

   | 启动器权限 | 选「管理员」 | 选「SYSTEM」 |
   |---|---|---|
   | 普通用户 | 弹一次 UAC | 弹一次 UAC |
   | 已是管理员 | **不弹** | **不弹** |

5. **计划任务是「一次性」的**。创建 → 运行 → 启动后立即删除。实测确认任务列表里
   零残留 —— 不会开机自启，也不会在崩溃后偷偷重启。

---

## 五、生成的文件

全部落在 `%LOCALAPPDATA%\DshLauncher\elevated\`：

| 文件 | 作用 |
|---|---|
| `launch.json` | 启动参数 + 注入的环境。用文件传参是为了避开计划任务命令行里的空格/引号地狱 |
| `run-dsh.ps1` | 还原环境 → 释放端口 → 启动 node 并重定向到日志 → 写 `node.pid` |
| `run-dsh.vbs` | 隐藏窗口的启动器（只在勾选「隐藏控制台」时被用到） |
| `privileged-launch.ps1` | `-Mode admin` / `-Mode system`，负责 UAC 后的实际启动与计划任务生命周期 |
| `stop-dsh.ps1` | 按端口 + PID 结束实例 |
| `dsh.log` / `dsh.err.log` | 被尾随的输出 |
| `node.pid` | 供停止时精确定位 |

> 生成脚本**一律保持纯 ASCII**：Windows PowerShell 5.1 会把没有 BOM 的 `.ps1` 按 ANSI 读，
> 一句中文注释就足以让整个脚本损坏。
