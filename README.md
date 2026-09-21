# Hamster

<div align="center">

A personal accounting assistant built with modern web technologies.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Vue.js](https://img.shields.io/badge/Vue.js-3.5-4FC08D.svg)](https://vuejs.org)
[![TypeScript](https://img.shields.io/badge/TypeScript-6.0-3178C6.svg)](https://www.typescriptlang.org)

[English](README.md) | [中文](README.zh-CN.md)

</div>

## Overview

Hamster is a full-stack personal accounting assistant designed to help you manage your finances with ease. The project features a modern, responsive frontend built with Vue 3 and TypeScript, backed by a high-performance ASP.NET Core API.

## Project Structure

```
Hamster/
├── api/     # ASP.NET Core backend (.NET 10 + Minimal API)
├── ui/      # Vue 3 frontend (TypeScript + Vite)
└── doc/     # Logo and icon assets
```

## Tech Stack

### Backend ([api/](api/))
- **Framework**: ASP.NET Core 10.0 (.NET 10)
- **API Style**: Minimal APIs, with endpoints auto-registered via the `IEndpoint` convention
- **OpenAPI**: `AddOpenApi()` / `MapOpenApi()` in development (`/openapi/v1.json`)
- **Data Access**: SqlSugar ORM over **SQLite** (default) or **PostgreSQL**
- **Auth**: JWT bearer tokens (1-day lifetime), passwords hashed with PBKDF2
- **Layered as**: `Config/` · `Data/` · `Security/` · `Services/` · `Endpoints/`

### Frontend ([ui/](ui/))
- **Framework**: Vue 3.5 with Composition API
- **Language**: TypeScript 6.0
- **Build Tool**: Vite 8
- **State Management**: Pinia 4
- **Routing**: Vue Router 5
- **Linting / Formatting**: ESLint + oxlint + Prettier
- **Package Manager**: pnpm

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) ^22.18.0 || >=24.12.0
- [pnpm](https://pnpm.io/) (recommended package manager)
- [PostgreSQL](https://www.postgresql.org/) (optional — SQLite is the default and needs no setup)

## Getting Started

### Backend Setup

```bash
cd api

# Run the development server (SQLite by default, no setup needed)
dotnet run

# Build the project
dotnet build

# Publish for production
dotnet publish -c Release
```

The API listens on `http://localhost:5004` by default. On startup it prints the active database
type, the (masked) connection string, the SQLite file path, and the JWT signing key.

#### Database

SQLite is the default and needs no setup — the file `hamster.db` is created in the **current
working directory** on first start. Tables are created automatically via SqlSugar CodeFirst.

```bash
# Use PostgreSQL instead (connection string is optional if it matches the default)
export HAMSTER_DB_TYPE=PostgreSql
export HAMSTER_DB_CONNECTION="Host=localhost;Port=5432;Database=hamster;Username=postgres;Password=postgres"
```

| Environment variable | Default | Description |
|---|---|---|
| `HAMSTER_DB_TYPE` | `Sqlite` | `Sqlite` or `PostgreSql` |
| `HAMSTER_DB_CONNECTION` | *(empty)* | Connection string; when empty, a default is derived from the DB type |
| `HAMSTER_DB_AUTOMIGRATE` | `true` | Run CodeFirst table creation at startup |

Environment variables always take precedence over `appsettings.json`. If auto-migration fails
(e.g. the database is unreachable) the API logs a warning and keeps starting.

Health probes: `GET /health` (liveness, no database access) and `GET /health/db` (database
connectivity, returns `503` when unavailable).

#### Authentication

Login uses JWT bearer tokens with a **fixed 1-day lifetime**. Passwords are stored as PBKDF2
(HMAC-SHA256) hashes with a random per-user salt.

```bash
# Optional: pin the signing key (otherwise a random key is generated at startup)
export HAMSTER_JWT_KEY="<at least 32 bytes of random data>"
```

Without `HAMSTER_JWT_KEY`, a random key is generated on every start and printed to the console —
tokens issued before a restart become invalid. Set the variable in any long-running deployment.

##### Default administrator

The first start seeds a system administrator so a fresh checkout is usable right away:

> [!WARNING]
> The default credentials **`admin` / `admin123`** are for out-of-the-box use only. Before
> exposing an instance to anyone else, either override them at first start (variables below) or
> log in and change the password. Seeding only ever *creates* a missing `admin` account — it
> never overwrites an existing password, so changing it once is enough.

| Environment variable | Default | Description |
|---|---|---|
| `HAMSTER_ADMIN_SEED_ENABLED` | `true` | Seed the default administrator at startup |
| `HAMSTER_ADMIN_USERNAME` | `admin` | Username to seed |
| `HAMSTER_ADMIN_PASSWORD` | `admin123` | Password to seed (used only when the account is created) |
| `HAMSTER_PUBLIC_BASE_URL` | `http://localhost:5173` | Frontend origin used to build password-reset links |

##### Account lifecycle

1. **Register** — `POST /api/auth/register` creates the account **inactive** and returns no token.
2. **Activate** — an administrator activates it from `/admin/users`. Every login before that is rejected with `403` and the message "account not activated".
3. **Log in** — `POST /api/auth/login` returns a token. Deactivating an account blocks further logins; tokens already issued remain valid until they expire (1 day).
4. **Reset a password** — an administrator generates a **15-minute, single-use** link (`POST /api/admin/users/{id}/reset-link`) and hands it to the user. It opens `/reset-password?username=…&token=…`, which requires **both** the username and the token. Only a SHA-256 hash of the token is stored, and it is cleared as soon as it is used. Every failure returns the same `400` message, so the endpoint cannot be used to probe for usernames.
5. **Delete** — `DELETE /api/admin/users/{id}` removes the account and invalidates its tokens immediately.

Administrators cannot deactivate or delete their own account. Administrator status is **never**
carried in the JWT: every admin endpoint re-reads it from the database, so revocation takes
effect at once. The same password-then-activation order applies at login — a wrong password is
rejected with `401` before the account status is even considered, so anonymous requests cannot
learn whether a username exists but is merely inactive.

| Endpoint | Auth | Description |
|---|---|---|
| `POST /api/auth/register` | — | Register (201, no token); 409 if the username is taken |
| `POST /api/auth/login` | — | Log in (200, returns a token); 401 on bad credentials, 403 when inactive |
| `GET /api/auth/me` | Bearer | Current user |
| `POST /api/auth/reset-password` | — | Set a new password from `{ username, token, newPassword }`; 400 for any invalid, expired or used link |
| `GET /api/admin/users` | Admin | List users |
| `POST /api/admin/users/{id}/activate` | Admin | Activate a user (204) |
| `POST /api/admin/users/{id}/deactivate` | Admin | Deactivate a user (204); 400 for your own account |
| `POST /api/admin/users/{id}/reset-link` | Admin | Generate a 15-minute single-use reset link |
| `DELETE /api/admin/users/{id}` | Admin | Delete a user (204); 400 for your own account |

##### Upgrading an existing database

SqlSugar appends new columns as **nullable** and does not fill them in for pre-existing rows.
On startup the API therefore backfills the `is_admin` / `is_active` flags of any older user row
to `false` — meaning **accounts created before this version start out as "not an administrator,
not activated"** and need an administrator to activate them.

### Frontend Setup

```bash
cd ui

# Install dependencies
pnpm install

# Run the development server
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

The frontend development server runs at `http://localhost:5173`; the backend's development CORS policy already allows it.

---

Copyright (c) 2026 delly.net
