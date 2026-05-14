# Hamster

A personal accounting assistant built with ASP.NET Core backend and Vue 3 frontend.

## Project Structure

```
Hamster/
├── Hamster/                      # ASP.NET Core backend (C#)
│   ├── Config/                   # Application configuration
│   │   ├── AppSettings.cs        # Configuration classes (Database, Log)
│   │   └── Constant/             # Constants
│   │       ├── ConfigConst.cs    # Configuration constants (env vars, defaults)
│   │       └── SampleDataConst.cs # Log message constants
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

### Namespaces
- `Hamster.Config` - Configuration classes
- `Hamster.Constant` - Application constants

### Configuration Classes
- `AppSettings` - Root configuration with Database and Logging properties
- `DatabaseConfig` - Database connection settings
- `LogConfig` - Logging directory settings

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

### C#
- Nullable reference types enabled
- Implicit usings enabled
- XML documentation comments for public APIs
- Namespace-based organization (Config, Constant)

### TypeScript/Vue
- Single File Components with `<script setup>` syntax
- Strict type checking
- ESLint with Vue and TypeScript rules
- Prettier for code formatting

## License

MIT License - Copyright (c) 2026 delly.net