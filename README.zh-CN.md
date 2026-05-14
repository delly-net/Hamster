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

## 技术栈

### 后端 ([Hamster/](Hamster/))
- **框架**: ASP.NET Core 10.0
- **语言**: C# 12+
- **API 风格**: Minimal APIs，支持 OpenAPI/Swagger
- **特性**:
  - AOT（Ahead-of-Time）编译以获得最佳性能
  - 开发环境支持 OpenAPI 文档
  - 内置源生成的 JSON 序列化

### 前端 ([Vue/](Vue/))
- **框架**: Vue 3.5 使用组合式 API（Composition API）
- **语言**: TypeScript 6.0
- **构建工具**: Vite 8.0
- **状态管理**: Pinia 3.0
- **路由**: Vue Router 5.0
- **包管理器**: pnpm

## 前置要求

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) ^20.19.0 || >=22.12.0
- [pnpm](https://pnpm.io/)（推荐）

## 快速开始

### 后端设置

进入后端目录并运行开发服务器：

```bash
cd Hamster

# 运行开发服务器
dotnet run

# 构建项目
dotnet build

# 发布生产版本
dotnet publish -c Release
```

后端 API 默认运行在 `http://localhost:5004`。

### 前端设置

进入前端目录并安装依赖：

```bash
cd Vue

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

前端开发服务器将运行在 `http://localhost:5173`。

## 项目结构

```
Hamster/
├── Hamster/              # ASP.NET Core 后端
│   ├── Program.cs        # 应用程序入口，包含 API 端点
│   ├── Hamster.csproj    # 项目配置
│   └── Properties/       # 启动设置和配置
├── Vue/                  # Vue 3 前端
│   ├── src/
│   │   ├── components/   # Vue 组件
│   │   ├── views/        # 页面组件
│   │   ├── router/       # Vue Router 配置
│   │   ├── stores/       # Pinia 状态管理
│   │   └── assets/       # 静态资源
│   ├── vite.config.ts   # Vite 构建配置
│   └── package.json     # 依赖和脚本
├── .github/             # GitHub Actions 工作流
├── CLAUDE.md           # AI 助手项目文档
└── README.md           # 本文件
```

## API 端点

当前可用的端点：

| 方法 | 端点 | 描述 |
|--------|----------|-------------|
| GET | `/todos` | 获取所有待办事项 |
| GET | `/todos/{id}` | 根据 ID 获取待办事项 |

开发模式下可访问 `/openapi/v1.json` 获取 OpenAPI 文档。

## 开发

### 代码风格

- **C#**: 启用可空引用类型，启用隐式 using
- **TypeScript**: 在 `tsconfig.json` 中配置了严格类型检查
- **代码检查**: ESLint 配合 oxlint 和 Prettier 格式化
- **Vue**: 单文件组件使用 `<script setup>` 语法

### 环境

- 后端使用 `ASPNETCORE_ENVIRONMENT` 配置运行时行为
- 前端使用 Vite 内置的环境变量支持

## 许可证

本项目采用 MIT 许可证 - 详见 [LICENSE](LICENSE) 文件。

## 贡献

欢迎贡献！请随时提交 Pull Request。

## 致谢

- [ASP.NET Core](https://docs.microsoft.com/aspnet/core) - 用于后端的 Web 框架
- [Vue.js](https://vuejs.org) - 用于前端的渐进式 JavaScript 框架
- [Vite](https://vitejs.dev) - 下一代前端工具

---

版权所有 (c) 2026 delly.net