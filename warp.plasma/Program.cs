using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Warp.Core.Helper;
using Warp.Core.Job;
using Warp.Core.Job.Contexts;
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
                    // Clear default configuration sources and use warp configuration system
                    config.Sources.Clear();
                    
                    // Use the warp configuration system with includes and environment interpolation
                    config.AddWarpConfiguration("warp.plasma", "./config", useDevelopmentConfig: true, clearExistingSources: false)
                          .AddEnvironmentVariables("WARP_PLASMA_")
                          .AddCommandLine(args);
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<PlasmaEngine>();
                    services.AddHttpClient();
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                });

            var host = builder.Build();
            
            var engine = host.Services.GetRequiredService<PlasmaEngine>();
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            
            logger.LogInformation("Warp Plasma Engine initialized");
            
            // For now, just demonstrate that we can create a job context
            await engine.InitializeAsync();
            
            logger.LogInformation("Warp Plasma Engine ready for job processing");
            
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

public class PlasmaEngine
{
    private readonly ILogger<PlasmaEngine> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private RedisJobContext? _jobContext;

    public PlasmaEngine(ILogger<PlasmaEngine> logger, IConfiguration configuration, HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
    }

    public async Task InitializeAsync()
    {
        _logger.LogInformation("🔥 Plasma flow initialized - ready to process jobs");
        
        // Initialize Redis job context from configuration
        var redisConfig = _configuration.GetSection("Redis");
        var connectionString = redisConfig["ConnectionString"] ?? "localhost:6379";
        var database = int.Parse(redisConfig["Database"] ?? "0");
        var channel = redisConfig["Channel"] ?? "default";
        
        _logger.LogInformation("Connecting to Redis: {ConnectionString}, Database: {Database}, Channel: {Channel}", 
            connectionString, database, channel);
        
        _jobContext = new RedisJobContext(channel, connectionString, database);
        
        // Log configuration for debugging
        var pollingInterval = _configuration["PollingIntervalMs"];
        var maxConcurrentJobs = _configuration["MaxConcurrentJobs"];
        var enableMetrics = _configuration["Metrics:EnableMetrics"];
        
        _logger.LogInformation("Configuration - Polling: {PollingInterval}ms, MaxConcurrent: {MaxConcurrent}, Metrics: {Metrics}", 
            pollingInterval, maxConcurrentJobs, enableMetrics);
        
        await Task.CompletedTask;
    }

    public async Task Start()
    {
        _logger.LogInformation("🟢 Starting job processing loop...");
        
        var pollingIntervalMs = int.Parse(_configuration["PollingIntervalMs"] ?? "5000");
        var cancellationToken = new CancellationTokenSource();
        
        Console.WriteLine("✅ Engine ready. Press Ctrl+C to stop.");
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            _logger.LogInformation("🛑 Shutdown requested...");
            cancellationToken.Cancel();
        };
        
        try
        {
            while (!cancellationToken.Token.IsCancellationRequested)
            {
                await ProcessJobsAsync();
                await Task.Delay(pollingIntervalMs, cancellationToken.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("📤 Job processing stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Fatal error in job processing loop");
            throw;
        }
    }

    public async Task ProcessJobsAsync()
    {
        _logger.LogInformation("⚡ Processing jobs...");
        
        if (_jobContext != null)
        {
            try
            {
                var job = await _jobContext.DequeueJobAsync<Job>();
                _logger.LogInformation("Dequeued job: {JobId}", job.Id);
                
                // Access routing info directly from job fields
                if (job != null)
                {
                    try
                    {
                        // Resolve cluster ID to destination address from YARP cluster configuration
                        var targetDestination = "";
                        if (!string.IsNullOrEmpty(job.ClusterId))
                        {
                            // First try to get the cluster section
                            var clusterSection = _configuration.GetSection($"Clusters:{job.ClusterId}");
                            if (clusterSection.Exists())
                            {
                                // Look for the first destination in the cluster
                                var destinationsSection = clusterSection.GetSection("Destinations");
                                var firstDestination = destinationsSection.GetChildren().FirstOrDefault();
                                if (firstDestination != null)
                                {
                                    var address = firstDestination.GetValue<string>("Address");
                                    if (!string.IsNullOrEmpty(address))
                                    {
                                        targetDestination = address;
                                        _logger.LogDebug("Resolved cluster {ClusterId} to destination: {Destination}", job.ClusterId, targetDestination);
                                    }
                                }
                            }
                            
                            if (string.IsNullOrEmpty(targetDestination))
                            {
                                _logger.LogError("No destination configured for cluster {ClusterId}", job.ClusterId);
                                await _jobContext.UpdateJobAsync(job, JobStatus.Failed, error: $"No destination configured for cluster {job.ClusterId}");
                                return;
                            }
                        }
                        else if (!string.IsNullOrEmpty(job.TargetDestination))
                        {
                            targetDestination = job.TargetDestination;
                        }
                        else
                        {
                            _logger.LogError("Job {JobId} has no cluster ID or target destination", job.Id);
                            await _jobContext.UpdateJobAsync(job, JobStatus.Failed, error: "Job has no cluster ID or target destination");
                            return;
                        }

                        var syncPath = job.OriginalPath;
                        // Build the full sync URL
                        var syncUrl = $"{targetDestination.TrimEnd('/')}{syncPath}";
                        
                        _logger.LogInformation("Routing job to sync API: {SyncUrl} (Cluster: {ClusterId})", syncUrl, job.ClusterId);
                        _logger.LogDebug("Job parameters: {Parameters}", JsonSerializer.Serialize(job.Parameters));
                        
                        // Reverse transforms to reconstruct original request
                        var originalParameters = await ReverseTransformsAsync(job);
                        _logger.LogDebug("Parameters after reverse transform: {Parameters}", JsonSerializer.Serialize(originalParameters));
                        
                        // Execute the job by dispatching to sync API
                        await ExecuteJobAsync(job, syncUrl, originalParameters);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing job {JobId}", job.Id);
                        await _jobContext.UpdateJobAsync(job, JobStatus.Failed, error: $"Job execution error: {ex.Message}");
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // No jobs available - this is normal
                _logger.LogDebug("No jobs available in queue");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing job");
            }
        }
        
        await Task.CompletedTask;
    }

    private async Task ExecuteJobAsync(Job job, string syncUrl, Dictionary<string, object?> originalParameters)
    {
        try
        {
            _logger.LogInformation("🚀 Executing job {JobId} at {SyncUrl}", job.Id, syncUrl);
            
            // Prepare the HTTP request
            using var request = new HttpRequestMessage();
            
            // Determine HTTP method from job parameters or default to POST
            var httpMethod = job.Parameters.ContainsKey("_httpMethod") && job.Parameters["_httpMethod"] is string method 
                ? new HttpMethod(method.ToUpperInvariant()) 
                : HttpMethod.Post;
            
            request.Method = httpMethod;
            request.RequestUri = new Uri(syncUrl);
            
            // Add headers from job
            foreach (var header in job.Headers)
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    // If it's a content header, we'll add it to content headers later
                    _logger.LogDebug("Header {HeaderName} will be added to content headers", header.Key);
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
            
            // Set a reasonable timeout
            var timeoutMs = int.Parse(_configuration["HttpTimeoutMs"] ?? "3000000");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            
            // Execute the HTTP request
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using (var response = await _httpClient.SendAsync(request, cts.Token))
            {
                stopwatch.Stop();
                
                // Read response content
                var responseContent = await response.Content.ReadAsStringAsync();
                
                _logger.LogInformation("✅ Job {JobId} completed in {ElapsedMs}ms with status {StatusCode}", 
                    job.Id, stopwatch.ElapsedMilliseconds, (int)response.StatusCode);
                
                // Update job status based on response
                if (response.IsSuccessStatusCode)
                {
                    await _jobContext!.UpdateJobAsync(job, JobStatus.Completed, output: responseContent);
                    _logger.LogInformation("Job {JobId} marked as completed", job.Id);
                }
                else
                {
                    var errorMessage = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {responseContent}";
                    await _jobContext!.UpdateJobAsync(job, JobStatus.Failed, error: errorMessage);
                    _logger.LogWarning("Job {JobId} marked as failed: {Error}", job.Id, errorMessage);
                }
            }
        }
        catch (OperationCanceledException) when (_httpClient != null)
        {
            var timeoutError = "Request timeout";
            await _jobContext!.UpdateJobAsync(job, JobStatus.Failed, error: timeoutError);
            _logger.LogWarning("Job {JobId} timed out and marked as failed", job.Id);
        }
        catch (Exception ex)
        {
            var errorMessage = $"Exception during job execution: {ex.Message}";
            await _jobContext!.UpdateJobAsync(job, JobStatus.Failed, error: errorMessage);
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
                    var transform = CreateTransform(mapping.Transform);
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

    private ITransform CreateTransform(TransformConfig transformConfig)
    {
        var transformType = Type.GetType(transformConfig.Type);
        if (transformType == null)
            throw new InvalidOperationException($"Transform type not found: {transformConfig.Type}");

        var optionsType = transformType.BaseType?.GetGenericArguments().FirstOrDefault();
        if (optionsType == null)
            throw new InvalidOperationException($"Transform type {transformConfig.Type} does not have a parameterless constructor or options type");
        var options = Activator.CreateInstance(optionsType);

        // Copy all properties from the options dictionary to the options object
        foreach (var kvp in transformConfig.Options)
        {
            var prop = optionsType.GetProperty(kvp.Key);
            if (prop != null && prop.CanWrite)
                prop.SetValue(options, Convert.ChangeType(kvp.Value.ToString(), prop.PropertyType));
        }

        return (ITransform)Activator.CreateInstance(transformType, options)!;
    }
}
