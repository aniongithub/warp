using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using Warp.Core.Middleware;

namespace Warp.Dilithium.Middleware;

public class OrMiddlewareOptions : MiddlewareOptions
{
    public MiddlewareDescriptor? Middleware1 { get; set; }
    public MiddlewareDescriptor? Middleware2 { get; set; }
}

public sealed class OrMiddleware : MiddlewareBase<OrMiddlewareOptions>
{
    private readonly IWarpMiddleware? _middleware1;
    private readonly IWarpMiddleware? _middleware2;

    public OrMiddleware(string name, ILogger logger, IDataContext context, OrMiddlewareOptions options, IServiceProvider serviceProvider)
        : base(name, logger, context, options)
    {
        _middleware1 = CreateMiddleware(options.Middleware1, serviceProvider);
        _middleware2 = CreateMiddleware(options.Middleware2, serviceProvider);

        if (_middleware1 == null && _middleware2 == null)
        {
            throw new InvalidOperationException("OrMiddleware requires at least one middleware to be configured.");
        }

        Logger.LogDebug("OrMiddleware configured with Middleware1={Middleware1}, Middleware2={Middleware2}", 
            _middleware1?.Name ?? "null", _middleware2?.Name ?? "null");
    }

    private IWarpMiddleware? CreateMiddleware(MiddlewareDescriptor? descriptor, IServiceProvider serviceProvider)
    {
        if (descriptor == null)
            return null;

        var middlewareType = Type.GetType(descriptor.Type);
        if (middlewareType == null)
        {
            Logger.LogError("Middleware type '{Type}' could not be found", descriptor.Type);
            return null;
        }

        // Get the options type from the middleware type
        var baseType = middlewareType.BaseType;
        while (baseType != null && (!baseType.IsGenericType || baseType.GetGenericTypeDefinition() != typeof(MiddlewareBase<>)))
        {
            baseType = baseType.BaseType;
        }

        if (baseType == null)
        {
            Logger.LogError("Middleware type '{Type}' does not inherit from MiddlewareBase<>", descriptor.Type);
            return null;
        }

        var optionsType = baseType.GetGenericArguments()[0];
        var options = Activator.CreateInstance(optionsType);

        if (descriptor.Options != null && options != null)
        {
            descriptor.Options.Bind(options);
        }

        try
        {
            var constructorParams = new object[] { descriptor.Name, Logger, DataContext, options! };
            return (IWarpMiddleware?)Activator.CreateInstance(middlewareType, constructorParams);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create middleware instance of type '{Type}'", descriptor.Type);
            return null;
        }
    }

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Check if we should apply this middleware to the current request
        if (!ShouldApplyToRequest(context))
        {
            Logger.LogDebug("OrMiddleware not applicable to request type: {Path}", context.Request.Path);
            await next(context);
            return;
        }

        Logger.LogDebug("Starting OrMiddleware evaluation for request: {Path}", context.Request.Path);

        bool middleware1Success = true;
        bool middleware2Success = true;

        // Try middleware1 first (short-circuit if successful)
        if (_middleware1 != null)
        {
            Logger.LogDebug("Evaluating first middleware: {Middleware1}", _middleware1.Name);
            middleware1Success = await TryMiddleware(_middleware1, context, next);
            if (middleware1Success)
            {
                Logger.LogDebug("First middleware succeeded, short-circuiting");
                return;
            }
            Logger.LogDebug("First middleware failed, trying second middleware");
        }

        // Try middleware2 if middleware1 failed or doesn't exist
        if (_middleware2 != null)
        {
            Logger.LogDebug("Evaluating second middleware: {Middleware2}", _middleware2.Name);
            middleware2Success = await TryMiddleware(_middleware2, context, next);
            if (middleware2Success)
            {
                Logger.LogDebug("Second middleware succeeded");
                return;
            }
            Logger.LogDebug("Second middleware failed");
        }

        // If both failed or are null, the OR operation failed
        if (_middleware1 == null)
            middleware1Success = false;
        if (_middleware2 == null)
            middleware2Success = false;

        if (!middleware1Success && !middleware2Success)
        {
            Logger.LogWarning("Both middlewares failed for OrMiddleware");
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Access denied");
            }
        }
        else
        {
            // This shouldn't happen due to short-circuiting, but just in case
            await next(context);
        }
    }

    private async Task<bool> TryMiddleware(IWarpMiddleware middleware, HttpContext context, RequestDelegate next)
    {
        try
        {
            // Create a buffered context that won't affect the real response
            var bufferedContext = new BufferedHttpContext(context);
            bool middlewareAllowedRequest = false;
            
            RequestDelegate fakeNext = (ctx) =>
            {
                middlewareAllowedRequest = true;
                return Task.CompletedTask;
            };

            await middleware.InvokeWithTracingAsync(bufferedContext, fakeNext);

            // If the middleware called next() and didn't set an error status, it succeeded
            if (middlewareAllowedRequest && bufferedContext.Response.StatusCode < 400)
            {
                // Middleware succeeded, now call the real next with the original context
                await next(context);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Exception in middleware {Middleware}: {Message}", middleware.Name, ex.Message);
            return false;
        }
    }
}

/// <summary>
/// A wrapper around HttpContext that buffers response writes to prevent affecting the real response
/// until we know the middleware succeeded
/// </summary>
public class BufferedHttpContext : HttpContext
{
    private readonly HttpContext _innerContext;
    private readonly BufferedHttpResponse _bufferedResponse;

    public BufferedHttpContext(HttpContext innerContext)
    {
        _innerContext = innerContext;
        _bufferedResponse = new BufferedHttpResponse(innerContext.Response);
    }

    public override HttpRequest Request => _innerContext.Request;
    public override HttpResponse Response => _bufferedResponse;
    public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features => _innerContext.Features;
    public override System.Security.Claims.ClaimsPrincipal User { get => _innerContext.User; set => _innerContext.User = value; }
    public override IDictionary<object, object?> Items { get => _innerContext.Items; set => _innerContext.Items = value; }
    public override IServiceProvider RequestServices { get => _innerContext.RequestServices; set => _innerContext.RequestServices = value; }
    public override CancellationToken RequestAborted { get => _innerContext.RequestAborted; set => _innerContext.RequestAborted = value; }
    public override string TraceIdentifier { get => _innerContext.TraceIdentifier; set => _innerContext.TraceIdentifier = value; }
    public override Microsoft.AspNetCore.Http.ISession Session { get => _innerContext.Session; set => _innerContext.Session = value; }
    public override Microsoft.AspNetCore.Http.ConnectionInfo Connection => _innerContext.Connection;
    public override Microsoft.AspNetCore.Http.WebSocketManager WebSockets => _innerContext.WebSockets;

    public override void Abort() => _innerContext.Abort();
}

/// <summary>
/// A buffered response that doesn't actually write to the underlying response stream
/// </summary>
public class BufferedHttpResponse : HttpResponse
{
    private readonly HttpResponse _innerResponse;
    private readonly MemoryStream _bodyBuffer = new();
    private readonly Dictionary<string, Microsoft.Extensions.Primitives.StringValues> _headers = new();
    private int _statusCode = 200;

    public BufferedHttpResponse(HttpResponse innerResponse)
    {
        _innerResponse = innerResponse;
    }

    public override Stream Body { get => _bodyBuffer; set { } }
    public override long? ContentLength { get => _bodyBuffer.Length; set { } }
    public override string? ContentType { get; set; }
    public override IResponseCookies Cookies => _innerResponse.Cookies;
    public override bool HasStarted => false; // Always return false to allow middleware to write
    public override IHeaderDictionary Headers => new BufferedHeaderDictionary(_headers);
    public override int StatusCode { get => _statusCode; set => _statusCode = value; }
    public override HttpContext HttpContext => _innerResponse.HttpContext;

    public override void OnCompleted(Func<object, Task> callback, object state) => _innerResponse.OnCompleted(callback, state);
    public override void OnStarting(Func<object, Task> callback, object state) => _innerResponse.OnStarting(callback, state);
    public override void Redirect(string location) => _innerResponse.Redirect(location);
    public override void Redirect(string location, bool permanent) => _innerResponse.Redirect(location, permanent);
}

/// <summary>
/// A simple header dictionary implementation for buffering headers
/// </summary>
public class BufferedHeaderDictionary : IHeaderDictionary
{
    private readonly Dictionary<string, Microsoft.Extensions.Primitives.StringValues> _headers;

    public BufferedHeaderDictionary(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> headers)
    {
        _headers = headers;
    }

    public Microsoft.Extensions.Primitives.StringValues this[string key] 
    { 
        get => _headers.TryGetValue(key, out var value) ? value : Microsoft.Extensions.Primitives.StringValues.Empty;
        set => _headers[key] = value;
    }

    public long? ContentLength { get; set; }

    public int Count => _headers.Count;
    public bool IsReadOnly => false;
    public ICollection<string> Keys => _headers.Keys;
    public ICollection<Microsoft.Extensions.Primitives.StringValues> Values => _headers.Values;

    public void Add(string key, Microsoft.Extensions.Primitives.StringValues value) => _headers.Add(key, value);
    public void Add(KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> item) => _headers.Add(item.Key, item.Value);
    public void Clear() => _headers.Clear();
    public bool Contains(KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> item) => _headers.Contains(item);
    public bool ContainsKey(string key) => _headers.ContainsKey(key);
    public void CopyTo(KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>[] array, int arrayIndex) => throw new NotImplementedException();
    public IEnumerator<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> GetEnumerator() => _headers.GetEnumerator();
    public bool Remove(string key) => _headers.Remove(key);
    public bool Remove(KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> item) => _headers.Remove(item.Key);
    public bool TryGetValue(string key, out Microsoft.Extensions.Primitives.StringValues value) => _headers.TryGetValue(key, out value);
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _headers.GetEnumerator();
}