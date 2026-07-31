# Project Memory

以后处理 back 项目时，先读本文件，再按需阅读仓库根 `AGENTS.md` 和 `README.md`。

本文件只记录长期有效的项目事实、不可破坏的约束、导航入口和已知问题；一次性任务细节不要写进来。

## 项目信息

- 项目目录：`BackDatabase/`，在仓库语境中简称 **back 项目**；负责实际执行数据库备份，并提供单实例配置管理 Web 服务。多实例集中管理属于 `BackDatabaseManageServer`，不要混改边界。
- 技术形态：C# / `.NET 10`，`OutputType=Exe`；同一进程承载备份调度和 ASP.NET Core/Kestrel Web 服务。
- 运行根目录固定为 `AppContext.BaseDirectory`（exe 所在目录），不是当前工作目录。`env.conf`、`config/`、`web/` 和相对备份目录都以此为基准。
- 启动顺序：注册代码页支持 -> 加载 `env.conf` -> 加载 `config/*.conf` -> 创建推送、执行器和调度器 -> 启动每个配置的后台任务 -> 启动 Web 服务。没有有效 `.conf` 时只跳过备份任务，Web 仍需运行。
- `env.conf` 是全局 JSON 配置，字段包括 `pushAddr`、`pushKey`、`pushHwid`、`pushGroup`、`webPassword`。缺失或解析失败不会阻断启动，而是回退为空配置。
- 支持的数据库类型：`mysql`、`mariadb` 使用 `mysqldump`；`pgsql`、`postgres`、`postgresql` 使用 `pg_dump`。对应命令必须存在于运行机器的 `PATH`。
- 调度模式：数字 `backtime > 0` 表示间隔分钟，启动后立即执行一次；`HH:mm` 表示每日 UTC 定点，约 40 秒轮询，同一天只执行一次。备份文件名时间戳同样使用 UTC。
- Web 实际监听 `http://0.0.0.0:5080`。配置 `webPassword` 时使用 `HxSimpleWebAuth` 的 Bearer Token 认证；未配置口令时，受保护 API 仅允许回环来源访问，远程请求返回 403。
- Web 只负责把配置写入磁盘，不热加载。新增、修改、删除任务配置或环境配置后必须重启进程才影响调度器。
- 外部依赖采用 DLL `Reference`：`HxPushModel`、`HxPushSdk`、`HxSimpleWebAuth`。其 `HintPath` 依赖本机相邻仓库的固定目录布局。
- 正式发布 profile 为 `win_x64` 和 `linux_amd64`：x64、self-contained、trimmed、非单文件。self-contained 只免除 .NET Runtime，不能替代数据库客户端工具。
- 当前仓库没有自动化测试项目，也没有 `global.json`；构建机需安装 .NET 10 SDK。

## 长期规则

- 不得把路径基准从 `AppContext.BaseDirectory` 改成 `Environment.CurrentDirectory`，也不要让 `env.conf`、`config/`、`web/` 的定位依赖启动目录。
- 保持配置启动快照语义，不擅自增加热加载。若需求明确要热加载，必须同时设计调度任务的增删改、并发和失败回滚。
- 新增数据库类型必须实现 `IDatabaseBackupStrategy`，返回 `DumpCommand`，并在 `DatabaseBackupStrategyFactory.CreateDefault()` 注册；不要在 `BackupRunner` 中堆数据库类型分支。
- 数据库备份命令必须通过 `ProcessStartInfo.ArgumentList` 传参，不得拼接用户输入后交给 shell。`DisplayCommand` 和日志中的密码、Push Key、Web 口令必须脱敏。`web/AppEntry.cs` 的 Windows 自重启为受控例外，它通过 `cmd /c start` 脱离父进程；修改时必须保持参数转义，且不得引入 Web 用户可控参数。
- 保持调度语义：间隔模式立即首跑；每日模式使用 UTC 且每日最多一次；取消时终止 dump 进程树、删除残缺文件，不重试、不发送失败通知。
- 单个配置解析失败不得影响其他配置；单次备份异常不得终止调度循环；推送失败只记录日志，不得反向中断备份。
- 备份失败时删除残缺 SQL，同一数据库只自动重试一次；重试仍失败或遇到不可重试的配置错误时才发送失败通知。
- 清理顺序保持为：写新文件前先删 0 字节文件，再按 `LastWriteTimeUtc` 删除最旧文件。调整 `maxfiles` 行为前先看“已知问题”。
- 失败推送正文顺序固定为：`备份失败 -> 数据库 -> 备份计划 -> 配置 -> 主机 -> 类型 -> 原因`，增删字段时不要擅自重排。
- Web 的配置文件名、配置目录和保存目录边界校验不能弱化；机密字段只返回“是否已配置”，空值表示保留，清除必须显式指定。
- Web 认证统一使用 `HxSimpleWebAuth`。宿主只负责 `HttpContext` / `HttpRequestData` 和 `ApiResponse` 的边界映射，不在 back 项目重新实现口令比较、Token、IP 绑定或锁定策略。
- `webPassword` 为空时的回环限制是安全边界。若接入反向代理，必须明确可信代理和真实客户端 IP 规则，不能默认把代理的回环地址当成最终客户端。
- 服务只提供 HTTP。需要远程管理时必须由防火墙限制来源，并在可信反向代理处终止 TLS；不要直接把 5080 暴露到不可信网络。
- 保持半裁剪 JSON 策略：本项目类型优先使用 `AppJsonContext`；保留 `JsonSerializerIsReflectionEnabledByDefault=true` 及 `HxPushSdk`、`HxPushModel` 的 `TrimmerRootAssembly`，除非第三方反射路径已被完整替换和验证。
- 发布后必须检查 `web/`、`config/*.example`、`env.conf.example`、`HxPushModel.dll`、`HxPushSdk.dll`、`HxSimpleWebAuth.dll` 是否在产物中，不能用旧发布目录覆盖新产物。
- 修改配置格式、运行行为或部署方式时，同步更新根 `README.md`、样例配置和本文件。
- 提交前至少执行：

  ```powershell
  dotnet build .\BackDatabase\BackDatabase.csproj -c Release
  dotnet publish .\BackDatabase\BackDatabase.csproj -p:PublishProfile=win_x64
  dotnet publish .\BackDatabase\BackDatabase.csproj -p:PublishProfile=linux_amd64
  ```

## 重要文件

| 文件 | 职责 |
|---|---|
| `Program.cs` | 进程入口、根目录、加载顺序、对象装配、Kestrel 地址、停止联动 |
| `Models/BackupConfig.cs` | 单任务配置模型、保存目录解析 |
| `Models/ConfigLoader.cs` | `config/*.conf` 扫描、兼容格式解析、计划与默认值 |
| `Models/EnvConfig.cs` | `env.conf` 数据模型、Web 认证是否启用 |
| `utils/EnvConfigLoader.cs` | 启动时加载全局环境配置及失败回退 |
| `utils/AppJsonContext.cs` | System.Text.Json 源生成与第三方反射回退配置 |
| `Services/BackupScheduler.cs` | 每配置独立调度、间隔模式、每日 UTC 模式 |
| `Services/BackupRunner.cs` | 文件清理、逐库备份、进程生命周期、重试、取消、失败通知时机 |
| `Services/Strategies/IDatabaseBackupStrategy.cs` | 数据库策略接口和 `DumpCommand` 契约 |
| `Services/Strategies/DatabaseBackupStrategyFactory.cs` | 数据库类型注册与解析入口 |
| `Services/Strategies/MySqlBackupStrategy.cs` | MySQL / MariaDB 的 `mysqldump` 参数 |
| `Services/Strategies/PgSqlBackupStrategy.cs` | PostgreSQL 的 `pg_dump` 参数与 `PGPASSWORD` |
| `Services/PushNotifier.cs` | HxPush 初始化、失败消息格式、推送异常隔离 |
| `web/ConfigWebService.cs` | Web 认证边界、配置 API、校验、机密保留、原子写入 |
| `web/AppEntry.cs` | Web 发起的跨平台进程重启 |
| `web/app.js` | 管理页面状态、Bearer Token、配置编辑和重启轮询 |
| `BackDatabase.csproj` | 目标框架、裁剪策略、静态资源复制、外部 DLL 引用 |
| `Properties/PublishProfiles/*.pubxml` | Windows/Linux 正式发布参数 |

## 已知问题

- 2026-07-31：正式发布产物启动时曾出现 `MissingMethodException`，缺少 `EndpointRouteBuilderExtensions.MapPost(..., RequestDelegate)`。异常位于 `ConfigWebService.Configure` 的认证路由注册；尚未完成发布产物级根因验证，后续修复必须用正式 profile 重新发布并直接启动产物验证，不能只以 `dotnet build` 通过为准。
- 项目元数据版本为 `3.1.0`，`Program.cs` 启动日志仍硬编码输出 `1.5`，版本来源不一致。
- 根 `README.md` 仍有过时内容：声称 Web 仅监听本机、遗漏 `webPassword`/`HxSimpleWebAuth`、数据库别名和 Web 重启能力。
- `Properties/launchSettings.json` 使用 62569/62570，而程序硬编码 5080；IDE 自动打开地址可能不正确。
- `env.conf.example` 当前示例口令为 `123`，但 Web 保存校验要求至少 6 位；启动加载器不会执行该长度校验。
- `maxfiles` 不是写入完成后的严格上限：清理发生在本轮写文件之前，因此本轮结束后可能暂时超过上限。
- 清理逻辑枚举保存目录中的所有文件，并删除其中所有 0 字节文件，而非只处理本程序生成的 `.sql`；不要与其他程序共享备份目录。
- 手工 `.conf` 解析比 Web API 校验宽松：坏行可能被跳过、非法 `maxfiles` 回退 180、`HH:mm` 可接受多余分段；两条输入路径存在规则漂移。
- `postgres`、`postgresql` 省略端口时会得到默认 3306，只有 `pgsql` 默认 5432。
- 手工 `.conf` 的 `savedir` 只做规范化，可能通过 `..` 逃出程序目录；只有 Web API 保存路径会强制限制在程序目录内。
- Web 监听所有网卡且只提供 HTTP；“记住访问口令”会把口令和 Token 明文写入浏览器 `localStorage`。远程部署必须额外做网络隔离和 TLS。
- 构建依赖仓库外 DLL 的固定相对路径，干净机器或 CI 不会自动取得依赖。当前没有自动化测试覆盖调度、配置解析或 Web 认证。

## 进度记录

- 2026-07-31：按 Project Memory 模板重新梳理本文件，以当前源码为准补齐运行契约、Web 认证、发布要求和已知问题。
- 2026-07-30：Web 认证迁移到 `HxSimpleWebAuth`，前端改用 Bearer Token，并保留 `/api/session` 兼容接口。
- 当前已支持 MySQL、MariaDB 和 PostgreSQL 多个别名；数据库差异通过策略模块隔离。
- 当前 Web 管理页支持配置 CRUD、环境配置、状态查询和进程内重启；配置写入后仍需重启才影响调度器。
