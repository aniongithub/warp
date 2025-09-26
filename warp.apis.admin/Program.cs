using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Warp.Core.Data;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using System.Diagnostics;
using Microsoft.OpenApi.Models;

using Warp.Core.Helper;

const int USERS_PAGESIZE = 25;


var builder = WebApplication.CreateBuilder(args);
var assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "warp.apis.admin";

if (Environment.GetEnvironmentVariable("WARP_CONFIG_BASE_DIR") is string configBaseDir &&
    !string.IsNullOrWhiteSpace(configBaseDir))
    builder.Configuration.AddWarpConfiguration(assemblyName, clearExistingSources: true,
        baseDirectory: configBaseDir);

var dataContextSection = builder.Configuration.GetSection("DataContext");
IDataContext dataContext;

// Check if we're running under swagger CLI
bool isSwaggerGeneration = Environment.GetCommandLineArgs().Any(arg => arg.Contains("swagger")) ||
                          Environment.GetCommandLineArgs().Any(arg => arg.Contains("tofile")) ||
                          Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "SwaggerGeneration";

if (isSwaggerGeneration || !dataContextSection.Exists()) // Fallback for CLI/Swagger generation: use a dummy context
    dataContext = new Warp.Core.Data.Contexts.JsonDataContext("swagger-dummy.json");
else
    dataContext = dataContextSection.CreateFromConfiguration();

builder.Services.AddSingleton(dataContext);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Warp Admin API",
        Version = "v1",
        Description = "Administrative API for managing users and permissions in the Warp platform",
        Contact = new OpenApiContact
        {
            Name = "Warp Support",
            Email = "support@warp.com"
        }
    });

    // Include XML comments if available
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }
});

// Read OpenTelemetry endpoint from config
var otelEndpoint = builder.Configuration.GetSection("OpenTelemetry").GetValue<string>("Endpoint") ?? "http://otel-collector:4317";

// Add OpenTelemetry tracing (match warp setup)
builder.Services.AddOpenTelemetry().WithTracing(tracer =>
{
    tracer
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(assemblyName))
        .AddAspNetCoreInstrumentation(options =>
            options.EnrichWithHttpResponse = (activity, response) =>
                activity.SetStatus(response?.StatusCode.IsErrorStatus() == true
                    ? ActivityStatusCode.Error
                    : ActivityStatusCode.Ok,
                    response != null
                        ? $"HTTP {response.StatusCode.GetStatusDescription()}"
                        : string.Empty))
        .AddSource(assemblyName)
        .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otelEndpoint));
});

var activitySource = new ActivitySource(assemblyName);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Wire up the Admin APIs
app.MapGet("/admin/health", (HttpContext context, [FromServices] IDataContext dataContext) =>
{
    using (var activity = activitySource.StartActivity("AdminHealthCheck", ActivityKind.Internal))
    {
        var result = Results.Ok($"{assemblyName} is healthy");
        return result;
    }
})
.WithTags("Health")
.WithSummary("Check Admin API health status")
.WithDescription("Returns the health status of the Admin API service")
.Produces<string>(200, "text/plain");

// GET /admin/users/count - gets the user count
app.MapGet("/admin/users/count", ([FromServices] IDataContext dataContext) =>
{
    using (var activity = activitySource.StartActivity("AdminUsersCount", ActivityKind.Internal))
    {
        var count = dataContext.Users.Count();
        var result = Results.Ok(count);
        return result;
    }
})
.WithTags("Users")
.WithSummary("Get total user count")
.WithDescription("Returns the total number of users in the system")
.Produces<int>(200, "application/json");

// GET /admin/users/pages/count - gets the user page count (assuming 25 per page)
app.MapGet("/admin/users/pages/count", ([FromServices] IDataContext dataContext) =>
{
    using (var activity = activitySource.StartActivity("AdminUsersPagesCount", ActivityKind.Internal))
    {
        var count = dataContext.Users.Count();
        var pageCount = (int)Math.Ceiling(count / (double)USERS_PAGESIZE);
        var result = Results.Ok(pageCount);
        return result;
    }
})
.WithTags("Users")
.WithSummary("Get total page count for users")
.WithDescription($"Returns the total number of pages when users are paginated with {USERS_PAGESIZE} users per page")
.Produces<int>(200, "application/json");

// GET /admin/users/pages/{pageindex} - get one page of user results
app.MapGet("/admin/users/pages/{pageindex:int}", ([FromServices] IDataContext dataContext, int pageindex) =>
{
    using (var activity = activitySource.StartActivity("AdminUsersPage", ActivityKind.Internal))
    {
        const int pageSize = 25;
        var users = dataContext.Users.Skip(pageindex * pageSize).Take(pageSize).ToList();
        var result = Results.Ok(users);
        return result;
    }
})
.WithTags("Users")
.WithSummary("Get a page of users")
.WithDescription("Returns a specific page of users. Page indices start from 0. Each page contains up to 25 users.")
.Produces(200, typeof(List<object>), "application/json");

// GET /admin/users/{email} - get a particular user
app.MapGet("/admin/users/{email}", ([FromServices] IDataContext dataContext, string email) =>
{
    using (var activity = activitySource.StartActivity("AdminUserGet", ActivityKind.Internal))
    {
        var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
        var result = user != null ? Results.Ok(user) : Results.NotFound();
        return result;
    }
})
.WithTags("Users")
.WithSummary("Get a user by email")
.WithDescription("Returns details of a specific user by their email address")
.Produces(200, typeof(object), "application/json")
.Produces(404, typeof(string), "text/plain");

// PUT /admin/users/{email} - creates a new user, updates the user details if the user exists
app.MapPut("/admin/users/{email}", async ([FromServices] IDataContext dataContext, string email, [FromBody] IUser userUpdate) =>
{
    using (var activity = activitySource.StartActivity("AdminUserPut", ActivityKind.Internal))
    {
        var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
        if (user == null)
        {
            user = dataContext.CreateUser();
            user.Email = email;
        }
        user.Permissions.Clear();
        if (userUpdate.Permissions != null)
            user.Permissions.AddRange(userUpdate.Permissions);
        await dataContext.SaveAsync(user);
        var result = Results.Ok(user);
        return result;
    }
})
.WithTags("Users")
.WithSummary("Create or update a user")
.WithDescription("Creates a new user or updates an existing user's details including permissions. If the user doesn't exist, a new one is created.")
.Produces(200, typeof(object), "application/json");

// DEL /admin/users/{email} - deactivates the user
app.MapDelete("/admin/users/{email}", async ([FromServices] IDataContext dataContext, string email) =>
{
    using (var activity = activitySource.StartActivity("AdminUserDelete", ActivityKind.Internal))
    {
        var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
        if (user == null)
            return Results.NotFound();
        user.Permissions.Clear();
        await dataContext.SaveAsync(user);
        var result = Results.Ok();
        return result;
    }
})
.WithTags("Users")
.WithSummary("Deactivate a user")
.WithDescription("Deactivates a user by clearing all their permissions. This effectively removes their access to the system.")
.Produces(200, typeof(object), "application/json")
.Produces(404, typeof(string), "text/plain");

// GET /admin/users/{email}/permissions - get a user's permissions
app.MapGet("/admin/users/{email}/permissions", ([FromServices] IDataContext dataContext, string email) =>
{
    var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
    if (user == null)
        return Results.NotFound();
    return Results.Ok(user.Permissions);
})
.WithTags("Users")
.WithSummary("Get user permissions")
.WithDescription("Returns the permissions assigned to a specific user")
.Produces(200, typeof(List<string>), "application/json")
.Produces(404, typeof(string), "text/plain");

// PUT /admin/users/{email}/permissions - set a user's permissions (admin can set any permissions)
app.MapPut("/admin/users/{email}/permissions", async ([FromServices] IDataContext dataContext, string email, [FromBody] List<string> permissions) =>
{
    var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
    if (user == null)
    {
        user = dataContext.CreateUser();
        user.Email = email;
    }
    user.Permissions.Clear();
    if (permissions != null)
        user.Permissions.AddRange(permissions);
    await dataContext.SaveAsync(user);
    return Results.Ok(user.Permissions);
})
.WithTags("Users")
.WithSummary("Set user permissions")
.WithDescription("Sets the permissions for a specific user. Admin can assign any permissions. If the user doesn't exist, a new one is created.")
.Produces(200, typeof(List<string>), "application/json");

// GET /admin/users/{email}/quotas - get a user's quotas
app.MapGet("/admin/users/{email}/quotas", ([FromServices] IDataContext dataContext, string email) =>
{
    var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
    if (user == null)
        return Results.NotFound();
    var quotas = dataContext.Quotas.Where(q => q.Key == email).ToList();
    return Results.Ok(quotas);
})
.WithTags("Users")
.WithSummary("Get user quotas")
.WithDescription("Returns the quotas assigned to a specific user")
.Produces(200, typeof(List<object>), "application/json")
.Produces(404, typeof(string), "text/plain");

// SET /admin/users/{email}/{quotaId}/usage - set a user's quota usage by id
app.MapPut("/admin/users/{email}/quotas/{quotaId}/usage", async ([FromServices] IDataContext dataContext, string email, string quotaId, [FromBody] float usage) =>
{
    var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
    if (user == null)
        return Results.NotFound("User not found.");

    var quota = dataContext.Quotas.FirstOrDefault(q => q.Key == email && q.Id == quotaId);
    if (quota != null)
    {
        quota.Used = usage;
        await dataContext.SaveAsync(quota);
        return Results.Ok(quota);
    }
    else
        return Results.NotFound("Quota not found.");
})
.WithTags("Users")
.WithSummary("Set user quota by ID")
.WithDescription("Sets a specific quota for a user by quota ID. If the quota doesn't exist, a 404 error is returned.")
.Produces(200, typeof(object), "application/json")
.Produces(404, typeof(string), "text/plain");

app.Run();