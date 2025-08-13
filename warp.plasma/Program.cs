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
                    services.AddSingleton<EPS>();
                    services.AddHttpClient();
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                });

            var host = builder.Build();
            
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
    private RedisJobContext? _jobContext;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private DateTime _lastJobProcessedAt = DateTime.UtcNow;

    public EPS(ILogger<EPS> logger, IConfiguration configuration, HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
        
        var maxConcurrentJobs = int.Parse(_configuration["MaxConcurrentJobs"] ?? "5");
        _concurrencyLimiter = new SemaphoreSlim(maxConcurrentJobs, maxConcurrentJobs);
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
        var idleTimeout = _configuration["IdleTimeoutMs"];
        
        _logger.LogInformation("Configuration - Polling: {PollingInterval}ms, MaxConcurrent: {MaxConcurrent}, IdleTimeout: {IdleTimeout}ms", 
            pollingInterval, maxConcurrentJobs, idleTimeout);
        
        await Task.CompletedTask;
    }

    public async Task Start()
    {
        _logger.LogInformation("🟢 Starting job processing loop...");
        
        var pollingIntervalMs = int.Parse(_configuration["PollingIntervalMs"] ?? "5000");
        var idleTimeoutMs = int.Parse(_configuration["IdleTimeoutMs"] ?? "300000");
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
                var jobsProcessed = await ProcessJobsAsync();
                
                if (jobsProcessed > 0)
                {
                    _lastJobProcessedAt = DateTime.UtcNow;
                    _logger.LogDebug("Reset idle timer after processing {JobCount} jobs", jobsProcessed);
                }
                else
                {
                    // Check for idle timeout
                    var idleDuration = DateTime.UtcNow - _lastJobProcessedAt;
                    if (idleDuration.TotalMilliseconds > idleTimeoutMs)
                    {
                        _logger.LogInformation("💤 Idle timeout reached ({IdleMs}ms), shutting down...", idleTimeoutMs);
                        break;
                    }
                    else
                    {
                        _logger.LogDebug("Idle for {IdleMs}ms (timeout: {TimeoutMs}ms)", 
                            (int)idleDuration.TotalMilliseconds, idleTimeoutMs);
                    }
                }
                
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
        finally
        {
            _concurrencyLimiter?.Dispose();
        }
    }

    public async Task<int> ProcessJobsAsync()
    {
        _logger.LogDebug("⚡ Processing jobs...");
        
        var jobsProcessed = 0;
        var maxConcurrentJobs = int.Parse(_configuration["MaxConcurrentJobs"] ?? "5");
        
        if (_jobContext != null)
        {
            // Process multiple jobs concurrently up to the limit
            var tasks = new List<Task<int>>();
            
            _logger.LogDebug("Available concurrency slots: {Available}/{Max}", 
                _concurrencyLimiter.CurrentCount, maxConcurrentJobs);
            
            for (int i = 0; i < maxConcurrentJobs; i++)
            {
                // Try to acquire a concurrency slot
                if (await _concurrencyLimiter.WaitAsync(0)) // Non-blocking check
                {
                    var task = ProcessSingleJobAsync();
                    tasks.Add(task);
                    _logger.LogDebug("Started concurrent job task {TaskIndex} (remaining slots: {Remaining})", 
                        i + 1, _concurrencyLimiter.CurrentCount);
                }
                else
                {
                    // All concurrency slots are in use
                    _logger.LogDebug("All concurrency slots in use, stopping at {TaskCount} tasks", tasks.Count);
                    break;
                }
            }
            
            if (tasks.Any())
            {
                var results = await Task.WhenAll(tasks);
                jobsProcessed = results.Sum();
                
                if (jobsProcessed > 0)
                {
                    _logger.LogInformation("⚡ Processed {JobCount} jobs concurrently", jobsProcessed);
                }
            }
        }
        
        return jobsProcessed;
    }

    private async Task<int> ProcessSingleJobAsync()
    {
        try
        {
            var job = await _jobContext!.DequeueJobAsync<Job>();
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
                            return 1; // Job was processed (failed)
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
                        return 1; // Job was processed (failed)
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
                    return 1; // Job was processed
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing job {JobId}", job.Id);
                    await _jobContext.UpdateJobAsync(job, JobStatus.Failed, error: $"Job execution error: {ex.Message}");
                    return 1; // Job was processed (failed)
                }
            }
            
            return job != null ? 1 : 0;
        }
        catch (InvalidOperationException)
        {
            // No jobs available - this is normal
            _logger.LogDebug("No jobs available in queue");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job");
            return 0;
        }
        finally
        {
            _concurrencyLimiter.Release();
            _logger.LogDebug("Released concurrency slot (available: {Available})", _concurrencyLimiter.CurrentCount);
        }
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
            
            // Set a reasonable timeout (15 mins)
            var timeoutMs = int.Parse(_configuration["HttpTimeoutMs"] ?? "9000000");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            _httpClient.Timeout = Timeout.InfiniteTimeSpan; // Disable HttpClient timeout, we use our own
            
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
}
