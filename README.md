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
- **Data Access**: SqlSugar ORM over PostgreSQL (via Npgsql)
- **Layered as**: `Config/` · `Data/` · `Services/` · `Endpoints/`

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
- [PostgreSQL](https://www.postgresql.org/) (optional for scaffolding work, required for data-backed endpoints)

## Getting Started

### Backend Setup

```bash
cd api

# Configure the database connection (or edit api/appsettings.json)
export HAMSTER_DB_CONNECTION="Host=localhost;Port=5432;Database=hamster;Username=postgres;Password=postgres"

# Run the development server
dotnet run

# Build the project
dotnet build

# Publish for production
dotnet publish -c Release
```

The API listens on `http://localhost:5004` by default.

Database behaviour:

- Tables are **not** created automatically by default. Set `Database:AutoMigrate=true` (or the `HAMSTER_DB_AUTOMIGRATE=true` environment variable) to run SqlSugar CodeFirst table creation at startup.
- Health probes: `GET /health` (liveness, no database access) and `GET /health/db` (PostgreSQL connectivity, returns `503` when unavailable).

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
