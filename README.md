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

#### Example Middleware Types

- **PermissionsChecker**: Enforces required permissions for a user or API key.
- **QuotaChecker**: Enforces prepaid/postpaid quotas per user/key and quota name.
- **RateLimiter**: Standard token bucket rate limiting per user/key.
- **JwtValidator**: Validates JWT tokens and extracts user info.
- **ApiKeyValidator**: Validates API keys and loads associated permissions.

### Configuration

All routes, clusters, and middleware are configured in `appsettings.json`. Example:

```json
{
  "ReverseProxy": {
    "Routes": {
      "stapi_basic": {
        "ClusterId": "stapi_cluster",
        "Match": { "Path": "/api/basic/{**catch-all}" },
        "Metadata": {
          "Preprocess": "OpenTelemetryStart,ApiKeyValidator,BasicRateLimiter,BasicQuotaChecker",
          "Predispatch": "StapiOpenApiValidator",
          "Postprocess": "OpenTelemetryEnd"
        }
      }
    }
  },
  "PipelineComponents": [
    {
      "Name": "BasicQuotaChecker",
      "Type": "Warp.Middleware.QuotaChecker, warp",
      "Options": {
        "QuotaName": "basic_quota",
        "QuotaUsage": 1.0,
        "QuotaLimit": 10.0,
        "CreateQuotaIfNotFound": true
      }
    }
  ]
}
```

### Quota & Billing Model

- **Quotas** are centrally named (e.g., `basic_quota`, `pro_quota`) and can be prepaid (hard-limit) or postpaid (soft-limit, bill later).
- **QuotaChecker** middleware enforces usage and can auto-create quotas for new users/keys.
- **Billing** is performed out-of-band (e.g., via a cron job that processes quota deltas or negative balances).

### Rate Limiting

- **RateLimiter** uses a token bucket algorithm for predictable, burst-tolerant rate limiting.
- Configurable per route and per user/key.

### Extending Warp

- Add new middleware by implementing `MiddlewareBase<TOptions>`.
- Register your middleware in `PipelineComponents` and reference it in route metadata.
- All middleware can access the shared `IDataContext` for user, key, quota, and request state.

## Persistence and Data Storage

- **Pluggable data context**: Use SQLite or JSON file for persistence (see `warp.core/Data/Contexts/`). You can also add new storage backends by implementing the `IDataContext` interface and placing your implementation in this directory.
- **Schema**: Users, API keys, quotas, and requests are all stored and managed centrally.

## Getting Started (Devcontainers)

Warp is designed to be developed and run easily in a [VS Code Dev Container](https://code.visualstudio.com/docs/devcontainers/containers). To get started:

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

### **Test the API**
Ensure that the "Warp API Gateway" launch configuration is active before testing and then run any of the scripts under `scripts/tests`.

These scripts will:
 - Prompt you to login to Google (`google-login.sh`)
 - Use the JWT to fetch a WARP API key for your username (`get-api-key.sh`)
 - Use the WARP API key to perform a search using the Star Trek API for "the best of both worlds" (`the-best-of-both-worlds.sh`)

You can use the OpenAPI specs in
 - `spec/stapi.yaml` 
 - `warp.apis.admin.yml`
 - `warp.apis.developer.yml`

to formulate additional commands for testing different functionalities of Warp or your API.

## Contributing

- Fork and PRs welcome!
- Please add tests and update documentation for new features.
