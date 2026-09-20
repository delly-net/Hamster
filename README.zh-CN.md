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
- **数据访问**: SqlSugar ORM，经 Npgsql 连接 PostgreSQL
- **分层**: `Config/` · `Data/` · `Services/` · `Endpoints/`

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
- [PostgreSQL](https://www.postgresql.org/)（仅搭框架时可暂不准备，数据相关接口需要）

## 快速开始

### 后端设置

```bash
cd api

# 配置数据库连接串（也可直接修改 api/appsettings.json）
export HAMSTER_DB_CONNECTION="Host=localhost;Port=5432;Database=hamster;Username=postgres;Password=postgres"

# 运行开发服务器
dotnet run

# 构建项目
dotnet build

# 发布生产版本
dotnet publish -c Release
```

后端 API 默认运行在 `http://localhost:5004`。

数据库行为说明：

- 默认**不会**自动建表。将 `Database:AutoMigrate` 置为 `true`（或设置环境变量 `HAMSTER_DB_AUTOMIGRATE=true`），启动时才会执行 SqlSugar CodeFirst 建表。
- 健康探针：`GET /health`（存活探针，不访问数据库）与 `GET /health/db`（PostgreSQL 连通性，不可用时返回 `503`）。

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
