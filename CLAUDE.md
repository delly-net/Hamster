# Hamster

A personal accounting assistant built with ASP.NET Core backend and Vue 3 frontend.

## Project Structure

```
Hamster/
├── Hamster/              # ASP.NET Core backend (C#)
│   ├── Program.cs        # Main application entry point
│   ├── Hamster.csproj    # Project configuration (.NET 10.0)
│   ├── Properties/       # Launch settings and configuration
│   ├── Config/           # Application configuration
│   ├── Modules/          # Feature modules
│   │   └── Todo/         # Todo module
│   │       ├── Constant/ # Todo constants
│   │       ├── Service/  # Todo services
│   │       └── Controller/ # Todo controllers
│   └── logs/             # Log files directory
├── Vue/                  # Vue 3 frontend (TypeScript)
│   ├── src/
│   │   ├── components/   # Vue components
│   │   ├── views/        # Page components
│   │   ├── router/       # Vue Router configuration
│   │   ├── stores/       # Pinia state management
│   │   └── assets/       # Static assets
│   ├── vite.config.ts   # Vite build configuration
│   └── package.json     # Dependencies and scripts
└── .github/workflows/    # GitHub Actions (empty)
```

## Tech Stack

### Backend (Hamster)
- **Framework**: ASP.NET Core 10.0
- **Language**: C#
- **API**: Minimal APIs with OpenAPI/Swagger support
- **Build**: .NET SDK with AOT compilation enabled

### Frontend (Vue)
- **Framework**: Vue 3.5 with Composition API (`<script setup>`)
- **Language**: TypeScript 6.0
- **Build Tool**: Vite 8.0
- **State Management**: Pinia 3.0
- **Routing**: Vue Router 5.0
- **Package Manager**: pnpm

## Development Commands

### Backend (Hamster/)
```bash
# Run development server
dotnet run

# Build
dotnet build

# Publish
dotnet publish -c Release
```

### Frontend (Vue/)
```bash
# Install dependencies
pnpm install

# Run dev server
pnpm dev

# Type check
pnpm type-check

# Build for production
pnpm build

# Lint and fix
pnpm lint

# Format code
pnpm format

# Preview production build
pnpm preview
```

## Current API Endpoints

- `GET /todos` - Get all todo items
- `GET /todos/{id}` - Get todo by ID

## Key Configuration

- **Backend Port**: Defined in `Properties/launchSettings.json`
- **Backend URL**: OpenAPI available at `/openapi/v1.json` in development
- **Frontend Dev Server**: Vite default (http://localhost:5173)
- **TypeScript**: Path alias `@` resolves to `./src`
- **Node Version**: ^20.19.0 || >=22.12.0

## Code Style

### Backend
- **Database**: Use Sqlite with SqlSugar ORM, support environment variable for connection string
- **Logging**: Use Serilog with environment variable for log directory
- **Architecture**:
  - Use modular code structure in Modules directory
  - Organize by feature module/feature category hierarchy
  - Constants stored in corresponding Constant directory by scope
  - Each feature category maintains Service/Controller subdirectories
  - Each feature category maintains its own AppJsonSerializerContext with Entity definitions
  - Use MVC architecture for Controllers
- **Naming**:
  - Constants: UPPER_SNAKE_CASE, classes end with `Const`
  - Service classes: Must end with `Service`
  - Controller classes: Must end with `Controller`
  - Namespaces must match directory structure
- **Code Style**:
  - Use Service classes for business logic implementation
  - Use Controller classes for API definition, calling Service for functionality
  - String literals must be defined as constants (paths, names, attribute definitions excluded)
  - All if statements must use braces, even for single-line blocks
  - No extra dependencies unless explicitly requested
  - AOT compilation required
  - Report issues with unreasonable requirements before proceeding
- **Comments**:
  - Public objects: Use `///` XML documentation comments
  - XML param comments must match actual parameters
  - Private objects: Use `//` single-line comments only
  - Space between comment symbol and content

### Frontend
- **C#**: Nullable reference types enabled, implicit usings enabled
- **TypeScript**: Strict type checking via `tsconfig.json`
- **Linting**: ESLint + oxlint with Prettier formatting
- **Vue**: Single File Components with `<script setup>` syntax

## License

MIT License - Copyright (c) 2026 delly.net
