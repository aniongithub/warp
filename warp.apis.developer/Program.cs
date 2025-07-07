using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;

using Warp.Core.Data;
using Warp.Core.Helper;

var builder = WebApplication.CreateBuilder(args);
var assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "warp.apis.developer";

builder.Configuration.AddWarpConfiguration(assemblyName, clearExistingSources: true);

var dataContextSection = builder.Configuration.GetSection("DataContext");
IDataContext dataContext;
if (dataContextSection.Exists())
    dataContext = dataContextSection.CreateFromConfiguration();
else
    // Fallback for CLI/Swagger generation: use a dummy or in-memory context
    dataContext = new Warp.Core.Data.Contexts.JsonDataContext("swagger-dummy.json");
builder.Services.AddSingleton(dataContext);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Warp Developer API",
        Version = "v1",
        Description = "API for managing developer API keys and permissions in the Warp platform",
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

const int MAX_API_KEYS = 5;

app.MapGet("/developer/health", (HttpContext context, [FromServices] IDataContext dataContext) =>
{
    var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
    return Results.Ok($"{assemblyName} is healthy");
})
.WithTags("Health")
.WithSummary("Check API health status")
.WithDescription("Returns the health status of the Developer API service")
.Produces<string>(200, "text/plain");

app.MapPost("/developer/api-keys", async (HttpContext context, [FromServices] IDataContext dataContext) =>
{
    var email = context.Request.Headers["X-JWT-Email"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(email))
        return Results.Unauthorized();

    var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
    if (user is null)
    {
        user = dataContext.CreateUser();
        user.Id = Guid.NewGuid().ToString();
        user.Email = email;
        await dataContext.SaveAsync(user);
    }

    var keys = dataContext.ApiKeys.Where(k => k.Owner == email && k.IsActive).ToList();
    if (keys.Count >= MAX_API_KEYS)
        return Results.Problem($"You can only have up to {MAX_API_KEYS} API keys.");

    var apiKey = dataContext.CreateApiKey();
    apiKey.Id = Guid.NewGuid().ToString();
    apiKey.Key = Guid.NewGuid().ToString();
    apiKey.Owner = email;
    apiKey.IsActive = true;
    // Set permissions from X-Permissions header, or default to ["free"]
    var permsHeader = context.Request.Headers["X-Permissions"].FirstOrDefault();
    apiKey.Permissions = !string.IsNullOrWhiteSpace(permsHeader)
        ? permsHeader.Split(',').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToList()
        : new List<string> { "free" };
    await dataContext.SaveAsync(apiKey);

    return Results.Ok(apiKey);
})
.WithTags("API Keys")
.WithSummary("Create a new API key")
.WithDescription("Creates a new API key for the authenticated user. The user can have up to 5 active API keys. Permissions can be set via the X-Permissions header (comma-separated), defaults to 'free' if not specified.")
.Produces(200, typeof(object), "application/json")
.Produces(401, typeof(string), "text/plain")
.Produces(400, typeof(string), "text/plain");

app.MapGet("/developer/api-keys", async (HttpContext context, [FromServices] IDataContext dataContext) =>
{
    var email = context.Request.Headers["X-JWT-Email"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(email))
        return Results.Unauthorized();

    var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
    if (user is null)
    {
        user = dataContext.CreateUser();
        user.Id = Guid.NewGuid().ToString();
        user.Email = email;
        await dataContext.SaveAsync(user);
    }
    var keys = dataContext.ApiKeys.Where(k => k.Owner == email && k.IsActive).ToList();

    // If there no keys, create a default one
    if (!keys.Any())
    {
        var apiKey = dataContext.CreateApiKey();
        apiKey.Id = Guid.NewGuid().ToString();
        apiKey.Key = Guid.NewGuid().ToString(); // Generate a random key
        apiKey.Owner = email;
        apiKey.IsActive = true;
        apiKey.Permissions = new List<string> { "free" }; // Default permissions
        await dataContext.SaveAsync(apiKey);
        keys.Add(apiKey);
    }

    return Results.Ok(keys);
})
.WithTags("API Keys")
.WithSummary("Get all API keys for the authenticated user")
.WithDescription("Returns all active API keys for the authenticated user. If no keys exist, a default key with 'free' permissions is automatically created.")
.Produces(200, typeof(List<object>), "application/json")
.Produces(401, typeof(string), "text/plain");

app.MapDelete("/developer/api-keys/{id}", async (string id, HttpContext context, [FromServices] IDataContext dataContext) =>
{
    var email = context.Request.Headers["X-JWT-Email"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(email))
        return Results.Unauthorized();

    var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
    if (user is null)
    {
        user = dataContext.CreateUser();
        user.Id = Guid.NewGuid().ToString();
        user.Email = email;
        await dataContext.SaveAsync(user);
    }
    var apiKey = dataContext.ApiKeys.FirstOrDefault(k => k.Id == id && k.Owner == email && k.IsActive);
    if (apiKey == null)
        return Results.NotFound();
    apiKey.IsActive = false;
    await dataContext.SaveAsync(apiKey);
    return Results.Ok(new { success = true });
})
.WithTags("API Keys")
.WithSummary("Delete an API key")
.WithDescription("Deactivates an API key by setting IsActive to false. Only the owner of the API key can delete it.")
.Produces(200, typeof(object), "application/json")
.Produces(401, typeof(string), "text/plain")
.Produces(404, typeof(string), "text/plain");

app.MapPut("/developer/api-keys/{id}/permissions", async (string id, List<string> newPermissions, HttpContext context, [FromServices] IDataContext dataContext) =>
{
    var email = context.Request.Headers["X-JWT-Email"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(email))
        return Results.Unauthorized();

    var user = dataContext.Users.FirstOrDefault(u => u.Email == email);
    if (user is null)
        return Results.Unauthorized();

    var apiKey = dataContext.ApiKeys.FirstOrDefault(k => k.Id == id && k.Owner == email && k.IsActive);
    if (apiKey == null)
        return Results.NotFound();

    // Ensure new permissions are a subset of the user's permissions
    if (!newPermissions.All(p => user.Permissions.Contains(p)))
        return Results.BadRequest("New permissions must be a subset of the user's permissions.");

    apiKey.Permissions = newPermissions;
    await dataContext.SaveAsync(apiKey);

    return Results.Ok(apiKey);
})
.WithTags("API Keys")
.WithSummary("Update API key permissions")
.WithDescription("Updates the permissions for an existing API key. The new permissions must be a subset of the user's permissions. Only the owner of the API key can update its permissions.")
.Produces(200, typeof(object), "application/json")
.Produces(401, typeof(string), "text/plain")
.Produces(404, typeof(string), "text/plain")
.Produces(400, typeof(string), "text/plain");

app.MapGet("/developer/api-keys/{id}/permissions", (string id, [FromServices] IDataContext dataContext) =>
{
    var apiKey = dataContext.ApiKeys.FirstOrDefault(k => k.Id == id && k.IsActive);
    if (apiKey == null)
        return Results.NotFound();

    return Results.Ok(apiKey.Permissions);
})
.WithTags("API Keys")
.WithSummary("Get API key permissions")
.WithDescription("Returns the permissions associated with a specific API key.")
.Produces(200, typeof(List<string>), "application/json")
.Produces(404, typeof(string), "text/plain");

app.MapGet("/developer/api-keys/{id}/is-active", (string id, [FromServices] IDataContext dataContext) =>
{
    var apiKey = dataContext.ApiKeys.FirstOrDefault(k => k.Id == id);
    if (apiKey == null)
        return Results.NotFound();
    return Results.Ok(apiKey.IsActive);
})
.WithTags("API Keys")
.WithSummary("Check if API key is active")
.WithDescription("Returns the active status of a specific API key.")
.Produces(200, typeof(bool), "application/json")
.Produces(404, typeof(string), "text/plain");

app.MapPut("/developer/api-keys/{id}/is-active", async (string id, [FromBody] bool isActive, [FromServices] IDataContext dataContext) =>
{
    var apiKey = dataContext.ApiKeys.FirstOrDefault(k => k.Id == id);
    if (apiKey == null)
        return Results.NotFound();
    apiKey.IsActive = isActive;
    await dataContext.SaveAsync(apiKey);
    return Results.Ok(apiKey);
})
.WithTags("API Keys")
.WithSummary("Update API key active status")
.WithDescription("Updates the active status of a specific API key. Set to true to activate or false to deactivate.")
.Produces(200, typeof(object), "application/json")
.Produces(404, typeof(string), "text/plain");

app.Run();