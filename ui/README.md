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
│   ├── favicon.ico          # 站点图标（产品 ico，见「品牌资源」）
│   └── conf/setting.json    # 运行时配置（后端地址等，部署后可改）
└── src/
    ├── main.ts              # 应用入口（挂载前载入运行时配置并恢复登录态）
    ├── App.vue              # 根组件（header + body 骨架、功能菜单、抽屉与登录态入口）
    ├── assets/              # 样式与图片（base.css 配色令牌 / main.css 全局骨架 / logo.png 产品 Logo）
    ├── api/
    │   ├── http.ts          # 统一请求层（附加令牌、错误文案、401 处理）
    │   └── openapi/         # OpenAPI 文档拉取与调试请求（types / schema / client）
    ├── auth/                # 本地读写（localStorage + 内存兜底）：token.ts 登录令牌 / accountSet.ts 当前账套
    ├── config/
    │   ├── appConfig.ts     # 运行时配置加载
    │   ├── appInfo.ts       # 产品名 / 版本号 / 简介 / 技术栈 / 许可（「用户设置」的关于区块）
    │   └── menu.ts          # 左侧功能菜单配置（新增菜单项只改这里）
    ├── components/          # 通用组件（AccountSetPicker + openapi/ 调试面板组件）
    ├── views/               # 页面组件（HomeView / LoginView / OpenApiView / UserAdminView
    │                        #          / AccountSetAdminView / AccountView / SettingsView / ResetPasswordView）
    ├── router/index.ts      # 路由表、布局 meta 与登录 / 管理员守卫
    └── stores/
        ├── auth.ts          # 认证状态（注册 / 登录 / 登出 / 恢复）
        ├── accountSets.ts   # 账套状态（我可访问的账套 / 当前账套 / 管理侧增删改与关联用户）
        ├── accounts.ts      # 账户状态（当前账套内的账户列表 / 新建 / 修改 / 停用启用）
        └── users.ts         # 用户管理状态（列表 / 激活停用 / 重置链接 / 删除）
```

> 脚手架自带的 `HelloWorld`、`TheWelcome`、`WelcomeItem`、`components/icons/` 与 `stores/counter.ts` 已随本次布局重构全部删除。

## 品牌资源与配色

### 品牌资源

产品视觉资产来自仓库根目录的 [`doc/`](../doc)，落地到本工程的映射：

| 源文件 | 目标 | 用途 |
|---|---|---|
| `doc/Hamster.ico` | [`public/favicon.ico`](public/favicon.ico) | 站点图标（`index.html` 的 `rel="icon"` 已引用该路径，替换文件即生效） |
| `doc/Hamster_512.png` | [`src/assets/logo.png`](src/assets/logo.png) | 全站主 Logo（完整头部 34px、登录页卡片内 56px） |

`logo.png` 是 512×512 的**带透明圆角位图**，可干净地落在渐变页底上，无需再加底板。更换品牌图只需覆盖这两个文件——它们取代了脚手架自带的 Vite 图标与 Vite `logo.svg`（后者已删除）。

### 配色令牌

配色取自 Logo 本身（琥珀橙底、条纹焦糖橙、腹部奶油、深暖棕描边），全部集中在 [`src/assets/base.css`](src/assets/base.css)。**页面样式一律引用语义令牌，不再出现硬编码色值**：

| 令牌 | 亮色 | 暗色 | 用途 |
|---|---|---|---|
| `--color-accent` | `#e8934a` | `#f0a85c` | 主色：按钮实底、聚焦环、链接、选中态边框 |
| `--color-accent-strong` | `#d97b2e` | `#f7bc7c` | 主色加深：hover、小字号彩色文本 |
| `--color-accent-soft` | `rgba(232,147,74,.14)` | `rgba(240,168,92,.18)` | 主色浅底：hover 填充、选中态背景 |
| `--color-accent-contrast` | `#ffffff` | `#2a1b0e` | 主色实底上的前景色（**暗色下翻转为深棕**） |
| `--color-danger` / `-soft` / `-border` | 砖红系 | 暖珊瑚系 | 表单与请求报错 |
| `--gradient-page` | 奶油三档渐变 | 深棕三档渐变 | `body` 页底（`background-image`，`background-color` 兜底） |
| `--radius-card` / `--radius-control` | `18px` / `10px` | 同左 | 卡片与控件的圆角 |
| `--shadow-card` / `--shadow-control` | 暖棕柔和投影 | 纯黑投影 | 卡片浮起与控件层次 |

设计约定：

- **换肤只改 `base.css` 一处**——强调色收敛为上述三件套后，全站再无硬编码品牌色（先前 12 处散落的 Vite 绿已全部替换）。
- **暗色分支必须重新声明 `--gradient-page`**，否则暗色下会残留亮色奶油渐变（`prefers-color-scheme` 只覆盖显式列出的变量）。
- **`--color-accent-contrast` 在暗色下翻转**：暗色主色是亮橙，白字对比度不足，须配深棕文字。
- `/openapi` 页的**HTTP 方法徽标**（GET 蓝 / POST 绿 / PATCH 橙 / DELETE 紫等）刻意保留各自语义色，不并入品牌色，以维持方法间的可辨识度。

## 页面布局

全站为经典的「header + body」后台结构：

```
┌──────────────────────────────────────────────────┐
│ header：产品 Logo + 产品名 │ 用户名 · 账套 + 切换 · 退出登录 │
├──────────────┬───────────────────────────────────┤
│ body 左侧     │ body 右侧                          │
│ 功能菜单侧栏   │ 内容主体区（路由页面）                │
└──────────────┴───────────────────────────────────┘
```

| 环节 | 实现 |
|---|---|
| 骨架 | [`src/App.vue`](src/App.vue) 渲染 `header.app-header` + `div.app-body`（`aside.app-sidebar` 菜单 + `main.app-main` 内容区） |
| header 左侧 | 汉堡按钮（仅窄屏）+ 产品 Logo（34px）+ 产品名「仓鼠理财管家」 |
| header 右侧 | 已登录时依次为**用户名 → 当前账套名 +【切换】→【退出登录】**；未登录时仅显示「登录 / 注册」入口 |
| 功能菜单 | 数据源为 [`src/config/menu.ts`](src/config/menu.ts)，以**路由名**指向路由；`adminOnly: true` 的项（接口调试、用户管理、账套管理）仅管理员渲染；「首页」「账户管理」与末尾的「用户设置」面向所有登录用户 |
| 滚动模型 | `main.css` 把非空白布局的 `#app` 锁为整屏高度 + `overflow: hidden`，滚动交给 `.app-main`；header 与侧栏因此保持不动 |
| 内容区宽度 | 页面**铺满** `.app-main` 的可用宽度，内边距统一由 `.app-main` 提供，页面自身不再限宽居中 |
| 窄屏（<1024px） | 侧栏收起为抽屉，由 header 内汉堡按钮开合；路由跳转 / `Esc` / 点击遮罩均可关闭 |
| 空白布局例外 | 路由标 `meta: { layout: 'blank' }`（登录页、重置密码页）时不渲染 header 与侧栏，仅页面内容 + 一行版权页脚 |

新增功能菜单项只需在 `src/config/menu.ts` 的 `menuItems` 中追加一项（`routeName` 对应路由 `name`），`App.vue` 无需改动。

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

开发时访问 `http://localhost:5173/openapi`，页面会读取上述配置中的 OpenAPI 文档并渲染：

- **左侧**：按 Tag 分组的接口清单，支持按路径 / 摘要 / operationId 搜索；
- **右侧**：选中接口的详情与调试面板——按位置（path / query / header）填写参数、编辑 JSON 请求体（默认按 schema 推导出示例骨架）、点击「发送请求」查看响应状态码、耗时、响应头与响应体（JSON 自动美化，可一键复制）。

注意事项：

- **入口仅系统管理员可见**：左侧功能菜单的「接口调试」项标了 `adminOnly: true`，普通用户与未登录访客看不到它。但该页是开发调试工具，**路由本身未加守卫、依然公开**——直达 `/openapi` 对任何访客可用，别把它当成受保护页面；
- 该文档**仅在 Development 环境由后端暴露**，后端以其他环境启动时 `/openapi/v1.json` 返回 404，页面会给出对应提示而不是白屏；
- 调试请求直接发往 `api.baseUrl`，本地联调依赖后端已放行的 CORS（见下节）；
- 请求体编辑目前仅支持 `application/json`。

## 登录与注册 `/login`

以「用户名 + 密码」登录，登录态由后端签发的 JWT 承载。页面为登录 / 注册合一的表单（Tab 切换）：

- **注册**：用户名 3–32 位字母、数字或下划线，密码至少 6 位；用户名全局唯一（查重不区分大小写）。注册**不会自动登录**——新账号默认未激活，注册成功后页面**切回登录 Tab** 并提示「注册成功，请等待管理员激活后登录」，同时清空密码框。按钮文案相应为「注册」而非「注册并登录」。
- **登录**：凭据错误时提示「用户名或密码错误」（后端不区分用户不存在与密码错误，避免枚举用户名）；账号未激活时提示「账号尚未激活，请联系管理员激活」，且**不写入令牌**、不跳转，用户可原地重试。

### 空白布局

登录页为**空白布局**（见「页面布局」）——未登录用户不应看到应用内的功能菜单与其他模块入口，故该页不渲染 header 与侧栏，卡片外仅保留一行版权页脚，表单卡片在视口内水平垂直居中。品牌 Logo 由登录页自身承载，**位于表单卡片内部顶部并居中**（56px），与标题、Tab、表单构成同一视觉整体。

| 环节 | 实现 |
|---|---|
| 布局声明 | 路由上标 `meta: { layout: 'blank' }`（见 [`src/router/index.ts`](src/router/index.ts)） |
| 头部/菜单/页脚切换 | [`src/App.vue`](src/App.vue) 依 `route.meta.layout` 在空白布局下只渲染页面与版权页脚，同时在 `body` 上切换 `layout-blank` 类 |
| 版式分支 | [`src/assets/main.css`](src/assets/main.css) 的 `body:not(.layout-blank) #app` 只在完整布局下锁定整屏高度；卡片的居中由页面自己用 `margin: auto` 完成 |

其余路由（`/`、`/settings`、`/openapi`、`/admin/*`）沿用完整 header-body 布局。新增其它空白页只需在路由上补 `meta: { layout: 'blank' }`，无需改动 `App.vue`。

### 登录态的存放与恢复

| 环节 | 实现 |
|---|---|
| 令牌存放 | `localStorage`（键 `hamster.auth.token`），隐私模式等不可用时降级为内存存储 |
| 令牌附加 | [`src/api/http.ts`](src/api/http.ts) 自动附加 `Authorization: Bearer <token>` |
| 启动恢复 | `main.ts` 在安装路由前调用 `auth.restore()`，凭本地令牌请求 `/api/auth/me` 还原用户；失败则静默登出 |
| 令牌失效 | 请求返回 401 时统一清空令牌并跳转登录页——登录 / 注册接口自身的 401 例外（传 `handleUnauthorized: false`），避免把「密码错误」误判为登录失效 |

令牌有效期**固定 1 天**，过期或后端更换签名密钥后需重新登录。

## 用户管理 `/admin/users`

系统管理员专用的用户管理台（[`src/views/UserAdminView.vue`](src/views/UserAdminView.vue) + [`src/stores/users.ts`](src/stores/users.ts)），列表展示序号、用户名、角色、激活状态与注册时间，并提供三类操作：

| 操作 | 行为 |
|---|---|
| 激活 / 停用 | 双向开关。停用后该用户无法再登录；**已签发的令牌在过期前仍可用**（见下方「已知边界」） |
| 重置链接 | 生成有效期 15 分钟、一次性的专属链接，页内展示全文并提供一键复制；重新生成会使旧链接立即失效 |
| 删除 | 需二次确认（按钮就地变为「确认删除 / 取消」，不使用 `window.confirm`，以免阻塞无头渲染与自动化）；删除后该用户令牌立即失效，用户名可被重新注册 |

设计约定：

- **入口显隐只是体验**：左侧功能菜单的「用户管理」入口按 `auth.isAdmin` 显隐，路由守卫也会挡回非管理员，但**真正的防线是后端**——每个管理端点都回查数据库确认调用者仍是启用状态的管理员。篡改前端状态只会看到一个请求全部失败的页面。
- **自锁保护**：当前登录账号所在行标「当前账号」，其「停用」「删除」按钮置灰；后端对同一规则有独立校验，绕过前端只会得到 400。
- **序号列是行号不是 Id**：取 `v-for` 的行下标 +1，按当前列表顺序连续编号，刷新后重算。**不要改成 `user.id`**——删过用户就会出现断号，容易被误读为数据缺失。
- **时间显示**：后端回传的是带 `Z` 的 UTC 时间，页面用 `Intl.DateTimeFormat` 按浏览器本地时区渲染。
- **成功提示复用主色**而非绿色：全站只有 `--color-danger` 一种语义色，不额外引入绿色以维持暖色视觉体系。

> **已知边界**：停用只阻止**新的登录**，已签发的令牌在其 1 天有效期内仍可访问非管理接口。若需要「停用即踢下线」，需在服务端的令牌校验环节回查用户状态（本项目当前未实现）。

## 账户管理 `/accounts`

面向**所有登录用户**的记账账户管理页（[`src/views/AccountView.vue`](src/views/AccountView.vue) + [`src/stores/accounts.ts`](src/stores/accounts.ts)），路由只标 `requiresAuth`——**不带** `requiresAdmin`，菜单项同样不设 `adminOnly`。页面提供新建表单、账户列表与行内编辑，并支持停用/启用。

| 环节 | 行为 |
|---|---|
| 未选择账套 | 只显示「请先切换账套」的提示，**不渲染表单与表格**——账户一律挂在账套下，展示一个不属于任何账套的空列表只会误导 |
| 跟随账套切换 | `watch` 账套 store 的 `currentId`，变化即重新拉取；未选择时清空列表。**仅靠 `onMounted` 会残留上一账套的账户** |
| 新建 | 名称 + 归属范围（个人/公共）+ 类型（四选一）+ 期初金额。归属人由后端强制为当前登录者，故表单**没有**归属人字段 |
| 行内编辑 | 可改名称、类型、期初金额；归属范围**不可改**（个人 → 公共等于把私有数据公开给全账套），故编辑态不呈现该字段 |
| 停用 / 启用 | 停用需二次确认（就地变为「确认停用 / 取消」），启用直接生效；按钮文案统一用「停用」而非「删除」，与后端软删除语义一致 |
| 显示已停用 | 勾选框驱动列表请求的 `includeInactive`，勾选即重拉，无需手动刷新 |
| 金额呈现 | `Intl.NumberFormat` 固定两位小数，右对齐 + `tabular-nums` 便于纵向比对 |

设计约定：

- **个人账户的「私有」由后端维持**：列表里看不到他人个人账户、直接构造请求改他人个人账户返回 404，都是后端判定的结果。本页**不做任何本地过滤**——前端的可见性只是体验层，篡改本地状态只会拿到一批 403 / 404。
- **余额是派生值**：页面展示的余额来自后端的 `balance` 字段，当前等于期初金额（尚无流水表）。**不要在前端用期初金额渲染余额列**，否则流水表落地后页面会与后端脱节。
- **序号列是行号不是 Id**：同用户管理页，取行下标 +1，停用账户后不会断号。
- **未选账套时的 400 是预期行为**：后端对不带 `X-Account-Set-Id` 的请求返回 400「请先选择账套」，这正是页面在无账套时不发请求的原因。

## 用户设置 `/settings`

面向**所有登录用户**的个性化页（[`src/views/SettingsView.vue`](src/views/SettingsView.vue)），路由只标 `requiresAuth`——**不带** `requiresAdmin`，菜单项同样不设 `adminOnly`。页内含两个区块：

| 区块 | 内容 |
|---|---|
| 修改密码 | 原密码 + 新密码 + 确认新密码，提交 `POST /api/auth/change-password` |
| 关于 | 产品名、版本号、一句话简介、技术栈、开源许可（MIT）与版权 |

设计约定：

- **改密必须提供原密码**：仅有令牌不足以改密（否则令牌泄露即可直接夺号），后端校验原密码失败时统一返回 400「原密码不正确」。
- **改密成功后前端主动登出**：成功提示下方提供「前往登录」链接，用户须用新密码重新登录。见下方「已知边界」——这是体验层的收口，不是服务端的强制失效。
- **改密失败不触发全局登录失效处理**：请求层传 `handleUnauthorized: false`，400 是业务校验失败，不应被误判为登录态过期。
- **关于信息集中在 [`src/config/appInfo.ts`](src/config/appInfo.ts)**，页面只做渲染。
- **版本号以 `package.json` 为单一数据源**：`vite.config.ts` 读取后在构建时经 `define` 注入全局常量 `__APP_VERSION__`（类型声明见 [`env.d.ts`](env.d.ts)），页面与 `appInfo.ts` 均不硬编码版本号。当前版本 `0.1.0`。

> **已知边界**：JWT 不携带密码版本，**改密（乃至被管理员停用）都不会使已签发的旧令牌立即失效**，旧令牌在其 1 天有效期内仍可访问非管理接口。前端改密后主动登出只是让本人这台浏览器不再沿用旧令牌；若需要「改密即踢下线」，需在服务端的令牌校验环节回查用户状态（本项目当前未实现）。

## 密码重置 `/reset-password`

管理员生成的重置链接落地页（[`src/views/ResetPasswordView.vue`](src/views/ResetPasswordView.vue)），**免登录**（路由 `meta: { layout: 'blank' }`，不带 `requiresAuth`）——被重置的用户多半处于未登录态，要求登录会让链接形同虚设。

- 链接形如 `/reset-password?username=xxx&token=yyy`，用户名由查询参数**预填但可修改**：预填只是省事，真正的「用户名 + 令牌」双因子匹配由后端完成，篡改它只会得到统一的失败提示。
- 缺少 `token` 参数时给出「重置链接不完整」的明确提示，并禁用提交按钮，而不是让用户对着无效表单反复重试。
- 前端校验两次密码一致与长度下限，后端仍会独立校验一次。
- 成功后展示后端返回的「密码已重置，请使用新密码登录」并提供前往登录页的链接。

### 路由守卫

[`src/router/index.ts`](src/router/index.ts) 中的 `beforeEach` 按 `meta.requiresAuth` / `meta.requiresAdmin` 拦截：

- 未登录访问受保护路由（`/`、`/settings`、`/admin/*`）→ 重定向到 `/login`，并带上 `redirect` 查询参数，登录成功后跳回来源页；
- 已登录但非管理员访问 `/admin/users` → 重定向回首页；
- 已登录访问 `/login` → 直接跳回首页。

`/openapi` 与 `/reset-password` 不依赖登录态，保持公开。

注意 `/openapi` 的两层是分开的：**菜单入口**由 `menu.ts` 的 `adminOnly: true` 收敛为仅管理员可见，**路由**未加守卫、直达地址仍对任何访客开放。若日后要真正限制访问，应给该路由补 `meta: { requiresAuth: true, requiresAdmin: true }`（与 `/admin/*` 一致）。

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

POST http://localhost:5004/api/auth/register       # 注册（201，不返回令牌，需管理员激活）
POST http://localhost:5004/api/auth/login          # 登录（200，返回令牌）
GET  http://localhost:5004/api/auth/me             # 当前用户（需 Bearer 令牌）
POST http://localhost:5004/api/auth/change-password # 自助修改密码（需 Bearer 令牌 + 原密码）
POST http://localhost:5004/api/auth/reset-password # 凭用户名 + 令牌设置新密码（免登录）

GET    http://localhost:5004/api/admin/users                    # 用户列表（需管理员）
POST   http://localhost:5004/api/admin/users/{id}/activate      # 激活
POST   http://localhost:5004/api/admin/users/{id}/deactivate    # 停用
POST   http://localhost:5004/api/admin/users/{id}/reset-link    # 生成 15 分钟一次性重置链接
DELETE http://localhost:5004/api/admin/users/{id}               # 删除
```

首次启动会自动播种默认管理员 `admin` / `admin123`（可用 `HAMSTER_ADMIN_USERNAME` / `HAMSTER_ADMIN_PASSWORD` 覆盖，详见[根 README](../README.zh-CN.md)）。
