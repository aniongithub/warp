# API Keys Example

This example demonstrates how to use Warp Gateway with API key authentication and the Developer API for key management. It shows how to protect APIs with API keys and manage those keys through a developer portal.

## Configuration Overview

This example includes:

- **API Key Authentication**: Protected MemoryAlpha API requiring valid API keys
- **Developer API**: Management interface for creating and managing API keys
- **User Permissions**: Role-based access with "developer" permissions required
- **OpenAPI Validation**: Both APIs validate requests against their specs

## Running the Example

1. **Start the services** using the **"Warp API Gateway"** compound launch configuration in VS Code, which starts:
   - Warp Gateway (port 5000)
   - Developer API (port 5002) 
   - Admin API (port 5003)
   - Plasma service

   Or run individual services with their respective launch configurations.

## Getting an API Key

### Step 1: Get an API Key from the Developer API

First, you need to authenticate and get an API key:

```bash
# Create a new API key (will auto-create user with "developer" permissions)
curl -X POST "http://localhost:5000/developer/api-keys" \
  -H "X-JWT-Email: user@example.com"

# Or get existing API keys
curl "http://localhost:5000/developer/api-keys" \
  -H "X-JWT-Email: user@example.com"
```

This will return something like:
```json
{
  "id": "abc123",
  "key": "1234567890abcdef",
  "owner": "user@example.com",
  "isActive": true,
  "permissions": ["developer", "user"]
}
```

### Step 2: Use the API Key to Access Protected Endpoints

Now you can use the API key to access the MemoryAlpha API:

```bash
# Using your API key to ask a question
curl -G "http://localhost:5000/examples/simple/memoryalpha/rag/ask" \
  -H "X-Api-Key: 1234567890abcdef" \
  --data-urlencode "question=What is a Transporter?"
```

### Step 3: Try Without API Key (Will Fail)

To see the protection in action, try calling the API without an API key (or an incorrect API key):

```bash
# This will fail with 401 Unauthorized
curl -G "http://localhost:5000/examples/simple/memoryalpha/rag/ask" \
  --data-urlencode "question=What is a Transporter?"
```

## API Key Management

### List Your API Keys
```bash
curl "http://localhost:5000/developer/api-keys" \
  -H "X-JWT-Email: user@example.com"
```

### Check API Key Permissions
```bash
curl "http://localhost:5000/developer/api-keys/{key-id}/permissions" \
  -H "X-JWT-Email: user@example.com"
```

### Deactivate an API Key
```bash
curl -X DELETE "http://localhost:5000/developer/api-keys/{key-id}" \
  -H "X-JWT-Email: user@example.com"
```

See the OpenAPI Spec for the warp developer API to understand more.

## How It Works

### API Key Flow
1. **Request arrives** with `X-Api-Key` header at `/developer/api-keys`
2. **Rate limiter** applies limits for this route to prevent misuse
3. **ApiKeyValidator** middleware validates the key and loads associated permissions
4. **OpenAPI validator** validates the request after transforming the path to the canonical one in the OpenAPI spec
5. **Request forwarded** to MemoryAlpha API
6. **Response returned** to client

### Developer API Flow
1. **Request arrives** with `X-JWT-Email` header (simulating JWT authentication)
2. **PermissionsChecker** validates user has "developer" permission
3. **OpenAPI validator** validates against Developer API spec
4. **API key operations** performed (create, list, update, delete)

## Key Features Demonstrated

- **API Key Authentication**: Protecting APIs with key-based access
- **Developer Portal**: Self-service API key management
- **Permission-based Access**: Role-based protection ("developer" permission required)
- **Rate Limiting**: Per-key rate limiting (1 req/sec in this example)
- **OpenAPI Validation**: Request/response validation for both APIs
- **Multi-service Architecture**: Gateway + Developer API + Protected API

This example shows how Warp can provide a complete API management solution with authentication, authorization, rate limiting, and developer self-service capabilities.