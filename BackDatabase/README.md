# BackDatabase

从 `D:\code\backmysql`（Go）移植的 .NET 版数据库定时备份工具。

通过调用本机 `mysqldump` / `pg_dump`，按 `config/*.conf` 定时导出 SQL，并自动清理超出数量的旧备份。

## 依赖

- .NET 10 运行时/SDK（项目目标框架 `net10.0`）
- MySQL/MariaDB：安装客户端并保证 **`mysqldump` 在 PATH**
- PostgreSQL：安装客户端并保证 **`pg_dump` 在 PATH**

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
  Models/                 # 配置模型与解析
    BackupConfig.cs
    ConfigLoader.cs
  Services/
    BackupRunner.cs       # 调用 dump 工具、裁剪旧文件
    BackupScheduler.cs    # 间隔 / 每日 UTC 调度
  config/
    52hhx.com.conf.example
```

启动后扫描 **可执行文件目录下** 的 `config/*.conf`（仅 `.conf`，`.example` 不加载）。

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
- 支持 Ctrl+C 优雅退出

## 与 Go 版差异

- 使用 `Process` + 参数列表调用客户端（mysqldump 标准输出直写文件），减少 shell 注入风险
- 每日模式避免同一分钟重复执行
- 配置解析为自实现键值解析
