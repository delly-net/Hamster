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

## Tech Stack

### Backend ([Hamster/](Hamster/))
- **Framework**: ASP.NET Core 10.0
- **Language**: C# 12+
- **API Style**: Minimal APIs with OpenAPI/Swagger support
- **Features**:
  - AOT (Ahead-of-Time) compilation for optimal performance
  - OpenAPI documentation in development
  - Built-in JSON serialization with source generation

### Frontend ([Vue/](Vue/))
- **Framework**: Vue 3.5 with Composition API
- **Language**: TypeScript 6.0
- **Build Tool**: Vite 8.0
- **State Management**: Pinia 3.0
- **Routing**: Vue Router 5.0
- **Package Manager**: pnpm

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) ^20.19.0 || >=22.12.0
- [pnpm](https://pnpm.io/) (recommended package manager)

## Getting Started

### Backend Setup

Navigate to the backend directory and run the development server:

```bash
cd Hamster

# Run the development server
dotnet run

# Build the project
dotnet build

# Publish for production
dotnet publish -c Release
```

The backend API will be available at `http://localhost:5004` by default.

### Frontend Setup

Navigate to the frontend directory and install dependencies:

```bash
cd Vue

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

The frontend development server will run at `http://localhost:5173`.

---

Copyright (c) 2026 delly.net