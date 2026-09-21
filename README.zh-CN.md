# Hamster

<div align="center">

基于现代 Web 技术构建的个人记账助手

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Vue.js](https://img.shields.io/badge/Vue.js-3.5-4FC08D.svg)](https://vuejs.org)
[![TypeScript](https://img.shields.io/badge/TypeScript-6.0-3178C6.svg)](https://www.typescriptlang.org)

[English](README.md) | [中文](README.zh-CN.md)

</div>

## 简介

Hamster 是一个全栈个人记账助手，旨在帮助您轻松管理财务。该项目采用 Vue 3 和 TypeScript 构建现代化、响应式的前端界面，后端使用高性能的 ASP.NET Core API。

## 项目结构

```
Hamster/
├── api/     # ASP.NET Core 后端（.NET 10 + Minimal API）
├── ui/      # Vue 3 前端（TypeScript + Vite）
└── doc/     # Logo 与图标资源
```

## 技术栈

### 后端 ([api/](api/))
- **框架**: ASP.NET Core 10.0（.NET 10）
- **API 风格**: Minimal APIs，端点模块通过 `IEndpoint` 约定自动注册
- **OpenAPI**: 开发环境通过 `AddOpenApi()` / `MapOpenApi()` 暴露文档（`/openapi/v1.json`）
- **数据访问**: SqlSugar ORM，支持 **SQLite**（默认）与 **PostgreSQL**
- **认证**: JWT 令牌（有效期 1 天），密码以 PBKDF2 哈希存储
- **分层**: `Config/` · `Data/` · `Security/` · `Services/` · `Endpoints/`

### 前端 ([ui/](ui/))
- **框架**: Vue 3.5 使用组合式 API（Composition API）
- **语言**: TypeScript 6.0
- **构建工具**: Vite 8
- **状态管理**: Pinia 4
- **路由**: Vue Router 5
- **代码检查 / 格式化**: ESLint + oxlint + Prettier
- **包管理器**: pnpm

## 前置要求

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) ^22.18.0 || >=24.12.0
- [pnpm](https://pnpm.io/)（推荐）
- [PostgreSQL](https://www.postgresql.org/)（可选——默认为 SQLite，无需任何准备）

## 快速开始

### 后端设置

```bash
cd api

# 运行开发服务器（默认 SQLite，无需任何准备）
dotnet run

# 构建项目
dotnet build

# 发布生产版本
dotnet publish -c Release
```

后端 API 默认运行在 `http://localhost:5004`。启动时会输出数据库类型、连接串（已脱敏）、
SQLite 文件路径与 JWT 签名密钥，便于确认实际生效的配置。

#### 数据库

默认使用 SQLite，开箱即用——首次启动时会在**当前执行目录**下创建 `hamster.db`，
并通过 SqlSugar CodeFirst 自动建表。

```bash
# 改用 PostgreSQL（连接串与默认值一致时可省略）
export HAMSTER_DB_TYPE=PostgreSql
export HAMSTER_DB_CONNECTION="Host=localhost;Port=5432;Database=hamster;Username=postgres;Password=postgres"
```

| 环境变量 | 默认值 | 说明 |
|---|---|---|
| `HAMSTER_DB_TYPE` | `Sqlite` | 取值为 `Sqlite` 或 `PostgreSql` |
| `HAMSTER_DB_CONNECTION` | *（空）* | 连接串；留空时按数据库类型生成默认连接串 |
| `HAMSTER_DB_AUTOMIGRATE` | `true` | 启动时是否执行 CodeFirst 建表 |

环境变量优先级始终高于 `appsettings.json`。自动建表失败（如数据库连不上）时仅记录告警，
不会阻断应用启动。

健康探针：`GET /health`（存活探针，不访问数据库）与 `GET /health/db`（数据库连通性，
不可用时返回 `503`）。

#### 认证

登录使用 JWT 令牌，有效期**固定 1 天**；密码以 PBKDF2（HMAC-SHA256）哈希 + 每用户随机盐存储。

```bash
# 可选：固定签名密钥（不设置时每次启动随机生成）
export HAMSTER_JWT_KEY="<至少 32 字节的随机数据>"
```

未配置 `HAMSTER_JWT_KEY` 时会随机生成密钥并打印到控制台，**进程重启后此前签发的令牌将全部失效**，
长期部署请显式设置该变量。

| 接口 | 说明 |
|---|---|
| `POST /api/auth/register` | 注册（201，返回令牌）；用户名已存在返回 409 |
| `POST /api/auth/login` | 登录（200，返回令牌）；凭据错误返回 401 |
| `GET /api/auth/me` | 当前登录用户（需 Bearer 令牌） |

### 前端设置

```bash
cd ui

# 安装依赖
pnpm install

# 运行开发服务器
pnpm dev

# 类型检查
pnpm type-check

# 构建生产版本
pnpm build

# 代码检查和修复
pnpm lint

# 代码格式化
pnpm format

# 预览生产构建
pnpm preview
```

前端开发服务器将运行在 `http://localhost:5173`，后端开发环境的 CORS 策略已放行该地址。

---

版权所有 (c) 2026 delly.net
