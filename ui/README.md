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
│   └── conf/setting.json    # 运行时配置（后端地址等，部署后可改）
└── src/
    ├── main.ts              # 应用入口（挂载前载入运行时配置并恢复登录态）
    ├── App.vue              # 根组件（导航与登录态入口）
    ├── assets/              # 样式与图片
    ├── api/
    │   ├── http.ts          # 统一请求层（附加令牌、错误文案、401 处理）
    │   └── openapi/         # OpenAPI 文档拉取与调试请求（types / schema / client）
    ├── auth/token.ts        # 登录令牌的本地读写（localStorage + 内存兜底）
    ├── config/appConfig.ts  # 运行时配置加载
    ├── components/          # 通用组件（含 openapi/ 调试面板组件）
    ├── views/               # 页面组件（HomeView / AboutView / LoginView / OpenApiView）
    ├── router/index.ts      # 路由表与登录守卫
    └── stores/
        ├── auth.ts          # 认证状态（注册 / 登录 / 登出 / 恢复）
        └── counter.ts       # Pinia 示例 store
```

> `stores/counter.ts` 与 `components/` 中的 `HelloWorld`、`TheWelcome` 等仍是脚手架自带的示例内容，开始业务开发时可直接删除。

## 运行时配置

后端地址不再硬编码在源码中，而是由 [`public/conf/setting.json`](public/conf/setting.json) 提供，应用启动时通过 `fetch` 读取：

```json
{
  "api": {
    "baseUrl": "http://localhost:5004",
    "openApiDocPath": "/openapi/v1.json",
    "timeoutMs": 15000
  }
}
```

| 字段 | 说明 |
|---|---|
| `api.baseUrl` | 后端服务基址（末尾斜杠可省略） |
| `api.openApiDocPath` | OpenAPI 文档路径，相对 `baseUrl` |
| `api.timeoutMs` | 文档拉取与调试请求的超时时间（毫秒） |

`public/` 下的文件会被原样拷贝到构建产物，因此**部署后可直接修改该文件调整后端地址，无需重新构建**。文件缺失或字段缺省时回退到内置默认值（同样是 `http://localhost:5004`），不会阻断启动。

## 接口调试页 `/openapi`

开发时访问 `http://localhost:5173/openapi`（导航栏「接口调试」入口），页面会读取上述配置中的 OpenAPI 文档并渲染：

- **左侧**：按 Tag 分组的接口清单，支持按路径 / 摘要 / operationId 搜索；
- **右侧**：选中接口的详情与调试面板——按位置（path / query / header）填写参数、编辑 JSON 请求体（默认按 schema 推导出示例骨架）、点击「发送请求」查看响应状态码、耗时、响应头与响应体（JSON 自动美化，可一键复制）。

注意事项：

- 该文档**仅在 Development 环境由后端暴露**，后端以其他环境启动时 `/openapi/v1.json` 返回 404，页面会给出对应提示而不是白屏；
- 调试请求直接发往 `api.baseUrl`，本地联调依赖后端已放行的 CORS（见下节）；
- 请求体编辑目前仅支持 `application/json`。

## 登录与注册 `/login`

以「用户名 + 密码」登录，登录态由后端签发的 JWT 承载。页面为登录 / 注册合一的表单（Tab 切换）：

- **注册**：用户名 3–32 位字母、数字或下划线，密码至少 6 位；用户名全局唯一（查重不区分大小写）；注册成功即登录。
- **登录**：凭据错误时提示「用户名或密码错误」（后端不区分用户不存在与密码错误，避免枚举用户名）。

### 登录态的存放与恢复

| 环节 | 实现 |
|---|---|
| 令牌存放 | `localStorage`（键 `hamster.auth.token`），隐私模式等不可用时降级为内存存储 |
| 令牌附加 | [`src/api/http.ts`](src/api/http.ts) 自动附加 `Authorization: Bearer <token>` |
| 启动恢复 | `main.ts` 在安装路由前调用 `auth.restore()`，凭本地令牌请求 `/api/auth/me` 还原用户；失败则静默登出 |
| 令牌失效 | 请求返回 401 时统一清空令牌并跳转登录页——登录 / 注册接口自身的 401 例外（传 `handleUnauthorized: false`），避免把「密码错误」误判为登录失效 |

令牌有效期**固定 1 天**，过期或后端更换签名密钥后需重新登录。

### 路由守卫

[`src/router/index.ts`](src/router/index.ts) 中的 `beforeEach` 按 `meta.requiresAuth` 拦截：

- 未登录访问受保护路由（`/`、`/about`）→ 重定向到 `/login`，并带上 `redirect` 查询参数，登录成功后跳回来源页；
- 已登录访问 `/login` → 直接跳回首页。

`/openapi` 不依赖登录态，保持公开。

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

本工程**未使用 Vite proxy**：后端基址由 `public/conf/setting.json` 的 `api.baseUrl` 在运行时提供（见「运行时配置」），请求由浏览器直接发往该地址，因此**依赖后端放行的 CORS**。若前端端口发生变化，需同步调整后端 [`ConfigConst.FRONTEND_DEV_ORIGIN`](../api/Config/ConfigConst.cs)。

业务接口统一经 [`src/api/http.ts`](src/api/http.ts) 发起（自动附加令牌、统一错误文案与 401 处理），OpenAPI 调试页仍使用 [`src/api/openapi/`](src/api/openapi/) 下的独立实现。

后端可用的探针、认证与示例接口：

```
GET  http://localhost:5004/health                  # 存活探针
GET  http://localhost:5004/health/db               # 数据库连通性探针（返回当前库类型）
GET  http://localhost:5004/openapi/v1.json         # OpenAPI 文档（仅开发环境）

POST http://localhost:5004/api/auth/register       # 注册（201，返回令牌）
POST http://localhost:5004/api/auth/login          # 登录（200，返回令牌）
GET  http://localhost:5004/api/auth/me             # 当前用户（需 Bearer 令牌）
```
