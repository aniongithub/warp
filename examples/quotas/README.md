# Quotas Example

This example demonstrates how to use Warp Gateway with dynamic quota restriction and tracking to ensure we can monetize our APIs. It showcases dynamic usage tracking where quota consumption is determined by the actual response from the API (e.g., token usage), rather than fixed per-request charges.

**Note**: Warp also supports simple quotas where each request consumes a fixed amount (like 1.0 per request), but this example focuses on the more sophisticated dynamic quota system.

## Configuration Overview

This example includes:

- **Dynamic Quota tracking**: Quota consumption based on actual API response data (e.g., tokens used)
- **Response-based Usage**: Middleware extracts usage information from JSON responses using jq selectors
- **Prepaid vs Postpaid**: Both "prepaid" (hard limits) and "postpaid" (usage tracking for billing) quota models
- **API Key Authentication**: Protected MemoryAlpha API requiring valid API keys
- **Developer API**: End-user accessible management interface for creating and managing API keys
- **Admin API**: Administration API that is not accessible by end-users, for managing users, quotas, billing, etc.
- **User Permissions**: Role-based access with "developer" permissions required
- **OpenAPI Validation**: Both APIs validate requests against their specs

## Running the Example

### Step 1: Launch all required executables

**Start the services** using the **"Warp Permissions & Quotas"** compound launch configuration in VS Code, which starts:
   - Warp Gateway (port 5000)
   - Developer API (port 5002) 
   - Admin API (port 5003)

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

After running this command one or more times, you will see that the quota usage for this user has been dynamically updated based on the actual token usage returned by the API:

```json
  ...
  "quotas": [
    {
      "id": "7a6c43e1-d170-429b-8833-ada343ec92ea",
      "key": "user@example.com",
      "quotaName": "free_quota",
      "used": 1120.0,
      "limit": 5000.0,
      "type": "prepaid"
    }
  ]
  ...
```

Notice how the `used` value (1120.0) corresponds to the actual token usage from the API response, not just a simple increment. The middleware extracts the `token_usage.total_tokens` value from the JSON response and uses that to update the quota.
If we exceed the number of tokens allowed in our quota, any subsequent requests will be rejected. 
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

### Quota-Protected API Flow (Dynamic Usage Tracking)
1. **Request arrives** with `X-Api-Key` header at `/examples/quotas/free/memoryalpha/rag/ask`
2. **ApiKeyValidator** middleware validates the key and loads associated user permissions
3. **QuotaChecker** middleware checks if user has sufficient quota for the request and sets quota context
4. **Rate limiter** applies per-key rate limits (1 req/sec in this example)
5. **OpenAPI validator** validates the request against the MemoryAlpha API spec
6. **Request forwarded** to MemoryAlpha API if all checks pass
7. **Response intercepted** in PostDispatch phase:
   - **JsonResponseToHeaderTransform** extracts actual usage from response (e.g., `token_usage.total_tokens`)
   - **QuotaUpdater** updates quota with the actual usage amount, not a fixed increment
8. **Response returned** to client with updated quota tracking

This dynamic approach allows for precise usage-based billing where different requests may consume different amounts of quota based on their actual resource consumption.

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

- **Dynamic Quota Management**: Usage tracking based on actual API response data (token consumption)
- **Response Data Extraction**: Using jq selectors to extract usage metrics from JSON responses
- **Flexible Quota Models**: Both "prepaid" (hard limits) and "postpaid" (usage tracking) quota models
- **Header-based Context**: Quota context passed via request headers for external observability
- **API Key Authentication**: Protecting APIs with key-based access
- **Admin Operations**: Administrative API for quota management and user administration
- **Developer Portal**: Self-service API key management
- **Permission-based Access**: Role-based protection ("developer" and "admin" permissions)
- **Rate Limiting**: Per-key rate limiting (1 req/sec in this example)
- **OpenAPI Validation**: Request/response validation for all APIs
- **Multi-service Architecture**: Gateway + Developer API + Admin API + Protected API
- **Real-time Usage Tracking**: Precise quota consumption tracking with data persistence

This example shows how Warp can provide a sophisticated API monetization solution with dynamic usage-based quota enforcement, real-time consumption tracking, administrative controls, and developer self-service capabilities. The dynamic quota system allows for precise billing based on actual resource consumption rather than simple per-request charges.