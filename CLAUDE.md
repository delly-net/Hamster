# Hamster

A personal accounting assistant built with ASP.NET Core backend and Vue 3 frontend.

## Project Structure

```
Hamster/
├── Hamster/                      # ASP.NET Core backend (C#)
│   ├── Constant/                 # Global constants
│   │   ├── SystemConfigConst.cs  # System configuration constants
│   │   └── LogMessageConst.cs    # Log message constants
│   ├── Modules/                  # Business modules (所有业务代码)
│   │   ├── <功能模块>/           # Feature module directory
│   │   │   ├── <功能模块>Router.cs    # Module router
│   │   │   └── <功能分类>/       # Feature category directory
│   │   │       ├── <功能分类>Router.cs    # Category router
│   │   │       ├── <功能分类>Entity.cs    # Category entities
│   │   │       ├── Constant/     # Category constants
│   │   │       ├── Service/      # Service implementations
│   │   │       └── Controller/   # API controllers
│   │   └── Example/              # Example module
│   │       ├── ExampleRouter.cs
│   │       └── Simple/           # Example category
│   │           ├── SimpleRouter.cs
│   │           ├── SimpleEntity.cs
│   │           ├── Constant/
│   │           │   ├── SimpleApiNameConst.cs
│   │           │   └── SimpleApiPathConst.cs
│   │           ├── Service/
│   │           │   └── TodoService.cs
│   │           └── Controller/
│   │               └── TodoController.cs
│   ├── Program.cs                # Main application entry point
│   ├── appsettings.json          # Application configuration
│   └── Hamster.csproj            # Project configuration (.NET 10.0)
├── Vue/                          # Vue 3 frontend (TypeScript)
│   ├── src/
│   │   ├── components/           # Vue components
│   │   │   ├── HelloWorld.vue
│   │   │   ├── TheWelcome.vue
│   │   │   ├── WelcomeItem.vue
│   │   │   └── icons/            # Icon components
│   │   ├── views/                # Page components
│   │   │   ├── HomeView.vue
│   │   │   └── AboutView.vue
│   │   ├── router/               # Vue Router configuration
│   │   │   └── index.ts
│   │   ├── stores/               # Pinia state management
│   │   │   └── counter.ts
│   │   ├── assets/               # Static assets
│   │   ├── App.vue               # Root component
│   │   └── main.ts               # Application entry point
│   ├── vite.config.ts            # Vite build configuration
│   ├── tsconfig.json             # TypeScript configuration
│   └── package.json              # Dependencies and scripts
└── .github/workflows/            # GitHub Actions
```

## Tech Stack

### Backend (Hamster)
- **Framework**: ASP.NET Core 10.0
- **Language**: C# (.NET 10.0)
- **ORM**: SqlSugar
- **Database**: SQLite (default: `hamster.db`)
- **Logging**: Serilog (Console + File with daily rotation)
- **API**: Minimal APIs with OpenAPI support
- **Build**: .NET SDK with AOT compilation enabled

### Frontend (Vue)
- **Framework**: Vue 3.5 with Composition API (`<script setup>`)
- **Language**: TypeScript 6.0
- **Build Tool**: Vite 8.0
- **State Management**: Pinia 3.0
- **Routing**: Vue Router 5.0
- **Dev Tools**: Vue DevTools plugin
- **Package Manager**: pnpm
- **Linting**: ESLint + oxlint
- **Formatting**: Prettier

## Development Commands

### Backend (Hamster/)
```bash
# Run development server
dotnet run

# Build
dotnet build

# Publish
dotnet publish -c Release

# Restore dependencies
dotnet restore
```

### Frontend (Vue/)
```bash
# Install dependencies
pnpm install

# Run dev server (http://localhost:5173)
pnpm dev

# Type check
pnpm type-check

# Build for production
pnpm build

# Lint and fix (oxlint + eslint)
pnpm lint

# Format code
pnpm format

# Preview production build
pnpm preview
```

## Environment Variables

### Backend
| Variable | Description | Default |
|----------|-------------|---------|
| `DB_CONNECTION_STRING` | SQLite database connection string | `Data Source=hamster.db` |
| `LOG_PATH` | Log file directory | `logs` |

## Backend Architecture

### Module Structure
所有业务代码存储在 `Modules/` 目录下，按照**功能模块**/**功能分类**两级目录组织。

### Module Component Structure
每个**功能分类**目录包含：
- `<功能分类>Router.cs` - 分类路由注册类
- `<功能分类>Entity.cs` - 分类实体定义
- `Constant/` - 分类常量目录
- `Service/` - 服务实现目录
- `Controller/` - 控制器目录

### Router Registration Flow
```
Program.cs -> <功能模块>Router.Register -> <功能分类>Router.Register
```

### API Route Naming
- **规则**: `/<功能模块>/<功能分类>/<功能>/<接口>`
- **格式**: 小写字母加横杠（kebab-case）
- **示例**: `/example/simple/todo/get-by-id`

### Global Constants
- `SystemConfigConst` - 系统配置常量（数据库、日志相关）
- `LogMessageConst` - 日志消息常量

### Dependencies
- SqlSugarClient registered as singleton
- Serilog configured for console and file output
- OpenAPI enabled in development mode

## Frontend Architecture

### Routing
- `/` - Home page (TheWelcome component)
- `/about` - About page

### State Management
- `counter` store - Example Pinia store with count state

### Path Aliases
- `@` resolves to `./src`

## Key Configuration

### Backend
- **Port**: Defined in `Properties/launchSettings.json`
- **OpenAPI**: Available at `/openapi/v1.json` in development
- **Log Format**: Daily rolling files (`log-.txt`)
- **Log Level**: Information

### Frontend
- **Dev Server**: http://localhost:5173
- **Node Version**: ^20.19.0 || >=22.12.0
- **TypeScript**: Strict mode enabled

## Code Style

### C# Naming Conventions
- 所有常量遵循大写下划线格式命名（UPPER_SNAKE_CASE）
- 所有常量定义类命名以 `Const` 结尾
- 所有Service类命名以 `Service` 结尾
- 所有Controller类命名以 `Controller` 结尾
- Router类命名以 `Router` 结尾
- Entity类命名以 `Entity` 结尾

### C# Code Standards
- 使用 `Service` 类进行实际逻辑实现
- 使用 `Controller` 类进行接口定义，并调用 `Service` 实现功能
- 使用 `record` 关键字定义实体对象
- 字符串要进行常量定义（路径、名称定义、特性定义除外）
- `if` 语句判断后的语句必须用大括号包裹，只有一行代码也不能省略
- 命名空间要与目录结构完全吻合
- 除非对话中明确要求，其他情况不允许额外添加依赖包
- 项目需要进行AOT编译

### C# Comment Standards
- 所有 `public` 对象都需要使用 `///` 进行XML文档化注释
- XML文档化注释中的 `param` 信息要匹配实际参数
- 所有 `private` 对象只需要使用 `//` 进行注释，减少代码篇幅
- 注释/符号与注释内容之间留有一个空格

### TypeScript/Vue
- Single File Components with `<script setup>` syntax
- Strict type checking
- ESLint with Vue and TypeScript rules
- Prettier for code formatting

## License

MIT License - Copyright (c) 2026 delly.net