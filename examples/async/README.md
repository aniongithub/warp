# Async API Example with Memory Alpha Chat

This example demonstrates how Warp Gateway transforms synchronous APIs into asynchronous job processing with real-time notifications with no changes, only configuration. This can also allow for queue based scaling with KEDA or other scaler mechanisms.

## Architecture Flow

```mermaid
sequenceDiagram
    participant User as User
    participant Chat as Chat Server
    participant Warp as Warp Gateway
    participant Redis as Redis Queue
    participant Plasma as Plasma Processor
    participant API as Memory Alpha API

    User->>Chat: Ask question
    Chat->>Warp: POST /async/jobs/memoryalpha/rag/ask
    Warp->>Redis: Enqueue job
    Warp->>Chat: Return jobId
    Chat->>User: Show "searching..." (WebSocket)
    
    Plasma->>Redis: Dequeue job
    Plasma->>API: Execute request
    API->>Plasma: Return result
    Plasma->>Chat: Webhook with result
    Chat->>User: Display answer (WebSocket)
```

The key insight: synchronous API calls are transformed into asynchronous jobs with real-time result delivery via configurable delivery mechanisms without any change to our APIs!

## Files in this Example

- **`warp.yml`**: Main gateway configuration with sync and async routes
- **`warp.plasma.yml`**: Job processor configuration with webhook delivery
- **`datacontext.yml`**: Shared data context configuration
- **`chat/`**: Simple Memory Alpha chat interface demonstrating real-time async results
  - **`main.py`**: FastAPI server with WebSocket support (~100 lines)
  - **`requirements.txt`**: Python dependencies
- **`README.md`**: This documentation

## Quick Start

**Option 1: VS Code (Recommended)**
1. Open Run and Debug panel
2. Select "Async Chat Demo"
3. Click Run
4. Visit http://localhost:8000

**Option 2: Manual**
```bash
# Start Plasma (background job processor)
export WARP_CONFIG_BASE_DIR=./examples/async
dotnet run --project warp.plasma

# In another terminal - start chat server
cd examples/async/chat
pip install -r requirements.txt
python main.py
```

## Running the Example

### Option 1: Memory Alpha Chat Demo (Recommended)

**VS Code Compound Launch:**
1. Open "Run and Debug" panel in VS Code
2. Select "Async Chat Demo" from dropdown  
3. Click play button

This starts both Warp Plasma and the Memory Alpha chat interface at http://localhost:8000
Then visit http://localhost:8000 and chat with Memory Alpha!

## Example Configuration

This example is pre-configured with:

**warp.plasma.yml** - Job processor with webhook delivery:
```yaml
Jobs:
  memoryalpha:
    MaxConcurrentJobs: 3
    PollingIntervalMs: 2000
    Delivery:
      Type: "Warp.Core.Job.Delivery.WebhookResultDelivery, warp.core"
      Options:
        WebhookUrl: "http://localhost:8000/webhook"
```

**warp.yml** - Gateway with async routes:
```yaml
Routes:
  - Path: "/examples/async/jobs/memoryalpha/rag/ask"
    Method: POST
    Middleware:
      - Type: "Warp.Dilithium.Middleware.RedisAsyncApiHandler"
```

**chat/main.py** - FastAPI server (~100 lines total):
- WebSocket endpoint for real-time updates
- Webhook receiver for job completion
- Simple HTML chat interface

## What This Demonstrates

- **Sync to Async Transformation**: Any synchronous API becomes asynchronous with no changes
- **Job Queueing and Processing**: Configurable, transparent job queueing and processing with Warp + Plasma
- **Real-time Notifications**: Configurable delivery of results  
- **Zero code-changes**: Complex async processing hidden behind simple configuration

This shows how Warp Gateway can transform any synchronous API into a modern asynchronous system with minimal configuration.
