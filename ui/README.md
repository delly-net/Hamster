# Hamster UI

仓鼠理财管家的前端工程：Vue 3 + TypeScript + Vite。

## 技术栈

| 项 | 选型 |
|---|---|
| 框架 | Vue 3.5（组合式 API，`<script setup>`） |
| 语言 | TypeScript 6.0（`vue-tsc` 做类型检查） |
| 构建 | Vite 8 |
| 路由 | Vue Router 5 |
| 状态管理 | Pinia 4 |
| 代码质量 | ESLint 10 + oxlint + Prettier |
| 包管理 | pnpm（唯一锁文件 `pnpm-lock.yaml`） |
| 开发端口 | 5173 |

## 目录结构

```
ui/
├── index.html               # HTML 入口
├── vite.config.ts           # Vite 配置（含 @ -> src 别名）
├── tsconfig*.json           # TS 工程配置（app / node 分离）
├── eslint.config.ts         # ESLint 扁平配置
├── .prettierrc.json         # Prettier 配置
├── .oxlintrc.json           # oxlint 配置
├── public/                  # 静态资源（原样拷贝）
└── src/
    ├── main.ts              # 应用入口
    ├── App.vue              # 根组件
    ├── assets/              # 样式与图片
    ├── components/          # 通用组件（当前为脚手架示例组件）
    ├── views/               # 页面组件（HomeView / AboutView 示例页）
    ├── router/index.ts      # 路由表
    └── stores/counter.ts    # Pinia 示例 store
```

> `components/`、`views/`、`stores/` 中当前是脚手架自带的示例内容，用于演示路由、状态与组件接线方式，开始业务开发时可直接删除。

## 开发命令

```bash
pnpm install     # 安装依赖
pnpm dev         # 启动开发服务器 http://localhost:5173
pnpm type-check  # 类型检查（vue-tsc --build）
pnpm lint        # oxlint + ESLint 检查并自动修复
pnpm format      # Prettier 格式化 src/
pnpm build       # 类型检查 + 生产构建，产出 dist/
pnpm preview     # 预览生产构建产物
```

## 与后端联调

后端工程位于 [`../api`](../api)，开发端口 **5004**，其开发环境已放行来自 `http://localhost:5173` 的 CORS 请求，因此本地可直接跨端口调用。

本工程当前**未包含** API 请求层（无 axios / fetch 封装）。接入后端时建议：

1. 在 `src/` 下新增 `api/` 目录，统一放置请求客户端与接口定义；
2. 在 `vite.config.ts` 中配置 `server.proxy` 将 `/api` 转发到 `http://localhost:5004`，即可省去跨域与硬编码域名。

后端可用的探针与示例接口：

```
GET  http://localhost:5004/health          # 存活探针
GET  http://localhost:5004/health/db       # PostgreSQL 连通性探针
GET  http://localhost:5004/openapi/v1.json # OpenAPI 文档（仅开发环境）
```
