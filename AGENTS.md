# 项目说明

本仓库包含两个主要项目：

- `BackDatabase`：负责执行数据库备份，并提供用于配置和管理自身的 Web 服务。
- `BackDatabaseManageServer`：用于集中管理多个 `BackDatabase` 实例的服务。

## 简称约定

- `BackDatabase` 简称为 `back` 项目。
- `BackDatabaseManageServer` 简称为 `server` 项目。
- 后续文档、讨论和任务描述中的“back 项目”与“server 项目”均采用上述含义。

## 项目关系

`BackDatabase` 是实际执行数据库备份的工作节点，每个实例维护并提供自身配置；`BackDatabaseManageServer` 是集中管理端，负责管理多个 `BackDatabase` 实例。

## 修改范围

- 与单个实例的数据库备份、实例自身配置或实例 Web 接口有关的代码，应修改 `back` 项目（`BackDatabase`）。
- 与多实例的发现、接入、集中配置、状态查看或统一管理有关的代码，应修改 `server` 项目（`BackDatabaseManageServer`）。
- 涉及两个项目的改动，应明确实例端与管理端之间的接口边界，并保持双方的数据结构和通信协议一致。
