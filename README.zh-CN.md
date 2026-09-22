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

##### 默认管理员

首次启动会自动播种一个系统管理员，使全新拉取的工程开箱可用：

> [!WARNING]
> 默认口令 **`admin` / `admin123` 仅用于开箱体验**。在把实例开放给他人之前，请先通过下面的
> 环境变量覆盖，或登录后立即改密。播种**只会创建**缺失的 `admin` 账号，**绝不覆盖已有密码**，
> 因此改过一次即不会再被默认值还原。

| 环境变量 | 默认值 | 说明 |
|---|---|---|
| `HAMSTER_ADMIN_SEED_ENABLED` | `true` | 启动时是否播种默认管理员 |
| `HAMSTER_ADMIN_USERNAME` | `admin` | 播种的管理员用户名 |
| `HAMSTER_ADMIN_PASSWORD` | `admin123` | 播种的管理员密码（仅在创建账号时使用） |
| `HAMSTER_PUBLIC_BASE_URL` | `http://localhost:5173` | 拼接密码重置链接所用的前端地址 |

##### 账号生命周期

1. **注册**：`POST /api/auth/register` 创建的账号默认**未激活**，且**不签发令牌**。
2. **激活**：由管理员在用户管理页 `/admin/users` 激活；在此之前登录一律返回 `403` 与「账号尚未激活，请联系管理员激活」。
3. **登录**：`POST /api/auth/login` 返回令牌。停用账号会阻止后续登录；**已签发的令牌在过期前仍然有效**（有效期 1 天）。
4. **重置密码**：管理员生成**有效期 15 分钟、一次性**的重置链接（`POST /api/admin/users/{id}/reset-link`）并交付用户。链接打开 `/reset-password?username=…&token=…`，需**同时**提供用户名与令牌；服务端只存令牌的 SHA-256 哈希，用后立即清空。所有失败原因统一返回同一 `400` 文案，避免被用于探测用户名。
5. **删除**：`DELETE /api/admin/users/{id}` 删除账号并**立即使其令牌失效**。

管理员不可停用或删除自己的账号。管理员身份**不写入 JWT**：每个管理端点都回查数据库，
因此撤销权限立即生效。登录同样遵循**先验密码、后判激活**的顺序——密码错误先返回 `401`，
匿名请求无法借此得知「某用户名存在但未激活」。

| 接口 | 鉴权 | 说明 |
|---|---|---|
| `POST /api/auth/register` | — | 注册（201，**不返回令牌**）；用户名已存在返回 409 |
| `POST /api/auth/login` | — | 登录（200，返回令牌）；凭据错误返回 401，账号未激活返回 403 |
| `GET /api/auth/me` | Bearer | 当前登录用户 |
| `POST /api/auth/reset-password` | — | 凭 `{ username, token, newPassword }` 设置新密码；链接无效、已过期或已使用统一返回 400 |
| `GET /api/admin/users` | 管理员 | 用户列表 |
| `POST /api/admin/users/{id}/activate` | 管理员 | 激活用户（204） |
| `POST /api/admin/users/{id}/deactivate` | 管理员 | 停用用户（204）；停用自己返回 400 |
| `POST /api/admin/users/{id}/reset-link` | 管理员 | 生成 15 分钟一次性重置链接 |
| `DELETE /api/admin/users/{id}` | 管理员 | 删除用户（204）；删除自己返回 400 |

##### 账套

**账套**是一组业务数据的归属单位（如「家庭」与「公司」）。账套与用户是**多对多**关系：管理员在
`/admin/account-sets` 把用户关联到账套。管理员自身无需关联——可查看并切换到**全部**账套。

普通用户登录后的行为取决于其可访问的账套数量：

| 可访问账套数 | 登录后 |
|---|---|
| 0 个 | header 显示「暂无可用账套」，不显示账套名称与切换按钮 |
| 1 个 | 自动选中，不打扰用户 |
| 2 个及以上 | **先弹出选择弹窗**，未选定前无法关闭（弹窗内提供「退出登录」作为逃生入口） |

选定后，header 会在**退出登录之前**显示当前账套名称与【切换】按钮。

当前账套随每个请求以 `X-Account-Set-Id` 请求头携带，并由后端**逐请求**校验用户与该账套的关联关系
——账套刻意**不写入 JWT**：写进令牌后，管理员调整关联关系须等令牌过期（1 天）才对用户生效，
逐请求校验则在**下一次请求**即生效。

账户表 `hamster_account` 是**首张真正按账套隔离**的业务表（见下节）；示例表 `sample_account`
仍无账套字段。新增业务表请沿用同一约定：加 `account_set_id` 列，并以
`CurrentAccountSetExtensions.ResolveCurrentAccountSetAsync`（依请求头解析并鉴权账套）解析当前账套，
**未解析到账套即拒绝请求**——否则会退化成「查询全库数据」。

| 接口 | 鉴权 | 说明 |
|---|---|---|
| `GET /api/account-sets/mine` | Bearer | 我可访问的账套（管理员为全部） |
| `GET /api/account-sets/current` | Bearer | 依 `X-Account-Set-Id` 解析当前账套：不带头返回 `null`，格式非法返回 400，账套不存在或无权访问返回 403 |
| `GET /api/admin/account-sets` | 管理员 | 账套列表（含关联用户数） |
| `POST /api/admin/account-sets` | 管理员 | 新建账套（201）；名称重复返回 409 |
| `PUT /api/admin/account-sets/{id}` | 管理员 | 修改名称与备注（204） |
| `DELETE /api/admin/account-sets/{id}` | 管理员 | 删除账套并清除其关联（204） |
| `GET /api/admin/account-sets/{id}/members` | 管理员 | 账套关联用户 Id 列表 |
| `PUT /api/admin/account-sets/{id}/members` | 管理员 | 以 `{ userIds }` **整体替换**关联用户（204） |

##### 账户

**账户**是记账业务中资金与债务的挂靠对象，归属且仅归属一个账套：账户列表按当前账套过滤，
切换账套即切换账户集合（`hamster_account` 是第一张真正按账套隔离的业务表）。

归属范围分两种，可见性由后端逐请求判定：

| 归属范围 | 谁能看见、谁能改 |
|---|---|
| 公共账户 | 账套内**全部成员**，用于家庭共用的账户 |
| 个人账户 | **仅归属人本人**（归属人 = 创建者，创建后不可转让） |

系统管理员是唯一例外：对任意存在的账套均可访问（仍须带账套请求头），且可见并可改账套内的
**全部**账户（含他人个人账户）。本任务中「可见集合 = 可改集合」，故没有「只读可见」这一态。

账户类型固定四类，不支持用户自定义：

| 类型 | 取值 | 说明 | 用户可选 |
|---|---|---|---|
| 账本账户 | `Ledger` | 记账用的汇总性账户，本身不代表具体的钱 | **否**——由系统自动创建，且不在账户列表中呈现 |
| 资金账户 | `Fund` | 实实在在的钱（现金、银行卡、电子钱包等） | 是 |
| 负债账户 | `Liability` | 欠别人的钱（信用卡、借款等），期初金额允许为负 | 是 |
| 往来账户 | `Contact` | 人情往来与应收应付（借出、借入、待收报销等） | 是 |

**账本账户是纯系统内部账户**：它只作期初余额入账的复式对手方，**不接受手工建立或变更**
（`POST` / `PUT` 传 `type: "Ledger"` 一律 400），也**不在任何账户列表中呈现**——
管理员的列表同样不含它。不做例外、不设开关，可见性口径唯一。
它照常计入余额与参与复式配平，隐藏的只是界面呈现。是否可由用户指定由
`AccountTypeExtensions.IsUserAssignable` 一处判定，端点的校验与错误文案都从它派生。

**期初金额**是账户建立时已有的金额（两位小数以内），创建时即落成一笔期初交易（详见下节），
**创建后不可修改**——调整余额应记一笔余额调整交易，而不是改写既成的期初。
**余额**是**只读派生值**，等于该账户全部交易明细的有符号汇总。数据库中**没有余额列**：
派生值只留一条计算路径，避免「余额列忘了同步」的静默错账。

账户**不做物理删除**，只有停用/启用（软删除）：账户是流水的挂靠对象，物理删除会让历史流水悬空。
停用后默认不出现在列表中，加 `includeInactive=true` 可查看并重新启用。

`isSystem` 标记系统自动创建的账户（当前即期初账本账户）。它**不接受用户创建，也不出现在账户列表中**
（列表过滤依据是类型 `Ledger`，见上表；「不在列表中」与「不可手工建立」互为闭环）。
`isSystem` 本身只用于「按需创建时复用同一条」，故**不会因改名或改类型而失去系统身份**——
身份记在标记列上，不靠名称或类型反查。

| 接口 | 鉴权 | 说明 |
|---|---|---|
| `GET /api/accounts?includeInactive=false` | Bearer | 当前账套内我可见的账户（管理员为全部），`balance` 为派生余额；**不含账本账户** |
| `POST /api/accounts` | Bearer | 新建（201）；请求体 `{ name, scope, type, initialBalance }`，`type` 只能是 `Fund` / `Liability` / `Contact`；个人账户的归属人强制为当前登录者；期初金额非 0 时同时落成一笔期初交易 |
| `PUT /api/accounts/{id}` | Bearer | 修改名称/类型（204），`type` 同样不含 `Ledger`；归属范围、归属人、所属账套与**期初金额**一经创建均不可修改 |
| `POST /api/accounts/{id}/deactivate` | Bearer | 停用（软删除，204） |
| `POST /api/accounts/{id}/activate` | Bearer | 启用（204） |

失败约定：未携带账套请求头返回 400（账户一律挂在账套下）；账套不存在或无权访问返回 403；
账户不存在、不属于该账套或对当前用户不可见一律返回 **404**（三种情形同响应，避免被用于探测）；
名称在「账套 + 归属范围」内重复返回 409。`scope` / `type` 对外为**字符串**，
传数字或未知名称一律 400。

##### 交易与流水

记账采用**复式记账**：**每一笔交易都有借贷两条明细**，借方金额合计恒等于贷方金额合计。
数据分两张表：

| 表 | 内容 |
|---|---|
| `hamster_transaction` | 交易头：账套、类型、发生时间、摘要、备注、记账人 |
| `hamster_transaction_entry` | 交易明细：所属交易、账户、借贷方向、金额 |

明细的方向用 **`direction` 枚举 + 正数金额**表示，而不是「金额带正负号」：

| 方向 | 取值 | 对该账户余额 |
|---|---|---|
| 借方 | `Debit`（1） | 增加 |
| 贷方 | `Credit`（2） | 减少 |

符号换算**只在一处发生**（`TransactionService.SumSignedAmountsAsync`：借方计正、贷方计负），
其余各处一律只搬运正数金额。刻意**不按账户类型翻转符号**（例如「负债账户贷方表示增加」）——
那会让同一个 `direction` 在不同账户上含义相反，符号一旦分散到多处就再难对齐。

借贷配平由**写入侧保证**：每条明细都是「目标账户 + 对手方账户」成对写入，
没有只写单边的入口。

###### 期初余额

建账户时，期初金额**自动落成一笔期初交易**，而不是只写进 `initial_balance` 列：

```
期初金额 500 的「现金」：
  借  现金      500
  贷  期初账本  500
```

**期初账本账户**（名称「期初账本」，类型 `Ledger`，公共归属，`isSystem = true`）是这笔交易的对手方，
由系统**按需自动创建**：账套内首次需要时创建，之后复用，**一个账套恒只有一个**。
它**不呈现于账户列表**，也不接受手工建立或改类型（见上文「账户」一节的类型表）。
期初金额为 0 的账户**不写任何分录**（其明细汇总恒为 0，与期初金额一致）；
期初金额为负的账户（负债）方向自动翻转——目标账户落贷方（余额减少），账本账户落借方。

于是**余额 = 全部明细的有符号汇总**，期初分录本身就是「+期初金额」，故**不再叠加 `initial_balance`**，
否则期初金额会被重复计一次。

既有账户（本次升级前已存在）在启动时由**期初余额回填**补写缺失的期初交易：幂等，
已含期初分录的账户不受影响，回填写入的交易不带记账人；失败只告警，不阻断启动。

##### 升级既有数据库

SqlSugar 的增量加列只会把新列补成**可空**，不会为既有行填值。因此启动时会先把历史用户行的
`is_admin` / `is_active` 回填为 `false`，即**本次升级前已注册的账号一律是「非管理员 + 未激活」**，
需由管理员在用户管理页激活后才能登录。同理，历史账户行的 `is_system` 回填为 `false`
（升级前的账户不可能是系统账户）；不补这一步，`is_system` 为 NULL 会让账户列表因无法绑定而 500。

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
