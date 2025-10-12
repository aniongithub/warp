using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Warp.Core.Helper;
using Warp.Core.Job;
using Warp.Core.Job.Delivery;
using Warp.Dilithium.Transforms;

namespace Warp.Plasma;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("🚀 Warp Plasma - Job Processor Engine Starting...");
        
        try
        {
            var builder = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    // Use the warp configuration system with includes and environment interpolation
                    config.AddWarpConfiguration("warp.plasma",
                        baseDirectory: Environment.GetEnvironmentVariable("WARP_CONFIG_BASE_DIR") ?? "./config");
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddHttpClient();
                    
                    // Configure OpenTelemetry for distributed tracing
                    var config = context.Configuration;
                    var otelSection = config.GetSection("OpenTelemetry");
                    var sourceNames = otelSection.GetSection("SourceNames").Get<string[]>() ?? new[] { "Warp" };
                    var otelEndpoint = otelSection.GetValue<string>("Endpoint") ?? "http://localhost:4317";
                    var serviceName = otelSection.GetValue<string>("ServiceName") ?? "Warp";

                    services.AddOpenTelemetry().WithTracing(tracer =>
                    {
                        tracer
                            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
                            .AddSource(sourceNames)
                            .AddConsoleExporter() // For hierarchical console logging
                            .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otelEndpoint)) // For Jaeger/external tools
                            .AddHttpClientInstrumentation();
                    });
                    
                    // Register EPS with the configured source name
                    services.AddSingleton<EPS>(serviceProvider =>
                    {
                        var logger = serviceProvider.GetRequiredService<ILogger<EPS>>();
                        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
                        var httpClient = serviceProvider.GetRequiredService<HttpClient>();
                        var sourceName = sourceNames.Length > 0 ? sourceNames[0] : "Warp";
                        return new EPS(logger, configuration, httpClient, serviceProvider, sourceName);
                    });
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                });

            var host = builder.Build();

            // Ensure TracerProvider is initialized so ActivitySource listeners are registered
            try
            {
                var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
                tracerProvider.ForceFlush(1000);
            }
            catch
            {
                // best-effort: failure to flush shouldn't stop startup
            }

            var engine = host.Services.GetRequiredService<EPS>();
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            
            logger.LogInformation("EPS starting up...");
            
            // For now, just demonstrate that we can create a job context
            await engine.InitializeAsync();

            logger.LogInformation("EPS online.");
            
            // Start the job processing loop
            await engine.Start();
            
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Fatal error: {ex.Message}");
            return 1;
        }
    }
}

internal sealed class EPS
{
    private readonly ILogger<EPS> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly IServiceProvider _serviceProvider;
    private List<JobConfiguration> _jobConfigurations = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ActivitySource _activitySource;

    public EPS(ILogger<EPS> logger, IConfiguration configuration, HttpClient httpClient, IServiceProvider serviceProvider, string sourceName)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
        _serviceProvider = serviceProvider;
        
        // Create ActivitySource after TracerProvider is initialized
        _activitySource = new ActivitySource(sourceName);
    }

    public async Task InitializeAsync()
    {
        _logger.LogInformation("🔥 Plasma flow initialized - loading job configurations...");
        
        // Load job configurations from config
        _jobConfigurations = LoadJobConfigurations();
        
        _logger.LogInformation("Loaded {JobConfigCount} job configurations", _jobConfigurations.Count);
        foreach (var config in _jobConfigurations)
        {
            _logger.LogInformation("Job Configuration: {JobName} - Context: {ContextType}, Delivery: {DeliveryType}", 
                config.Name, config.ContextType, config.DeliveryType ?? "None");
        }
        
        await Task.CompletedTask;
    }

    public async Task Start()
    {
        _logger.LogInformation("🟢 Starting multi-consumer job processing...");
        
        Console.WriteLine("✅ Engine ready. Press Ctrl+C to stop.");
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            _logger.LogInformation("🛑 Shutdown requested...");
            _cancellationTokenSource.Cancel();
        };
        
        try
        {
            // Start a consumer task for each job configuration
            var consumerTasks = new List<Task>();
            
            foreach (var jobConfig in _jobConfigurations)
            {
                var consumerTask = Task.Run(() => ConsumeJobs(jobConfig, _cancellationTokenSource.Token));
                consumerTasks.Add(consumerTask);
                
                _logger.LogInformation("Started consumer for job type: {JobName}", jobConfig.Name);
            }
            
            // Wait for all consumers to complete
            await Task.WhenAll(consumerTasks);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("📤 Job processing stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Fatal error in job processing");
            throw;
        }
    }

    private List<JobConfiguration> LoadJobConfigurations()
    {
        var jobConfigs = new List<JobConfiguration>();
        var jobsSection = _configuration.GetSection("Jobs");
        
        foreach (var jobSection in jobsSection.GetChildren())
        {
            var config = new JobConfiguration
            {
                Name = jobSection.Key,
                Endpoint = jobSection["Endpoint"] ?? "",
                MaxConcurrentJobs = int.Parse(jobSection["MaxConcurrentJobs"] ?? "1"),
                PollingIntervalMs = int.Parse(jobSection["PollingIntervalMs"] ?? "5000")
            };
            
            // Load context and delivery types directly
            var contextSection = jobSection.GetSection("Context");
            config.ContextType = contextSection["Type"] ?? "";
            
            var deliverySection = jobSection.GetSection("Delivery");
            if (deliverySection.Exists())
            {
                config.DeliveryType = deliverySection["Type"] ?? "";
            }
            
            jobConfigs.Add(config);
        }
        
        return jobConfigs;
    }

    private async Task ConsumeJobs(JobConfiguration jobConfig, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting consumer for job type: {JobName}", jobConfig.Name);
        
        // Create job context instance
        var jobContext = CreateJobContext(jobConfig.ContextType, jobConfig.Name);
        
        // Create result delivery instance (optional)
        IJobResultDelivery? resultDelivery = null;
        if (!string.IsNullOrEmpty(jobConfig.DeliveryType))
        {
            resultDelivery = CreateResultDelivery(jobConfig.DeliveryType, jobConfig.Name);
        }
        
        // Create concurrency limiter for this job type
        using var concurrencyLimiter = new SemaphoreSlim(jobConfig.MaxConcurrentJobs, jobConfig.MaxConcurrentJobs);
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Try to dequeue a job (this will return a result indicating success or no job)
                    var dequeueResult = await jobContext.DequeueJobAsync<Job>();
                    
                    if (dequeueResult.HasJob)
                    {
                        var job = dequeueResult.Job!;
                        
                        // Acquire concurrency slot
                        await concurrencyLimiter.WaitAsync(cancellationToken);
                        
                        // Process job in background task to allow concurrent processing
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await ProcessSingleJobAsync(job, jobContext, resultDelivery, jobConfig);
                            }
                            finally
                            {
                                concurrencyLimiter.Release();
                            }
                        }, cancellationToken);
                    }
                    else
                    {
                        // No jobs available, wait and try again
                        await Task.Delay(jobConfig.PollingIntervalMs, cancellationToken);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Keep this for actual errors (not normal "no job" case)
                    _logger.LogWarning("Error occurred while dequeuing job for {JobName}", jobConfig.Name);
                    await Task.Delay(jobConfig.PollingIntervalMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in consumer loop for job type: {JobName}", jobConfig.Name);
                    await Task.Delay(jobConfig.PollingIntervalMs, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Consumer for job type {JobName} stopped", jobConfig.Name);
        }
    }

    private IJobContext CreateJobContext(string contextType, string jobName)
    {
        var type = Type.GetType(contextType);
        if (type == null)
        {
            throw new InvalidOperationException($"Could not load job context type: {contextType}");
        }

        try
        {
            // Get the configuration section for this job's context options
            var contextOptionsSection = _configuration.GetSection($"Jobs:{jobName}:Context:Options");

            // Create instance using parameterless constructor
            var instance = Activator.CreateInstance(type);
            if (instance is not IJobContext jobContext)
            {
                throw new InvalidOperationException($"Failed to create instance of {contextType}");
            }

            // Get connection string and channel for initialization
            var connectionString = contextOptionsSection["ConnectionString"] ?? 
                                 _configuration.GetConnectionString("Redis") ?? 
                                 "redis:6379,defaultDatabase=0";
            var channel = contextOptionsSection["Channel"] ?? jobName;

            // Initialize the job context using the new pattern
            jobContext.Initialize(connectionString, channel);

            return jobContext;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create job context instance for type: {contextType}", ex);
        }
    }

    private IJobResultDelivery? CreateResultDelivery(string deliveryType, string jobName)
    {
        var type = Type.GetType(deliveryType);
        if (type == null)
        {
            _logger.LogWarning("Could not load result delivery type: {DeliveryType}", deliveryType);
            return null;
        }

        try
        {
            _logger.LogDebug("Creating result delivery instance: {DeliveryType}", deliveryType);

            // Use the base class pattern to find options type
            var deliveryBaseType = type.GetResultDeliveryBaseType();
            if (deliveryBaseType == null)
            {
                _logger.LogWarning("Result delivery type {DeliveryType} does not inherit from ResultDeliveryBase<>", deliveryType);
                return null;
            }

            var optionsType = deliveryBaseType.GetGenericArguments()[0];
            var deliveryName = $"delivery-{deliveryType}";

            // Create options instance and bind configuration directly like middleware
            var optionsInstance = Activator.CreateInstance(optionsType);
            if (optionsInstance == null)
            {
                _logger.LogWarning("Could not create options instance for result delivery: {DeliveryType}", deliveryType);
                return null;
            }

            // Get the configuration section for this job's delivery options
            var deliveryOptionsSection = _configuration.GetSection($"Jobs:{jobName}:Delivery:Options");
            if (deliveryOptionsSection.Exists())
            {
                _logger.LogDebug("Binding options for result delivery: {DeliveryType}", deliveryType);
                deliveryOptionsSection.Bind(optionsInstance);
            }

            _logger.LogDebug("Creating delivery instance using ActivatorUtilities with base class pattern");
            
            // Use the warp pattern with ActivatorUtilities - base class constructor signature
            var logger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(type);
            var delivery = ActivatorUtilities.CreateInstance(_serviceProvider, type, deliveryName, logger, optionsInstance);
            
            return delivery as IJobResultDelivery;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create result delivery instance for type: {DeliveryType}", deliveryType);
            return null;
        }
    }

    private async Task ProcessSingleJobAsync(Job job, IJobContext jobContext, IJobResultDelivery? resultDelivery, JobConfiguration jobConfig)
    {
        // Create a new activity as a child of the original request's trace
        using var activity = CreateChildActivity(job, $"Job.Process.{jobConfig.Name}");
        activity?.SetTag("job.id", job.Id);
        activity?.SetTag("job.type", jobConfig.Name);
        activity?.SetTag("job.status", job.Status.ToString());
        activity?.SetTag("job.user_id", job.User?.Id ?? "unknown");
        
        try
        {
            _logger.LogInformation("Processing job: {JobId} (type: {JobType}) with trace: {TraceId}", 
                job.Id, jobConfig.Name, activity?.TraceId.ToString() ?? "none");
            
            // Get the endpoint from job configuration
            var targetEndpoint = jobConfig.Endpoint;
            if (string.IsNullOrEmpty(targetEndpoint))
            {
                // Fallback to global configuration
                targetEndpoint = _configuration["Endpoint"] ?? "http://localhost:8000";
            }
            _logger.LogDebug("Using configured endpoint: {Endpoint}", targetEndpoint);

            var syncPath = job.OriginalPath;
            // Build the full sync URL
            var syncUrl = $"{targetEndpoint.TrimEnd('/')}{syncPath}";
            
            _logger.LogInformation("Routing job to sync API: {SyncUrl}", syncUrl);
            _logger.LogDebug("Job parameters: {Parameters}", JsonSerializer.Serialize(job.Parameters));
            
            // Reverse transforms to reconstruct original request
            var originalParameters = await ReverseTransformsAsync(job);
            _logger.LogDebug("Parameters after reverse transform: {Parameters}", JsonSerializer.Serialize(originalParameters));
            
            // Execute the job by dispatching to sync API
            await ExecuteJobAsync(job, jobContext, syncUrl, originalParameters);
            
            // Deliver result if configured
            if (resultDelivery != null && (job.Status == JobStatus.Completed || job.Status == JobStatus.Failed))
            {
                try
                {
                    await resultDelivery.DeliverAsync(job);
                    _logger.LogInformation("Result delivered for job {JobId} via {DeliveryType}", 
                        job.Id, resultDelivery.GetType().Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deliver result for job {JobId}", job.Id);
                    // Don't fail the job just because delivery failed
                }
            }
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, $"Job execution error: {ex.Message}");
            activity?.SetTag("exception.type", ex.GetType().Name);
            activity?.SetTag("exception.message", ex.Message);
            _logger.LogError(ex, "Error executing job {JobId}", job.Id);
            
            try
            {
                await jobContext.UpdateJobAsync(job, JobStatus.Failed, error: $"Job execution error: {ex.Message}");
            }
            catch (Exception updateEx)
            {
                _logger.LogError(updateEx, "Failed to update job {JobId} status to failed", job.Id);
            }
        }
    }

    private async Task ExecuteJobAsync(Job job, IJobContext jobContext, string syncUrl, Dictionary<string, object?> originalParameters)
    {
        using var activity = _activitySource.StartActivity("Job.Execute.HttpCall");
        activity?.SetTag("job.id", job.Id);
        activity?.SetTag("http.url", syncUrl);
        activity?.SetTag("http.method", job.Parameters.ContainsKey("_httpMethod") ? job.Parameters["_httpMethod"]?.ToString() : "POST");
        
        try
        {
            _logger.LogInformation("🚀 Executing job {JobId} at {SyncUrl} with trace: {TraceId}", 
                job.Id, syncUrl, activity?.TraceId.ToString() ?? "none");
            
            // Prepare the HTTP request
            using var request = new HttpRequestMessage();
            
            // Determine HTTP method from job parameters or default to POST
            var httpMethod = job.Parameters.ContainsKey("_httpMethod") && job.Parameters["_httpMethod"] is string method 
                ? new HttpMethod(method.ToUpperInvariant()) 
                : HttpMethod.Post;
            
            request.Method = httpMethod;
            
            // For GET requests, add parameters as query string
            if (httpMethod == HttpMethod.Get && originalParameters.Any())
            {
                var queryBuilder = new StringBuilder(syncUrl);
                queryBuilder.Append(syncUrl.Contains('?') ? '&' : '?');
                
                var queryParams = new List<string>();
                foreach (var (key, value) in originalParameters)
                {
                    if (key != "_httpMethod" && value != null) // Skip internal parameters
                    {
                        var valueString = value.ToString() ?? "";
                        queryParams.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(valueString)}");
                    }
                }
                queryBuilder.Append(string.Join("&", queryParams));
                request.RequestUri = new Uri(queryBuilder.ToString());
            }
            else
            {
                request.RequestUri = new Uri(syncUrl);
            }
            
            // Add headers from job
            foreach (var header in job.Headers)
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    // If it's a content header, we'll add it to content headers later
                    _logger.LogDebug("Header {HeaderName} will be added to content headers", header.Key);
                }
            }
            
            // Add current activity's tracing context to propagate distributed trace
            if (Activity.Current != null)
            {
                var currentTraceParent = Activity.Current.Id;
                var currentTraceState = Activity.Current.TraceStateString;
                
                if (!string.IsNullOrEmpty(currentTraceParent))
                {
                    request.Headers.TryAddWithoutValidation("traceparent", currentTraceParent);
                    _logger.LogDebug("Added traceparent header: {TraceParent}", currentTraceParent);
                }
                
                if (!string.IsNullOrEmpty(currentTraceState))
                {
                    request.Headers.TryAddWithoutValidation("tracestate", currentTraceState);
                    _logger.LogDebug("Added tracestate header: {TraceState}", currentTraceState);
                }
            }
            
            // Check if we have file data that needs multipart encoding
            var hasFileData = originalParameters.Values.Any(v => IsFileData(v));
            
            // Add request body if we have parameters and it's not a GET request
            if (originalParameters.Any() && httpMethod != HttpMethod.Get)
            {
                if (hasFileData)
                {
                    // Create multipart content for file uploads
                    var multipartContent = new MultipartFormDataContent();
                    
                    foreach (var (key, value) in originalParameters)
                    {
                        if (IsFileData(value))
                        {
                            var fileData = value as BlobFileContent;
                            if (fileData != null)
                            {
                                var fileContent = new ByteArrayContent(fileData.Content);
                                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(fileData.ContentType);
                                multipartContent.Add(fileContent, key, fileData.FileName);
                            }
                        }
                        else
                        {
                            // Add as string content
                            multipartContent.Add(new StringContent(value?.ToString() ?? ""), key);
                        }
                    }
                    
                    request.Content = multipartContent;
                }
                else
                {
                    // Standard JSON content
                    var jsonContent = JsonSerializer.Serialize(originalParameters);
                    request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                }
                
                // Add content headers that couldn't be added to request headers
                foreach (var header in job.Headers)
                {
                    if (!request.Headers.Contains(header.Key) && request.Content != null)
                    {
                        request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }
            
            // Set timeout (-1 means infinite)
            var timeoutMs = int.Parse(_configuration["HttpTimeoutMs"] ?? "900000");
            using var cts = timeoutMs == -1 
                ? new CancellationTokenSource() 
                : new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            
            // Execute the HTTP request
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using (var response = await _httpClient.SendAsync(request, cts.Token))
            {
                stopwatch.Stop();
                
                // Read response content
                var responseContent = await response.Content.ReadAsStringAsync();
                
                _logger.LogInformation("✅ Job {JobId} completed in {ElapsedMs}ms with status {StatusCode}", 
                    job.Id, stopwatch.ElapsedMilliseconds, (int)response.StatusCode);
                
                // Add response information to activity
                activity?.SetTag("http.status_code", (int)response.StatusCode);
                activity?.SetTag("http.response_time_ms", stopwatch.ElapsedMilliseconds);
                activity?.SetStatus(response.IsSuccessStatusCode ? ActivityStatusCode.Ok : ActivityStatusCode.Error, 
                    response.IsSuccessStatusCode ? "Success" : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                
                // Update job status based on response
                if (response.IsSuccessStatusCode)
                {
                    await jobContext.UpdateJobAsync(job, JobStatus.Completed, output: responseContent);
                    _logger.LogInformation("Job {JobId} marked as completed", job.Id);
                }
                else
                {
                    var errorMessage = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {responseContent}";
                    await jobContext.UpdateJobAsync(job, JobStatus.Failed, error: errorMessage);
                    _logger.LogWarning("Job {JobId} marked as failed: {Error}", job.Id, errorMessage);
                }
            }
        }
        catch (OperationCanceledException) when (_httpClient != null)
        {
            var timeoutError = "Request timeout";
            activity?.SetStatus(ActivityStatusCode.Error, timeoutError);
            await jobContext.UpdateJobAsync(job, JobStatus.Failed, error: timeoutError);
            _logger.LogWarning("Job {JobId} timed out and marked as failed", job.Id);
        }
        catch (Exception ex)
        {
            var errorMessage = $"Exception during job execution: {ex.Message}";
            activity?.SetStatus(ActivityStatusCode.Error, errorMessage);
            activity?.SetTag("exception.type", ex.GetType().Name);
            activity?.SetTag("exception.message", ex.Message);
            await jobContext.UpdateJobAsync(job, JobStatus.Failed, error: errorMessage);
            _logger.LogError(ex, "Job {JobId} failed with exception", job.Id);
        }
    }

    private bool IsFileData(object? value)
    {
        if (value == null) return false;
        
        // Check if it's a BlobFileContent from the real transform
        return value is BlobFileContent;
    }

    private async Task<Dictionary<string, object?>> ReverseTransformsAsync(Job job)
    {
        var originalParameters = new Dictionary<string, object?>();
        
        // Process each parameter mapping to reverse transforms
        foreach (var (paramName, mapping) in job.ParameterMappings)
        {
            var currentValue = job.Parameters.TryGetValue(paramName, out var value) ? value : null;
            
            // If there's a transform, reverse it
            if (mapping.Transform != null && currentValue != null)
            {
                try
                {
                    var transform = mapping.Transform.CreateTransform();
                    // Create a simple service provider - in production this would be injected
                    var services = new ServiceCollection().BuildServiceProvider();
                    var reversedValue = await transform.ReverseAsync(currentValue, services);
                    
                    // Map back to original field location
                    MapToOriginalLocation(originalParameters, mapping.From, reversedValue);
                    
                    _logger.LogDebug("Reversed transform {TransformType} for parameter {ParamName}: {TransformedValue} -> {OriginalValue}", 
                        mapping.Transform.Type, paramName, currentValue, reversedValue);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to reverse transform for parameter {ParamName}", paramName);
                    // Fall back to original value
                    MapToOriginalLocation(originalParameters, mapping.From, currentValue);
                }
            }
            else
            {
                // No transform, just map back to original location
                MapToOriginalLocation(originalParameters, mapping.From, currentValue);
            }
        }
        
        return originalParameters;
    }

    private void MapToOriginalLocation(Dictionary<string, object?> parameters, Warp.Core.Job.InputSource source, object? value)
    {
        // For simplicity, we'll reconstruct as a JSON body since that's what most APIs expect
        // The original extraction logic shows we support header, query, and body sources
        
        if (!string.IsNullOrEmpty(source.Body))
        {
            // Map to body field - for nested paths like "user.name", we'd need more complex logic
            parameters[source.Body] = value;
        }
        else if (!string.IsNullOrEmpty(source.Query))
        {
            // For query parameters, we'll include them in the body for simplicity
            // In a more complete implementation, we'd separate query from body
            parameters[source.Query] = value;
        }
        else if (!string.IsNullOrEmpty(source.Header))
        {
            // Headers should be handled separately, but for now include in body
            parameters[source.Header] = value;
        }
    }

    private Activity? CreateChildActivity(Job job, string operationName)
    {
        // If we have a traceparent from the original request, restore the parent context using W3C ActivityContext
        if (!string.IsNullOrEmpty(job.TraceParent))
        {
            try
            {
                if (ActivityContext.TryParse(job.TraceParent, job.TraceState, out var parentContext))
                {
                    // Start activity with explicit parent context so the trace id is preserved and this becomes a sibling (child of the parentContext)
                    var activity = _activitySource.StartActivity(operationName, ActivityKind.Internal, parentContext);
                    if (activity == null)
                    {
                        _logger.LogWarning("ActivitySource.StartActivity returned null for job {JobId} when using parent context", job.Id);
                    }
                    return activity;
                }
                else
                {
                    _logger.LogWarning("Failed to parse W3C traceparent for job {JobId}: {TraceParent}", job.Id, job.TraceParent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error restoring parent context for job {JobId}: {TraceParent}", job.Id, job.TraceParent);
            }
        }
        
        // Fallback: create a new root activity if no parent context available
        var fallbackActivity = _activitySource.StartActivity(operationName);
        if (fallbackActivity != null)
        {
            _logger.LogDebug("Created root activity {ActivityId} for job {JobId} (no parent trace)", 
                fallbackActivity.Id, job.Id);
        }
        return fallbackActivity;
    }
}
