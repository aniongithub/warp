# warp

A config-driven, batteries-included API gateway based on Microsoft's [YARP](https://microsoft.github.io/reverse-proxy/).

## What is Warp?

Warp is a modern, lightweight, production-grade, highly-configurable API gateway built on top of Microsoft's [YARP](https://microsoft.github.io/reverse-proxy/). It is designed for:

- **Declarative API productization**: Monetize, secure, and manage APIs with minimal code.
- **Ultra-low latency**: All enforcement (auth, quota, rate limiting) is done in-process, no per-request RPCs.
- **Extensibility**: Add new middleware, billing models, or product logic easily.

## Core Concepts

### Middleware Pipeline

Warp uses a declarative, config-driven middleware pipeline. Each route can specify a sequence of middleware components for:

- **Preprocess**: Runs before YARP transforms (auth, quota, rate limiting, etc.)
- **Predispatch**: Runs before dispatching to the backend (e.g., OpenAPI validation)
- **Postprocess**: Runs after backend response (logging, tracing, etc.)

Middlewares are registered in `appsettings.json` under `PipelineComponents` and referenced by name in route metadata.

### Extending Warp

- Add new middleware by implementing `MiddlewareBase<TOptions>`.
- Register your middleware in `PipelineComponents` and reference it in route metadata.
- All middleware can access the shared `IDataContext` for user, key, quota, and request state.

### Configuration

All routes, clusters, and middleware are configured in `config/warp.yml` with support for includes and automatic environment variable expansion.

## Features & Examples

This architecture allows us to put Warp together in a variety of ways with pure config changes. Here are some examples that demonstrate one or more specific Warp Gateway applications via pure configuration:

- **[Simple](examples/simple/README.md)** - Minimal API Gateway configuration with basic rate limiting and OpenAPI validation
- **[JWT/API Key auth & Developer API](examples/apikeys/README.md)** - JWT and/or API key authentication with developer portal key management
- **[Quotas & Permissions](examples/quotas/README.md)** - Permission-based quota tracking with usage-monitoring.
- **[Async-ification of synchronous APIs](examples/async/README.md)** - Transform synchronous APIs to asynchronous job processing with real-time notifications and end-to-end OpenTelemetry tracking

## Persistence and Data Storage

- **Pluggable data context**: Use SQLite, Postgres, Firebase or a simple JSON file for persistence (see `warp.core/Data/Contexts/`). You can also add new storage backends by implementing the `IDataContext` interface.
- **Schema**: Users, API keys, quotas, and requests are all stored and managed centrally.

## Getting Started (Devcontainers)

Warp is designed to be developed and run in a [VS Code Dev Container](https://code.visualstudio.com/docs/devcontainers/containers). To get started:

To use this environment, you should open the repository in [Visual Studio Code](https://code.visualstudio.com/) with the [Dev Containers extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers). This ensures all required tools and dependencies are automatically installed and configured.

**Steps:**

1. **Install VS Code** and the [Dev Containers extension](https://code.visualstudio.com/docs/devcontainers/containers).
2. **Clone this repository** to your local machine.
3. **Open the folder in VS Code**. When prompted, select **"Reopen in Container"** to launch the devcontainer.
4. The environment will build automatically, installing all dependencies and tools as defined in `.devcontainer/`.

For more details, see the [VS Code Dev Containers documentation](https://code.visualstudio.com/docs/devcontainers/containers).

### **Development**

#### Using VS Code (Recommended)

- Use the VS Code Run/Debug panel and select the **Warp API Gateway** compound configuration to launch the main gateway, Developer API, and Admin API projects together.
- To launch the developer console UI, select the **Developer Console** compound configuration. This will start both the static server and a Chrome browser for front-end debugging.
- This approach ensures all services are started and debugged in an integrated environment.

#### VS Code Launch Configurations

This repository includes pre-configured launch settings for development and debugging:

- **Warp API Gateway**: Launches the main gateway (`Warp`), Developer API (`DevApi`), and Admin API (`AdminApi`) projects together for a full backend environment.

You can select these compound configurations from the VS Code Run/Debug panel to start all related services at once. This also means you can use a single VS code window to debug flow all the way from the developer console to the middleware in your API gateway seamlessly.

## Contributing

- Fork and PRs welcome!
- Please add tests and update documentation for new features.
