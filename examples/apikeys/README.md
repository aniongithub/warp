# API Keys & JWT Authentication Example

This example demonstrates how to use Warp Gateway with both JWT and API key authentication patterns. It shows how to protect APIs with multiple authentication methods and manage API keys through a JWT-protected developer portal.

## Configuration Overview

This example includes:

- **JWT Authentication**: Developer API protected by Google OAuth2 JWT tokens with JWKS validation
- **API Key Authentication**: MemoryAlpha API accessible via API keys obtained from the Developer API  
- **Flexible Authentication**: MemoryAlpha API accepts EITHER JWT tokens OR API keys (OrMiddleware)
- **User Permissions**: Role-based access with "developer" permissions for portal access
- **OpenAPI Validation**: Both APIs validate requests against their specs
- **Auto User Creation**: Users automatically created from valid JWT claims

## Running the Example

1. **Start the services** using the **"Warp API Gateway"** compound launch configuration in VS Code, which starts:
   - Warp Gateway (port 5000)
   - Developer API (port 5002) 

   Or run individual services with their respective launch configurations.

## Authentication Methods

This example uses gcloud CLI to create JWts

### Method 1: JWT Authentication (For both Developer API and MemoryAlpha API)

First, get a Google OAuth2 JWT token:

```bash
# Get JWT token using Google Cloud CLI (for testing)
export JWT_TOKEN=$(gcloud auth print-identity-token --audiences=memoryalpha-dev)

# Access Developer API with JWT
curl -X POST "http://localhost:5000/developer/api-keys" \
  -H "Authorization: Bearer $JWT_TOKEN"

# Access MemoryAlpha API directly with JWT (different audience)
export API_JWT_TOKEN=$(gcloud auth print-identity-token --audiences=memoryalpha-api)
curl -G "http://localhost:5000/examples/apikeys/memoryalpha/rag/ask" \
  -H "Authorization: Bearer $API_JWT_TOKEN" \
  --data-urlencode "question=What is a Transporter?"
```

### Method 2: API Key Authentication (For MemoryAlpha API only)

#### Step 1: Get an API Key from the JWT-Protected Developer API

```bash
# Create a new API key using JWT
export JWT_TOKEN=$(gcloud auth print-identity-token --audiences=memoryalpha-dev)
curl -X POST "http://localhost:5000/developer/api-keys" \
  -H "Authorization: Bearer $JWT_TOKEN"

# Or get existing API keys
curl "http://localhost:5000/developer/api-keys" \
  -H "Authorization: Bearer $JWT_TOKEN"
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

#### Step 2: Use the API Key to Access MemoryAlpha API

```bash
# Using your API key to ask a question
curl -G "http://localhost:5000/examples/apikeys/memoryalpha/rag/ask" \
  -H "X-Api-Key: 1234567890abcdef" \
  --data-urlencode "question=What is a Transporter?"
```

### Testing Authentication Failures

```bash
# This will fail with 401 Unauthorized (no authentication)
curl -G "http://localhost:5000/examples/apikeys/memoryalpha/rag/ask" \
  --data-urlencode "question=What is a Transporter?"

# This will fail with 401 Unauthorized (invalid JWT)
curl -G "http://localhost:5000/examples/apikeys/memoryalpha/rag/ask" \
  -H "Authorization: Bearer invalid-token" \
  --data-urlencode "question=What is a Transporter?"

# Developer API requires JWT (API keys won't work)
curl -X POST "http://localhost:5000/developer/api-keys" \
  -H "X-Api-Key: 1234567890abcdef"  # This will fail
```

## API Key Management (JWT Required)

All Developer API operations require JWT authentication:

### List Your API Keys
```bash
export JWT_TOKEN=$(gcloud auth print-identity-token --audiences=memoryalpha-dev)
curl "http://localhost:5000/developer/api-keys" \
  -H "Authorization: Bearer $JWT_TOKEN"
```

### Check API Key Permissions
```bash
curl "http://localhost:5000/developer/api-keys/{key-id}/permissions" \
  -H "Authorization: Bearer $JWT_TOKEN"
```

### Deactivate an API Key
```bash
curl -X DELETE "http://localhost:5000/developer/api-keys/{key-id}" \
  -H "Authorization: Bearer $JWT_TOKEN"
```

See the OpenAPI Spec for the Warp Developer API to understand more.

## How It Works

### JWT Authentication Flow (Developer API)
1. **Request arrives** with `Authorization: Bearer <jwt>` header
2. **JwtValidator** middleware:
   - Validates JWT signature using Google's JWKS endpoint
   - Checks audience and issuer claims
   - Extracts user email and creates user if not found
   - Sets default permissions ("developer", "user")
   - Adds `X-JWT-Email` header for downstream use
3. **PermissionsChecker** validates user has "developer" permission
4. **OpenAPI validator** validates against Developer API spec
5. **API key operations** performed (create, list, update, delete)

### Flexible Authentication Flow (MemoryAlpha API)
1. **Request arrives** at MemoryAlpha API endpoint
2. **Rate limiter** applies limits to prevent abuse
3. **OrMiddleware** tries authentication methods in order:
   - **Option A**: JWT validation (same as Developer API but different audience)
   - **Option B**: API key validation using `X-Api-Key` header
   - **Success**: If either method succeeds, request continues
   - **Failure**: If both methods fail, request is rejected (401)
4. **OpenAPI validator** validates the request
5. **Request forwarded** to MemoryAlpha API
6. **Response returned** to client

### User Auto-Creation
- **JWT users**: Automatically created with email from JWT claims
- **API key users**: Already exist (created when JWT user generated the key)

## Key Features Demonstrated

### Authentication & Authorization
- **JWT Authentication**: Google OAuth2 tokens with JWKS signature validation
- **API Key Authentication**: Traditional API key-based access
- **Flexible Authentication**: OrMiddleware accepting either JWT or API key
- **Auto User Creation**: Automatic user provisioning from JWT claims
- **Permission-based Access**: Role-based protection ("developer" permission required)

### Security & Validation
- **JWKS Integration**: Real-time public key fetching from Google
- **Audience Validation**: Different JWT audiences for different APIs
- **Signature Verification**: Cryptographic validation of JWT tokens
- **OpenAPI Validation**: Request/response validation for both APIs
- **Rate Limiting**: Per-user/key rate limiting (1 req/sec in this example)

### Architecture Patterns
- **Developer Portal**: JWT-protected self-service API key management
- **Multi-Authentication Gateway**: Single API accepting multiple auth methods
- **Multi-service Architecture**: Gateway + Developer API + Protected API
- **Claims-to-Headers**: JWT claims automatically mapped to request headers

This example shows how Warp can provide a comprehensive API management solution that supports modern JWT-based authentication while maintaining backward compatibility with traditional API keys. The OrMiddleware pattern is particularly powerful for API migration scenarios where you need to support multiple authentication methods simultaneously.