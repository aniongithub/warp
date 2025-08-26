# Simple Warp Gateway Example

This example demonstrates a minimal Warp Gateway configuration that proxies requests to a MemoryAlpha RAG API service with basic rate limiting and OpenAPI validation.

## Configuration Overview

The `warp.yml` file in this directory configures:

- **Data Context**: JSON file-based storage for user data
- **Cluster**: Single destination pointing to the MemoryAlpha RAG API
- **Route**: Proxies `/examples/simple/*` to the upstream service
- **Middleware**: 
  - Rate limiting (1 request per second)
  - OpenAPI validation against the remote spec

## Running the Example

1. **Start the gateway** using the **"Warp (simple)"** launch configuration in VS Code (F5 or Run & Debug panel)

2. **Test the API** with curl:
   ```bash
   curl -G "http://localhost:5000/examples/simple/memoryalpha/rag/ask" \
     --data-urlencode "question=What is a Transporter?"
    ```

## How It Works

1. **Request arrives** at `/examples/simple/memoryalpha/rag/ask`
2. **Rate limiter** checks if user has exceeded 1 req/sec limit to avoid misuse
3. **Path transforms** remove `/examples/simple` prefixes
4. **OpenAPI validator** validates the request against the remote spec
5. **Request forwarded** to `${MEMORYALPHA_RAG_API_ADDR}/memoryalpha/rag/ask`
6. **Response returned** to client

## Environment Variables

The launch configuration automatically sets:
- `MEMORYALPHA_RAG_API_ADDR=http://memoryalpha-rag-api:8000`
- `WARP_CONFIG_BASE_DIR=./examples/simple`

You may also need:
- `MEMORYALPHA_RAG_API_VERSION`: Version tag for fetching the OpenAPI spec. This is set in our devcontainer via .env

## Key Features Demonstrated

- **Path-based routing** with prefix removal
- **Environment variable interpolation** in configuration
- **Remote OpenAPI spec loading** with version templating
- **Rate limiting** middleware
- **JSON file-based data storage** for development

This simple example shows how Warp can add cross-cutting concerns (rate limiting, validation) to existing APIs without modifying the upstream service.

