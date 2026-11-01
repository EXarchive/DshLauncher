# DSH 会话管理（启动器能力）

> 面向 DshLauncher 的会话检测 / 查看 / 归档 / 删除 / 修复能力说明。
> 逻辑实现在 Node（`tools\`），界面在 WPF（`src\SessionsWindow.*`）。

---

## 一、为什么逻辑放在 Node 而不是 C#

会话日志是 **zstd 串联帧容器**。.NET 没有内置 zstd，本机又无法访问 NuGet；Node 内置了
zstd。更关键的是：**启动器绝不能与帧读取器对"这个会话里有什么"给出不同答案**，所以只有
一份实现（`tools\dsh-session-log.mjs`），界面只负责渲染。

---

## 二、会话日志的真实结构

```
~/.dsh/sessions/<URL 编码的工作区>/
├── session-<uuid>/        用户会话           ← 只有这一类属于用户的会话列表
├── <裸 uuid>/             子 agent 会话
├── import-session-*/      导入的会话
└── _no-cwd/<name>/        系统伪会话（如 team-board）
```

**子 agent 会话的识别**：目录名是裸 UUID，且日志中含 `subagent/descriptor` 事件
（`data.label` 即子 agent 的任务名，`data.mode` 为 `continuable` / `one-shot`）。

### 归档状态是 DSH 自己的，不是我们另存的

DSH 会隐藏已归档会话，记录在：

```
~/.dsh/storages/workspace.json
  └── global.archivedSessionIds : ["session-...", ...]
  └── global.workspaceIds       : [uuid, ...]
  └── tables.workspaces[uuid]   : { path, title, sessionIds: [...] }
```

所以「隐藏一个会话」= 编辑那个列表，**让 DSH 自己去隐藏** —— 不引入可能与现实漂移的影子状态。

> 「未分组」的含义也来自 DSH 客户端源码：`"group.ungrouped": "未分组"`，
> 指**工作区被移除后其会话落入的分组**。判定依据是会话是否出现在任何工作区的
> `sessionIds` 里。

### 帧边界必须自己算

```js
// 错：压缩载荷里会出现 0x28B52FFD（实测一个 3.5MB 日志含 9946 帧，另有大量巧合字节串）
buf.indexOf(Buffer.from([0x28,0xb5,0x2f,0xfd]))

// 对：解析帧头（FHD → Window_Descriptor → Dictionary_ID → Frame_Content_Size），
//     再逐个走 3 字节块头直到 Last_Block，最后按标志位加 4 字节校验和
frameSize(buf, offset)
```

**Node 的 `zstdDecompressSync` 和 `createZstdDecompress` 都只解第一帧** —— 整个文件解出来
只有 172 字节（会话头）。必须逐帧解压。

---

## 三、命令

```powershell
node tools\session-manager.mjs <命令> [选项]
```

| 命令 | 作用 |
|---|---|
| `list [--all] [--include-subagent] [--include-archived] [--no-index]` | 会话清单（默认只显示未归档的用户会话） |
| `show --id <id> [--tail N] [--user-only]` | 渲染对话（`--user-only` 剔除子 agent 注入的轮次） |
| `scan` | 健康巡检：逐帧走一遍，报告损坏位置与丢失比例 |
| `select --select <类>` | **预览**某类会命中哪些会话（不修改） |
| `archive` / `unarchive --select <类>` | 隐藏 / 取消隐藏（写 DSH 自己的归档列表） |
| `delete --select <类>` | 移入隔离区（**可恢复**） |
| `restore --manifest <path>` | 从隔离区恢复 |
| **`purge --id <id> ...`** | **永久删除活会话**（不可恢复；见 §4.2） |
| `purge --kind <类>` / `--batch <名>` / `--all` | 永久销毁（**只销毁隔离区内的副本**） |
| `repair --select damaged` | 修复：丢弃不可读的尾部，原始文件改名保留 |
| `quarantine-list` | 列出隔离批次 |

**选择器**：`subagent` · `probe` · `imported` · `system` · `unlisted` · `archived` · `empty` · `all`

所有批量命令都支持 `--dry-run`，任何破坏性操作都先预览数量再确认。

---

## 三之二、性能：元数据索引

**问题**：`list` 曾经对每个会话调用一次 `readSession()`，也就是把**全部**日志逐帧解压再
逐行 `JSON.parse`。本机 86 个会话、187 MB，一次 `list` 要 **18.8 秒**——而 `archive` 本身
只要 60 ms。界面每次改完会话都会刷新列表，于是"修改会话很慢"的真实来源是刷新，不是修改。

雪上加霜的是裸 UUID 目录会被**解压两遍**：`classify()` 为了确认是不是子 agent 读一次，
`describe()` 又读一次。

**做法**：把「只能靠解码日志才知道的东西」（`kind` / `title` / `cwd` / `counts` / `health` /
`empty`）缓存到

```
%LOCALAPPDATA%\DshLauncher\session-index.json      （可用 DSH_SESSION_INDEX 覆盖）
```

缓存键是 `(logPath, size, mtimeMs)`。字节没变 → 结论不可能变；文件一改 → 只有它失效。

| | 改动前 | 改动后 |
|---|---|---|
| 修改后刷新（缓存已建立） | 18 821 ms | **97 ms** |
| 重复打开列表 | ~19 s | 107 ms |
| 首次建立索引（一次性） | — | 17.8 s |

要点：

- **索引只是缓存，永远不是事实来源**。stat 不匹配、JSON 损坏、版本号变化、文件缺失，
  任何一条都会静默回退到解码，因此它不可能给出与实际不同的答案。
- `--no-index` 强制全量解码（用于验证缓存与真实结果一致）。
- 版本号 `INDEX_VERSION` 变更即整体失效，字段结构改动时递增。
- 写入走临时文件 + `renameSync`，并只允许读；目录已消失的条目在下一次遍历时清除。
- `archive` / `unarchive` 只写 `workspace.json`、**不动日志**，所以改完刷新连一次解码都不需要。

---

## 四、安全设计（三条不可让步的原则）

### 1. 删除 = 搬运，不是抹除

`delete` 把会话目录**移动**到 `~/.dsh/supervision/session-quarantine/batch-<时间戳>/`，
并写 `manifest.json`（记录来源、去向、字节数）。`restore` 可完整恢复。
对隔离区的 `purge --kind/--batch/--all` 是**独立的第二步**，且**只销毁隔离区里的副本**。

> 一个打错一个字就能毁掉几个月对话的工具，必须可恢复。

### 2. 永久删除是独立动词，不是一个 `--force` 开关

`purge --id` 是真删除，因此它**不是** `delete` 的一个开关，而是另一个动词——开关会被
顺手带上，动词不会。它一次清理三样东西，少清理任何一样都会留下不同的 bug：

| 清理对象 | 不清会怎样 |
|---|---|
| 会话目录 | —— |
| 同一 id 的隔离区副本 | "先 delete 再 purge" 后会留下一份**自相矛盾**、还能被 `restore` 搬回来的副本 |
| `archivedSessionIds` 里的该 id | DSH 继续指向已不存在的目录 → 也就是让会话列表**看起来丢了条目**的那类陈旧引用 |

界面侧对应「🗑 永久删除」按钮。确认方式是一个 **Yes/No 对话框**，框里列出将被销毁的会话、
总数与"不可恢复"的说明——不可恢复的动作不该只靠一次点击，但也不该逼用户手抄一串 id 或批次名
（那只会训练出盲目的确认习惯，反而降低警惕）。隔离区的「永久销毁所选批次」同样用 Yes/No。

### 3. 修复保留证据

`repair` 只丢弃**不可读的尾部**——那部分在原文件里根本不存在，无法重建。
原文**改名保留**为 `session.jsonl.zstd.corrupt-<日期>-<时间戳>`，不删除。

### 4. 写 DSH 状态文件用「临时文件 + 改名」

`workspace.json` 是 DSH 判断"什么该隐藏"的唯一依据。写入走
`写临时文件 → renameSync`，中断也不会毁掉它。改动前另存 `.bak-<时间戳>`。

---

## 五、known 数据不一致（工具会报告，不静默忽略）

| 现象 | 含义 |
|---|---|
| **陈旧归档引用** | `archivedSessionIds` 里有 ID，但磁盘上目录已不存在 |
| **未列入工作区** | 磁盘上有会话，但不在任何工作区的 `sessionIds` 中（DSH 显示为「未分组」） |

两者都会在 `list` 的 `totals.unlisted` 与 `staleArchiveRefs` 里报出来，因为它们是
"看起来像数据丢失"的真正原因。

---

## 六、经验挖掘（附带能力）

```powershell
node tools\mine-sessions.mjs --out _analysis      # 遍历全部会话抽取条目
node tools\distill-sessions.mjs --in _analysis    # 失败签名聚类 → 教训
```

产出：`_analysis\{session-index.json, items.jsonl, lessons.md, mine-report.txt, EXPERIENCE.md}`

**方法**：教训**必须来自聚类**（单次报错不算规则），且**保留原文**以便证伪。
判定**精度优先**——一次 `grep` 命中 "error" 不是失败（收紧判定后失败条目从 3660 降到 2415）。

已由此得到的头号结论：约 **44%** 的工具失败是「参数不合规重试风暴」，
单会话内同一错误最多重复 **321 次**——持续烧 token 而零进展，正是「loop 断掉」的典型形态。

---

## 七、当前状态（清理记录）

```
清理前: 可见 68 个会话（含 53 个自动化探针 + 8 个子 agent + 空壳）
清理后: 可见 11 个 —— 全部是用户真实对话
        已归档 117 个 · 损坏 0 个 · 129 个会话日志逐帧校验通过
```

`workspace.json` 改动前已备份为 `workspace.json.bak-<时间戳>`。
