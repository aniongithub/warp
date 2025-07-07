using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Warp.Core.Data;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using System.Diagnostics;

using Warp.Core.Helper;

const int USERS_PAGESIZE = 25;


var builder = WebApplication.CreateBuilder(args);
var assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "warp.apis.admin";

builder.Configuration.AddWarpConfiguration(assemblyName, clearExistingSources: true);

// Load DataContext from config (use your extension method if available)
var dataContextSection = builder.Configuration.GetSection("DataContext");
IDataContext dataContext;
if (dataContextSection.Exists())
    dataContext = dataContextSection.CreateFromConfiguration();
else
    dataContext = new Warp.Core.Data.Contexts.JsonDataContext("swagger-dummy.json");
builder.Services.AddSingleton(dataContext);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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
});

// GET /admin/users/count - gets the user count
app.MapGet("/admin/users/count", ([FromServices] IDataContext dataContext) =>
{
    using (var activity = activitySource.StartActivity("AdminUsersCount", ActivityKind.Internal))
    {
        var count = dataContext.Users.Count();
        var result = Results.Ok(count);
        return result;
    }
});

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
});

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
});

// GET /admin/users/{email} - get a particular user
app.MapGet("/admin/users/{email}", ([FromServices] IDataContext dataContext, string email) =>
{
    using (var activity = activitySource.StartActivity("AdminUserGet", ActivityKind.Internal))
    {
        var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
        var result = user != null ? Results.Ok(user) : Results.NotFound();
        return result;
    }
});

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
});

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
});

// GET /admin/users/{email}/permissions - get a user's permissions
app.MapGet("/admin/users/{email}/permissions", ([FromServices] IDataContext dataContext, string email) =>
{
    var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
    if (user == null)
        return Results.NotFound();
    return Results.Ok(user.Permissions);
});

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
});

app.Run();