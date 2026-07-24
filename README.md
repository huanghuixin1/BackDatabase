# BackDatabase

通过调用本机 `mysqldump` / `pg_dump`，按 `config/*.conf` 定时导出 SQL，并自动清理超出数量的旧备份。

## 依赖
- MySQL/MariaDB：安装客户端并保证 **`mysqldump` 在 PATH**
- PostgreSQL：安装客户端并保证 **`pg_dump` 在 PATH** （也就是环境变量）

Ubuntu/Debian：

```bash
apt install mysql-client
# 或 postgresql-client-xx
```

Windows：将 `mysqldump.exe` / `pg_dump.exe` 所在目录加入环境变量。

## 目录结构

```
BackDatabase/
  Program.cs
  env.conf.example        # 全局环境配置样例（复制为 env.conf）
  Models/                 # 配置模型
    BackupConfig.cs
    ConfigLoader.cs
    EnvConfig.cs
  Utils/
    EnvConfigLoader.cs    # 加载 env.conf
  Services/
    BackupRunner.cs       # 调用 dump 工具、裁剪旧文件
    BackupScheduler.cs    # 间隔 / 每日 UTC 调度
    PushNotifier.cs       # 备份失败时 HxPush 推送
  config/
    52hhx.com.conf.example
```

启动时：

1. 读取 **可执行文件目录下** 的 `env.conf`（JSON，可选；不存在则推送关闭）
2. 扫描同目录 `config/*.conf`（仅 `.conf`，`.example` 不加载）

## 全局环境配置 `env.conf`

复制 `env.conf.example` 为 `env.conf` 后填写：

```json
{
  "pushAddr": "http://127.0.0.1:5212",
  "pushKey": "your-app-key",
  "pushHwid": ""
}
```

| 字段 | 说明 |
|---|---|
| `pushAddr` | HxPush 服务地址（支持 `http(s)://` 或 `ws(s)://.../ws`，SDK 会规范为 HTTP 根） |
| `pushKey` | 已在 HxPush 服务端登记的 AppKey |
| `pushHwid` | 可选设备 ID；为空时回退为本机机器名，再回退为 `BackDatabase` |

`pushAddr` 与 `pushKey` 都非空时启用推送；备份**最终失败**（含一次自动重试后仍失败）时推送一条消息。推送异常只记日志，不影响备份主流程。

依赖本机旁的 `HxPushSdk` 工程（`D:\code\HxPush\HxPushSdk`）。

### 裁剪发布说明（半裁剪）

勾选「裁剪未使用的代码」时工程已按中间方案配置：

- `TrimMode=partial` + `JsonSerializerIsReflectionEnabledByDefault=true`：体积缩小，仍允许反射 JSON  
- `TrimmerRootAssembly`：`HxPushSdk` / `HxPushModel` 元数据保留  
- 本项目 `env.conf` 走源生成；推送 SDK 注入「源生成 + 反射回退」的 `JsonSerializerOptions`

重新发布后请把 `env.conf` 放到 **发布目录**（不是源码目录）。

## 配置（兼容原 Go 版）

| 键 | 说明 |
|---|---|
| `dbType` | `mysql` 或 `pgsql`（MariaDB 填 `mysql`） |
| `backtime` | 数字 = 每隔 N 分钟；`HH:mm` = 每天该 **UTC** 时刻 |
| `host` / `port` / `user` / `pwd` | 连接信息 |
| `dbs` | 逗号分隔数据库名 |
| `savedir` | 相对程序目录的保存路径，如 `/backup/` |
| `maxfiles` | 最大保留文件数，默认 180 |

示例 `config/local.conf`：

```ini
dbType=mysql
backtime=2
port=3306
host=127.0.0.1
dbs=ss,hhx
user=root
pwd=pwd
savedir=/backup/
maxfiles=50
```

每日固定时刻：

```ini
backtime=02:30
```

## 运行

```powershell
cd D:\code\BackDatabase\BackDatabase
copy config\52hhx.com.conf.example config\local.conf
# 编辑 local.conf 后：
dotnet run
# 发布
dotnet publish -c Release -o publish
```

Linux 后台：

```bash
nohup ./BackDatabase > /var/log/backdatabase.log 2>&1 &
```

修改配置后需**重启程序**。

## 行为说明

- 每个 `.conf` 一个后台任务（并行）
- 间隔模式：立即备份一次，再按分钟睡眠（对齐原 Go）
- 每日模式：约 40 秒轮询 UTC 时分，同一天只跑一次
- 备份前按修改时间删除超出 `maxfiles` 的最旧文件
- 失败删除残缺 SQL 后自动重试一次
- 备份最终失败时可通过 `env.conf` + HxPush 推送告警
- 支持 Ctrl+C 优雅退出

## 与 Go 版差异

- 使用 `Process` + 参数列表调用客户端（mysqldump 标准输出直写文件），减少 shell 注入风险
- 每日模式避免同一分钟重复执行
- 配置解析为自实现键值解析
