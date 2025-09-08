# Quotas Example

This example demonstrates how to use Warp Gateway with quota restriction and tracking to ensure we can monetize our APIs. It includes configurations for "prepaid" (decrement an already assigned quota) and "postpaid" (tracking a user we will bill later) quotas.

## Configuration Overview

This example includes:

- **Quota tracking**: Protected free and pro routes that show two different kinds of monetization quotas
- **API Key Authentication**: Protected MemoryAlpha API requiring valid API keys
- **Developer API**: End-user accessible management interface for creating and managing API keys
- **Admin API**: Administration API that is not accessible by end-users, for managing users, quotas, billing, etc.
- **User Permissions**: Role-based access with "developer" permissions required
- **OpenAPI Validation**: Both APIs validate requests against their specs

## Running the Example

1. **Start the services** using the **"Warp API Gateway"** compound launch configuration in VS Code, which starts:
   - Warp Gateway (port 5000)
   - Developer API (port 5002) 
   - Admin API (port 5003)

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
  "key": "warp_1234567890abcdef",
  "owner": "user@example.com",
  "isActive": true,
  "permissions": ["developer", "user"]
}
```

### Step 2: Use the API Key to Access our endpoints

Now you can use the API key to access the free MemoryAlpha API:

```bash
# Using your API key to ask a question
curl -G "http://localhost:5000/examples/quotas/free/memoryalpha/rag/ask" \
  -H "X-Api-Key: 1234567890abcdef" \
  --data-urlencode "question=What is the color of Vulcan blood?"
```

After running this command one or more times, you will see that the quota usage for this user has been set/incremented
```json
  ...
  "quotas": [
    {
      "id": "7a6c43e1-d170-429b-8833-ada343ec92ea",
      "key": "user@example.com",
      "quotaName": "free_quota",
      "used": 2,
      "limit": 10,
      "type": "prepaid"
    }
  ]
  ...
```
Since this is a "prepaid" quota type, if you run this more than 10 times, any subsequent requests will be rejected. 
Note: If this were a "postpaid" quota, it would simply accrue usage for later billing.

### Step 3: Reset the quota (assume the user bought more usage tokens)

Now let's reset our own quota by manually giving ourselves admin permissions

```json
  "users": [
    {
      "id": "8651ca50-1d92-4fad-a8c9-db61291f7e86",
      "email": "user@example.com",
      "permissions": [
        "developer",
        "user",
        "admin" // <---- Add this and save data/data.json
      ]
    }
  ],
  ...
```
Now we can run a curl command to get the user's quota
Note: It's our own for this example, but we could technically do this with any user

```bash
curl -X GET "http://localhost:5000/admin/users/user@example.com/quotas" \
  -H "X-JWT-Email: user@example.com"
```
This will return 
```json
[
  {
    "key": "user@example.com",
    "quotaName": "free_quota",
    "used": 10,
    "limit": 10,
    "type": "prepaid",
    "id": "<quota-id>"
  }
]

```
Now we can (re)set the quota value using the quota id
```bash
curl -X PUT "http://localhost:5003/admin/users/user@example.com/quotas/<quota-id>/usage" \
  -H "Content-Type: application/json" \
  -H "X-JWT-Email: admin@example.com" \
  -d "0.0"
```
And we can use our free API key 10 more times! We can also run cron jobs that resets user quotas at the beginning of every month, etc.

What's most important to note is that without knowing anything about our API or its usage model, Warp can model any kind of quota restrictions we would need. Other examples will show how Warp's payment middlewares can do this automatically for us when connected to a payment gateway.

See the OpenAPI Spec for the warp admin API to understand more.

## How It Works

### Quota-Protected API Flow
1. **Request arrives** with `X-Api-Key` header at `/examples/quotas/free/memoryalpha/rag/ask`
2. **ApiKeyValidator** middleware validates the key and loads associated user permissions
3. **QuotaChecker** middleware checks if user has sufficient quota for the request:
   - For "prepaid" quotas: Verifies `used < limit`, increments usage on success
   - For "postpaid" quotas: Simply tracks usage for later billing
4. **Rate limiter** applies per-key rate limits (1 req/sec in this example)
5. **OpenAPI validator** validates the request against the MemoryAlpha API spec
6. **Request forwarded** to MemoryAlpha API if all checks pass
7. **Response returned** to client, quota usage updated in data store

### Developer API Flow (API Key Management)
1. **Request arrives** with `X-JWT-Email` header (simulating JWT authentication)
2. **PermissionsChecker** validates user has "developer" permission
3. **OpenAPI validator** validates against Developer API spec
4. **API key operations** performed (create, list, update, delete)

### Admin API Flow (Quota Management)
1. **Request arrives** with `X-JWT-Email` header for admin operations
2. **PermissionsChecker** validates user has "admin" permission (for quota modification)
3. **OpenAPI validator** validates against Admin API spec
4. **Quota operations** performed (view user quotas, reset usage, update limits)

## Key Features Demonstrated

- **Quota Management**: Both "prepaid" (hard limits) and "postpaid" (usage tracking) quota models
- **API Key Authentication**: Protecting APIs with key-based access
- **Admin Operations**: Administrative API for quota management and user administration
- **Developer Portal**: Self-service API key management
- **Permission-based Access**: Role-based protection ("developer" and "admin" permissions)
- **Rate Limiting**: Per-key rate limiting (1 req/sec in this example)
- **OpenAPI Validation**: Request/response validation for all APIs
- **Multi-service Architecture**: Gateway + Developer API + Admin API + Protected API
- **Usage Tracking**: Real-time quota consumption tracking with data persistence

This example shows how Warp can provide a complete API monetization solution with quota enforcement, usage tracking, administrative controls, and developer self-service capabilities.