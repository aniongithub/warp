using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Warp.Core.Helper;
using Warp.Core.Job;
using Warp.Core.Job.Contexts;

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
                        
                        // Execute the job by dispatching to sync API
                        await ExecuteJobAsync(job, syncUrl);
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

    private async Task ExecuteJobAsync(Job job, string syncUrl)
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
            
            // Add request body if we have parameters and it's not a GET request
            if (job.Parameters.Any() && httpMethod != HttpMethod.Get)
            {
                var jsonContent = JsonSerializer.Serialize(job.Parameters);
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                
                // Add content headers that couldn't be added to request headers
                foreach (var header in job.Headers)
                {
                    if (!request.Headers.Contains(header.Key))
                    {
                        request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }
            
            // Set a reasonable timeout
            var timeoutMs = int.Parse(_configuration["HttpTimeoutMs"] ?? "30000");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            
            // Execute the HTTP request
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(request, cts.Token);
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
}
