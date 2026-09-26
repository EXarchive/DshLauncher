# DshLauncher

**DeepSeek Harness (DSH) 桌面控制台** —— 单文件便携，双击即用。

> ⬇️ **下载：[Releases](../../releases)** —— 取 `DshLauncher.exe`，单个文件，无需安装 .NET 运行时。

---

## 这是什么

为 [DeepSeek Harness](https://github.com/deepseek-ai) 设计的独立桌面启动器：把 DSH 的启动、
内嵌浏览、日志、会话管理与故障救援收进一个窗口。

## 核心能力

- **单文件便携** —— 一个 exe 带走全部（自包含 .NET 9 + WebView2 + 救援快照），
  拷到任何 Windows x64 机器直接运行，不需要预装任何运行时。

- **内嵌浏览器 + 可收起日志面板** —— 中间是 WebView2 实时页面，底部是可拖拽分栏的原生日志流；
  `📋 日志` 一键收起，把纵向空间全让给浏览器。

- **自动捕获 auth token** —— DSH 启动时会打印带临时 token 的 URL；启动器从输出里正则捕获后，
  自动导航内嵌浏览器**并**同时打开系统浏览器，免去手动复制，直接解决
  `dsh web authentication required`。

- **启动权限级别（普通 / 管理员 / SYSTEM）** ——
  管理员走 UAC，SYSTEM 走**一次性计划任务**（启动后立即 `/delete`，不驻留、不自启、不重启）。
  两者都会把真实用户环境注入进去，否则 SYSTEM 会话会去找错误的 `~/.dsh`。
  因为提权进程**无法用管道读输出**，这两种模式改为「输出落文件 + 尾随」——**auth token 捕获照旧**。
  另可勾选**隐藏控制台**：勾选时子进程控制台完全融进启动器面板、不弹任何黑框；
  取消勾选则用 `Tee` 同时写窗口与文件，两边都能看到。

- **会话管理器** —— 枚举全部会话并分类（用户 / 子 agent / 导入 / 空壳 / 系统 / 已归档），
  可查看完整对话、排序、归档、恢复、移入隔离区、**永久删除**；内置**损坏检测与自动修复**
  （seq 重复、seq 乱序、未闭合 turn/step、无人应答的 tool call）。
  移入隔离区可恢复，永久删除需二次确认。

- **独立救援 DSH** —— 一个极简聊天框，后台跑完全独立的 DSH 实例（自带 `DSH_HOME`）。
  主体起不来时可用它对话排障；会话修复也走这个通道。

- **进程树生命周期** —— Job Object 绑定 `KILL_ON_JOB_CLOSE`，停止时连子孙进程一起清理，
  不留孤儿进程占着端口。

## 环境要求

- Windows 10/11 x64
- 已安装 [DeepSeek Harness](https://github.com/deepseek-ai)（`dsh` 命令可用）
- Node.js

## 从源码构建

```powershell
pwsh -File deploy\publish-portable.ps1
```

需要 .NET 9 SDK。构建**不访问网络**：运行时包由 `.runtimepacks\build-packs.ps1`
从本机已装的 .NET 合成（含单文件发布必需的 apphost 包）。

## 许可

随附各组件遵循其各自许可（.NET / WebView2 / DeepSeek Harness）。
