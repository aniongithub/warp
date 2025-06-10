using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Warp.Core.Data;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSwaggerGen();

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
});

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
});

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
    return Results.Ok(keys);
});

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
});

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
});

app.MapGet("/developer/api-keys/{id}/permissions", (string id, [FromServices] IDataContext dataContext) =>
{
    var apiKey = dataContext.ApiKeys.FirstOrDefault(k => k.Id == id && k.IsActive);
    if (apiKey == null)
        return Results.NotFound();

    return Results.Ok(apiKey.Permissions);
});

app.MapGet("/developer/api-keys/{id}/is-active", (string id, [FromServices] IDataContext dataContext) =>
{
    var apiKey = dataContext.ApiKeys.FirstOrDefault(k => k.Id == id);
    if (apiKey == null)
        return Results.NotFound();
    return Results.Ok(apiKey.IsActive);
});

app.MapPut("/developer/api-keys/{id}/is-active", async (string id, [FromBody] bool isActive, [FromServices] IDataContext dataContext) =>
{
    var apiKey = dataContext.ApiKeys.FirstOrDefault(k => k.Id == id);
    if (apiKey == null)
        return Results.NotFound();
    apiKey.IsActive = isActive;
    await dataContext.SaveAsync(apiKey);
    return Results.Ok(apiKey);
});

app.Run();