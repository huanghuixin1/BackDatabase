# AGENTS.md — BackDatabase

面向在本仓库中工作的编码助手（Claude Code / Cursor / 其他 Agent）的项目说明。  
修改代码前请先读完本文与 `README.md`。

---

## 1. 项目是什么


| 项 | 值 |
|---|---|
| 语言 / 运行时 | C# / .NET 10（`net10.0`） |
| 项目类型 | 控制台可执行程序（`OutputType=Exe`） |
| 程序集名 | `BackDatabase` |
| 版本字符串 | `3.1-net`（对齐原 Go 3.1） |
| 原版参考 | `D:\code\backmysql\main.go`、`config/*.conf` |
| 解决方案 | `D:\code\BackDatabase\BackDatabase.slnx` |
| 工程目录 | `D:\code\BackDatabase\BackDatabase\` |
| 推送 SDK | `D:\code\HxPush\HxPushSdk`（项目引用） |

**不做的事：** 不内嵌数据库驱动做逻辑备份；不提供 Web UI；不热加载配置（改 conf / env.conf 必须重启）。

---

## 2. 目录与职责

```
BackDatabase/
  Program.cs                          # 入口：env.conf → 备份 conf → 调度 → Ctrl+C
  env.conf.example                    # 全局 JSON 环境配置样例（复制为 env.conf）
  Models/
    BackupConfig.cs                   # 单 conf 对应的配置模型
    ConfigLoader.cs                   # 解析 key=value .conf
    EnvConfig.cs                      # env.conf JSON 模型（pushAddr/pushKey/pushHwid）
  Utils/
    EnvConfigLoader.cs                # 启动时加载 env.conf（非 model）
  Services/
    BackupRunner.cs                   # 裁剪旧文件 + 调策略 + 起进程 + 重试 + 失败推送
    BackupScheduler.cs                # 间隔分钟 / 每日 UTC 调度循环
    PushNotifier.cs                   # HxPushSdk 封装；未配置则为空操作
    Strategies/
      IDatabaseBackupStrategy.cs      # 策略接口 + DumpCommand
      DatabaseBackupStrategyFactory.cs# 按 dbType 注册/解析策略
      MySqlBackupStrategy.cs          # mysql / mariadb → mysqldump
      PgSqlBackupStrategy.cs          # pgsql / postgres / postgresql → pg_dump
  config/
    *.conf.example                    # 样例（不加载）
    *.conf                            # 运行时配置（被加载；勿提交真实密码）
  README.md                           # 用户向文档（仓库根也可有一份）
  AGENTS.md                           # 本文件（Agent 向）
```

命名空间约定：

- 配置模型：`BackDatabase.Config`（文件在 `Models/`）
- 工具/加载器：`BackDatabase.Utils`（文件在 `Utils/`）
- 服务：`BackDatabase.Services`
- 策略：`BackDatabase.Services.Strategies`

外部依赖：

- `System.Text.Encoding.CodePages`：Windows 下 GBK 解码 dump 错误
- 项目引用 `..\..\HxPush\HxPushSdk\HxPushSdk.csproj`：备份失败消息推送

---

## 3. 运行时行为（改代码时必须保持）

1. **根目录 = `AppContext.BaseDirectory`**（exe 目录），不是 `Environment.CurrentDirectory`。  
   `env.conf`、`config/`、`savedir` 都相对该目录。
2. **启动顺序**：先 `EnvConfigLoader.Load`（`env.conf`），再 `ConfigLoader.LoadAll`（`config/*.conf`）。
3. **只加载 `config/*.conf`**，忽略 `*.example` / 其它扩展名。`env.conf.example` 也不加载。
4. **每个 conf 一个后台 Task**，互不阻塞。
5. **`backtime` 两种模式**（与 Go 一致）：
   - 能解析为 `double` 且 `> 0` → 间隔分钟：先备份再 `Delay`，循环。
   - 否则 `HH:mm` → 每日 **UTC** 定点；约 40 秒轮询；同一天只跑一次。
6. **备份失败**：删残缺 `.sql`，同一库自动重试 **一次**；**重试仍失败**（或无法重试的配置错误）才走 `PushNotifier`。
7. **清理顺序**：写新文件前先删 **0 字节空文件**，再按 `LastWriteTimeUtc` 删最旧直到 ≤ `maxfiles`。
8. **文件名**：`{db}_{yyyy-MM-dd__HH.mm.ss}.sql`（UTC，点代替冒号，兼容 Windows）。
9. **无任何 conf**：打印提示并以退出码 `1` 结束，不空转。`env.conf` 缺失不阻断启动。
10. **Ctrl+C**：`CancelKeyPress` 取消调度，优雅退出；取消过程中不发失败推送。
11. **推送失败只打日志**，不得中断备份调度循环。

配置键（兼容 Go conf，勿随意改名）：

| 键 | 含义 |
|---|---|
| `dbType` | 策略标识：`mysql`/`mariadb`、`pgsql`/`postgres`/`postgresql` |
| `backtime` | 间隔分钟 或 `HH:mm`（UTC） |
| `host` `port` `user` `pwd` | 连接信息 |
| `dbs` | 逗号分隔库名 |
| `savedir` | 相对 exe 的保存路径 |
| `maxfiles` | 最大保留文件数，默认 180 |

`env.conf`（JSON）：

| 字段 | 含义 |
|---|---|
| `pushAddr` | HxPush 服务地址（http/https 或 ws/wss，SDK 会规范化） |
| `pushKey` | AppKey；与 pushAddr 都非空才启用推送 |
| `pushHwid` | 可选设备 ID；为空则回退机器名 / `BackDatabase` |

---

## 4. 架构要点：策略模式

`BackupRunner` **禁止**再为数据库类型写 `switch` / 大段 `if`。  
扩展新库必须走策略：

```
conf.dbType
    → DatabaseBackupStrategyFactory.Resolve
    → IDatabaseBackupStrategy.BuildCommand
    → DumpCommand
    → BackupRunner.RunProcess
```

### 新增数据库类型（检查清单）

1. 在 `Services/Strategies/` 新增 `XxxBackupStrategy : IDatabaseBackupStrategy`。
2. 实现 `SupportedDbTypes`（小写别名列表）与 `BuildCommand`。
3. 在 `DatabaseBackupStrategyFactory.CreateDefault()` 中 `.Register(new XxxBackupStrategy())`。
4. 更新 `README.md` 与本文件的 dbType 表。
5. **不要**改 `BackupScheduler` 调度逻辑，除非需求明确要求。

`DumpCommand` 字段约定：

- `FileName` / `Arguments`：走 `Process.ArgumentList`，禁止拼 shell 字符串。
- `ExtraEnvironment`：如 `PGPASSWORD`。
- `RedirectStdoutToFile`：`true` = 工具写 stdout（mysqldump）；`false` = 工具自己写文件（pg_dump `--file`）。
- `DisplayCommand`：日志用，**密码必须脱敏**（`***`）。

---

## 5. 编码约定

- **中文注释**：类、公开方法、关键分支保持中文说明（项目已采用）。
- **Nullable / ImplicitUsings**：已开启；新代码遵循。
- **风格**：与现有文件一致——小而清晰的 sealed 类、显式中文日志、少依赖。
- **依赖**：尽量少第三方。当前：
  - `System.Text.Encoding.CodePages`（GBK 解码 mysqldump 错误）
  - 项目引用 `HxPushSdk`（备份失败推送）
  新增 NuGet 需有明确理由。
- **安全**：
  - 不要把真实密码 / pushKey 写进仓库、样例 conf、日志。
  - `env.conf`、`config/*.conf` 已在 `.gitignore`；只提交 `*.example`。
  - dump 参数用 `ArgumentList`，避免 shell 注入。
  - 不要实现“根据用户输入执行任意 shell”。
- **跨平台**：路径用 `Path.Combine` / `Path.GetFullPath`；`savedir` 先统一 `/` 再 `TrimStart('/')` 再拼接（见 `BackupConfig.ResolveSaveDir`）。
- **时区**：调度与文件名时间戳一律 **UTC**（与 Go 原版一致）。若用户要本地时区，需显式需求再改，并写清文档。

---


## 7. 改动决策指南

| 需求 | 改哪里 |
|---|---|
| 新数据库类型 | `Services/Strategies/*` + Factory 注册 |
| 改 mysqldump/pg_dump 参数 | 对应 `*BackupStrategy` |
| 改调度（间隔/每日） | `BackupScheduler` |
| 改 conf 格式/新键 | `ConfigLoader` + `BackupConfig` + 样例 conf + README |
| 改失败重试、删旧文件、失败推送时机 | `BackupRunner` |
| 改推送内容/HxPush 字段 | `PushNotifier` + `EnvConfig` |
| 改启动/退出 | `Program.cs` |
| 用户文档 | `README.md` |
| Agent 约定 | `AGENTS.md`（本文件） |

## 8. 明确不要做的事

- 不要把 `config/*.conf` / `env.conf`（含真实密码、pushKey）提交进 git。
- 不要在 `BackupRunner` 里按数据库类型堆 `switch`。
- 不要用 `UseShellExecute=true` 拼接用户可控命令。
- 不要把路径基准改成 `Environment.CurrentDirectory`（会破坏部署习惯）。
- 不要删除策略扩展点“顺便简化成 if-else”。
- 不要在未要求时引入 DI 容器、Web 框架、后台 Windows Service 宿主——保持单 exe 控制台，除非用户明确要求。
- 不要因推送失败中断备份调度；推送必须吞掉异常并打日志。

---

## 9. 快速自检（提交前）

- [ ] `dotnet build -c Release` 0 错误  
- [ ] 新 dbType 已注册且 `SupportedDbTypes` 有别名  
- [ ] 日志无明文密码 / 无明文 pushKey  
- [ ] 中文注释覆盖新类/关键逻辑  
- [ ] conf / env 样例与 README、AGENTS 已同步（若改了配置或行为）  
- [ ] 未破坏：相对 exe 的 `config/` 与 `env.conf`、UTC 调度、失败重试一次、空文件+maxfiles 裁剪、失败推送  

---

## 10. 相关路径速查

| 路径 | 说明 |
|---|---|
| `D:\code\BackDatabase\BackDatabase.slnx` | VS 解决方案 |
| `D:\code\BackDatabase\BackDatabase\` | 本工程根目录 |
| `config/*.conf` | 运行时备份配置（exe 旁） |
| `env.conf` | 全局环境配置（exe 旁，JSON） |
| `D:\code\HxPush\HxPushSdk` | 消息推送 SDK |
