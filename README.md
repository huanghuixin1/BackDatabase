<img width="1086" height="731" alt="BackDatabase 管理界面" src="https://github.com/user-attachments/assets/5470ebe0-58a0-41e6-8051-cc1cf4d00fee" />

# BackDatabase

BackDatabase 是一个基于 .NET 10 的数据库备份系统，包含实际执行备份的 back 节点和集中管理多个节点的 server 管理端。

## 项目组成

| 项目 | 作用 | 默认地址 |
| --- | --- | --- |
| `BackDatabase` | back 节点：执行备份，提供节点 Web 控制台和 API | `http://节点地址:5080` |
| `BackDatabaseManageServer` | server：管理多个 back 节点、任务和程序更新包 | `http://server地址:5090` |

```text
BackDatabase.slnx
BackDatabase/                 # back 节点
BackDatabaseManageServer/     # server 管理端
libs/                         # 构建所需 DLL
.github/workflows/dotnet.yml  # GitHub Actions
```

## 功能

- 支持 MySQL、MariaDB、PostgreSQL 备份。
- 支持按分钟间隔或每天固定 UTC 时刻执行备份。
- 支持任务级和数据库级备份计划。
- 支持每个数据库单独设置最大保留文件数量。
- back 提供 Kestrel Web 控制台，用于维护备份任务、查看运行状态和备份文件。
- server 集中查看节点版本和在线状态，可手动刷新节点。
- server 可同时打开多个节点控制台，并可复制备份任务。
- server 可上传 zip 发布包并覆盖更新选中的节点。

## 前置条件

### .NET SDK

项目目标框架为 `.NET 10`，构建和开发环境必须安装 .NET SDK 10。

### 数据库客户端

back 通过本地命令行工具执行备份，必须将对应工具加入 `PATH`：

| 数据库 | 工具 |
| --- | --- |
| MySQL / MariaDB | `mysqldump` |
| PostgreSQL | `pg_dump` |

Linux 示例：

```bash
apt update
apt install -y mysql-client
# PostgreSQL 请安装对应版本的 postgresql-client
```

Windows 请将 `mysqldump.exe` 或 `pg_dump.exe` 所在目录加入系统 `PATH`，然后重启 back。

## 构建与发布

`libs/` 已包含项目需要的本地 DLL，不依赖开发机上其它项目的目录。

```bash
dotnet restore BackDatabase.slnx
dotnet build BackDatabase.slnx --configuration Release --no-restore
```

发布 back：

```bash
dotnet publish BackDatabase/BackDatabase.csproj --configuration Release --output publish/back
```

发布 server：

```bash
dotnet publish BackDatabaseManageServer/BackDatabaseManageServer.csproj --configuration Release --output publish/server
```

发布目录必须保留程序依赖文件、`web/`、`config/` 和 `env.conf.example`。

## Back 节点配置

back 默认监听 `http://0.0.0.0:5080`。首次部署时，在发布目录中复制 `env.conf.example` 为 `env.conf`：

```json
{
  "pushAddr": "http://127.0.0.1:5212",
  "pushKey": "your-app-key",
  "pushHwid": "",
  "pushGroup": "backDb",
  "webPassword": "change-this-password"
}
```

`webPassword` 保护节点 Web 控制台与 API；未配置密码时，API 仅允许本机回环地址访问。

备份任务保存在发布目录的 `config/*.conf`，也可以直接在 back Web 控制台创建和编辑。

| 字段 | 说明 |
| --- | --- |
| `dbType` | `mysql`、`mariadb` 或 `pgsql` |
| `backtime` | 分钟间隔，例如 `60`；或每天 UTC 时刻，例如 `02:30` |
| `host` / `port` / `user` / `pwd` | 数据库连接信息 |
| `dbs` | 逗号分隔的数据库名称 |
| `savedir` | 相对程序目录的备份路径，例如 `/backup/` |
| `maxfiles` | 任务默认最大保留文件数量 |
| `dbmaxfiles` | 可选的每库保留数量，例如 `app:30,analytics:90` |

示例：

```ini
dbType=mysql
backtime=60
host=127.0.0.1
port=3306
user=root
pwd=your-password
dbs=app,analytics
savedir=/backup/
maxfiles=90
dbmaxfiles=app:30,analytics:60
```

保存、修改、删除或恢复备份任务后立即生效。修改 `env.conf` 中的推送或 Web 密码后，需要重启 back。

### 运行 back

Linux 发布目录包含 `backdatabase.sh`：

```bash
chmod +x backdatabase.sh
./backdatabase.sh start
./backdatabase.sh status
./backdatabase.sh restart
./backdatabase.sh stop
```

Windows 可直接启动 `BackDatabase.exe`，或执行：

```powershell
dotnet BackDatabase.dll
```

## Server 管理端

server 默认监听 `http://0.0.0.0:5090`。首次部署时，在发布目录中复制 `env.conf.example` 为 `env.conf`：

```json
{
  "webPassword": "change-this-password"
}
```

在 server 中添加节点时，填写节点名称、back 控制地址（例如 `http://10.0.0.10:5080`）及该节点的 `webPassword`。server 通过 back HTTP API 管理节点，不直接读取节点磁盘。

## 在线更新

1. 为目标操作系统发布 back，并压缩发布目录为 `.zip`。
2. 在 server 的“程序更新”页面上传包。
3. 选择节点并执行覆盖更新。

更新过程会保护节点现有的 `config/`、`env.conf`、SQL 备份、日志、PID 和更新临时目录。Windows 更新日志为发布目录中的 `update.log`。

更新包必须与节点系统和运行时匹配，例如：

```text
BackDatabase-3.1.0-win-x64.zip
BackDatabase-3.1.0-linux-x64.zip
```

不要将 Windows 更新包部署到 Linux 节点，或将 Linux 更新包部署到 Windows 节点。

## GitHub Actions

`.github/workflows/dotnet.yml` 在 `main` 分支推送和 Pull Request 时执行：

```bash
dotnet restore BackDatabase.slnx
dotnet build BackDatabase.slnx --configuration Release --no-restore
```

当前解决方案没有测试项目，因此 workflow 仅执行还原与构建。

## 安全建议

- `env.conf` 含有密码，已被 `.gitignore` 忽略，只提交 `env.conf.example`。
- 为 back 和 server 分别设置高强度 `webPassword`。
- 不要将 5080 或 5090 端口直接暴露到不受信任网络。
- 仅允许受信任管理员上传和部署更新包。