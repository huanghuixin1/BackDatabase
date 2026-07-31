# BackDatabaseManageServer 初版

server 通过 HTTP 管理已登记的 BackDatabase 节点。server 与 back 使用同一个 `HxSimpleWebAuth` Bearer 登录流程。

## 运行

复制示例配置并设置 server 自身的管理口令：

```powershell
Copy-Item .\BackDatabaseManageServer\env.conf.example .\BackDatabaseManageServer\env.conf
# 编辑 env.conf 中的 webPassword
dotnet run --project .\BackDatabaseManageServer
```

server 从可执行文件同目录的 `env.conf` 读取 `webPassword`，默认监听 `http://0.0.0.0:5090`。未配置口令时，只允许本机访问 API。

节点信息保存在程序目录的 `nodes.json`，节点口令使用 ASP.NET Core Data Protection 加密，密钥保存在 `data-protection-keys`。

## 主要接口

- `POST /api/auth/login`：使用 `{ "key": "..." }` 登录 server。
- `GET|POST|PUT|DELETE /api/nodes`：查询、添加、修改、删除节点。
- `GET /api/nodes/{id}/status`：代理 back 的 `/api/status`。
- `GET /api/nodes/{id}/configs`：代理 back 的 `/api/configs`。
- `GET|PUT /api/nodes/{id}/environment`：代理 back 的环境配置接口。
- `POST /api/nodes/{id}/restart`：代理 back 的重启接口。
- `POST|PUT|DELETE /api/nodes/{id}/configs...`：代理 back 的配置写入和删除接口。

节点的 `baseUrl` 应填写 server 可访问的 back 地址，例如 `https://back-01.example.internal`。不要把 back 的明文 HTTP 端口直接暴露到公网。
