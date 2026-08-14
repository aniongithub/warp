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
- **Postdispatch**: Runs after getting a result from the backend, but before sendind a response to the client (usage-monitoring)
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
- **[Quotas & Permissions](examples/quotas/README.md)** - Flexible, permission-based quota tracking with usage-monitoring.
- **[Async-ification of synchronous APIs](examples/async/README.md)** - Transform synchronous APIs to asynchronous job processing with real-time notifications and end-to-end OpenTelemetry tracking
- **[Monetization with Stripe](examples/monetization/README.md)** - Monetize API usage with Stripe: one-time credit purchases (prepaid quota) and recurring subscriptions (postpaid plan quota), reconciled via auto-registered webhooks

## Security & Trust Boundary

Warp's control plane assumes the gateway is the single authenticated front door and that the internal APIs sit behind it. The following gates harden that boundary and are **on by default (fail closed)**. Each has a documented escape hatch for trusted-network deployments that logs a loud warning when used.

| Area | Default behavior | Config knob (section) | Env var |
| --- | --- | --- | --- |
| **Gateway JWT** (`warp.dilithium` `JwtValidator`) | Signature validation is **required**; tokens are rejected unless they verify against JWKS or a symmetric secret. JWKS keys are cached in-process and refreshed on an interval instead of fetched per request. | `ValidateSigningKey` (true), `JwksCacheLifetimeSeconds` (3600), `AllowUnsignedTokensInsecure` (false, dev-only) under each `JwtValidator` `Options` | — |
| **Admin API** (`warp.apis.admin`) | Every request except `/admin/health` must present a shared secret; missing/invalid → 401, gate enabled but no key configured → 503. | `AdminAuth:` (`Enabled`, `HeaderName` = `X-Admin-Api-Key`, `ApiKey`) | `ADMIN_API_KEY` |
| **Developer API** (`warp.apis.developer`) | `X-JWT-Email` / `X-Permissions` (and other identity headers) are **stripped** from any request that is not from a trusted upstream — loopback, or one carrying the gateway marker header. Defeats header spoofing when the API is reachable directly. | `GatewayTrust:` (`Enabled`, `HeaderName` = `X-Gateway-Auth`, `SharedSecret`) | `GATEWAY_SHARED_SECRET` |
| **Stripe webhooks** (`warp.latinum`) | Inbound webhooks are verified against the `Stripe-Signature` header and signing secret; failures → 400, missing secret → 500. | Stripe controller `Options` (`VerifySignature`, `WebhookSecret`, `PaymentWebhookSecret`, `SubscriptionWebhookSecret`, `SignatureToleranceSeconds`, `AllowUnverifiedWebhooksInsecure`) | `STRIPE_WEBHOOK_SECRET`, `STRIPE_PAYMENT_WEBHOOK_SECRET`, `STRIPE_SUBSCRIPTION_WEBHOOK_SECRET` |

**Deployment notes:**

- **Admin API:** set `ADMIN_API_KEY` and have callers send it in `X-Admin-Api-Key`. For a genuinely isolated network you may set `AdminAuth:Enabled: false` (logs a warning).
- **Developer API:** the guard trusts loopback automatically. For cross-host deployments, configure `GATEWAY_SHARED_SECRET` here **and** have the gateway inject the same value in `X-Gateway-Auth` (e.g. via a YARP request transform on the developer route). Without a secret, only loopback callers may supply identity headers. This gateway-injection step is an operator responsibility (documented assumption); everything else is enforced in code.
- **Stripe webhooks:** set `STRIPE_WEBHOOK_SECRET` (and the per-endpoint secrets if the payment and subscription webhooks differ). `AllowUnverifiedWebhooksInsecure` is dev/test only.

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
