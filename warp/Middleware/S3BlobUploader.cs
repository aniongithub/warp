using Warp.Core.Data;

namespace Warp.Middleware;

public class S3BlobUploaderOptions : MiddlewareOptions
{
    public string BucketName { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string Endpoint { get; set; } = string.Empty; // Optional custom endpoint
    public List<string> BlobFields { get; set; } = []; // List of form field names for blobs
    public string ObjectKeyPrefix { get; set; } = string.Empty; // Optional prefix for object key, 
    // if null or empty, a new guid prefix is generated each time
}

public sealed class S3BlobUploader : MiddlewareBase<S3BlobUploaderOptions>
{
    private readonly string _bucketName;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string _region;
    private readonly string _endpoint;
    private readonly List<string> _blobFields;
    private readonly string _objectKeyPrefix;

    public S3BlobUploader(string name, ILogger logger, IDataContext context, S3BlobUploaderOptions options)
        : base(name, logger, context, options)
    {
        _bucketName = options.BucketName;
        _accessKey = options.AccessKey;
        _secretKey = options.SecretKey;
        _region = options.Region;
        _endpoint = options.Endpoint;
        _blobFields = options.BlobFields ?? [];
        _objectKeyPrefix = options.ObjectKeyPrefix;

        Logger.LogDebug("S3BlobUploader configured: Bucket={Bucket}, Region={Region}, Endpoint={Endpoint}", _bucketName, _region, _endpoint);
    }

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        Logger.LogDebug("Starting S3 blob upload for request: {Path}", context.Request.Path);
        if (!context.Request.HasFormContentType)
        {
            Logger.LogWarning("Request does not have form content type.");
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Bad Request: Expected form content type.");
            await next(context);
            return;
        }

        var form = await context.Request.ReadFormAsync();
        var uploadedUrls = new Dictionary<string, string>();
        bool anyMissing = false;
        foreach (var blobField in _blobFields)
        {
            var file = form.Files.FirstOrDefault(f => f.Name == blobField);
            if (file == null)
            {
                Logger.LogWarning("Blob field '{BlobField}' not found in form.", blobField);
                anyMissing = true;
                continue;
            }
            var objectKey = string.IsNullOrEmpty(_objectKeyPrefix)
                ? $"{Guid.NewGuid()}/{file.FileName ?? $"uploaded-{blobField}"}"
                : $"{_objectKeyPrefix}/{file.FileName ?? $"uploaded-{blobField}"}";
            try
            {
                var s3Url = await UploadToS3Async(file.OpenReadStream(), objectKey, file.ContentType);
                Logger.LogInformation("File uploaded to S3: {S3Url}", s3Url);
                var headerName = $"x-{blobField}-blob";
                context.Request.Headers[headerName] = s3Url;
                uploadedUrls[blobField] = s3Url;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "S3 upload failed for field {BlobField}.", blobField);
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"S3 upload failed for field {blobField}.");
                await next(context);
                return;
            }
        }
        if (anyMissing)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync($"Bad Request: Missing one or more blob fields: {string.Join(",", _blobFields.Where(bf => !uploadedUrls.ContainsKey(bf)))}.");
            await next(context);
            return;
        }
        await next(context);
    }

    private async Task<string> UploadToS3Async(Stream fileStream, string objectKey, string contentType)
    {
        // Upload the blob using AWSSDK.S3
        using var s3Client = string.IsNullOrEmpty(_endpoint)
            ? new Amazon.S3.AmazonS3Client(_accessKey, _secretKey, Amazon.RegionEndpoint.GetBySystemName(_region))
            : new Amazon.S3.AmazonS3Client(_accessKey, _secretKey, new Amazon.S3.AmazonS3Config { ServiceURL = _endpoint, ForcePathStyle = true, RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_region) });

        var putRequest = new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = _bucketName,
            Key = objectKey,
            InputStream = fileStream,
            ContentType = contentType,
            AutoCloseStream = true
        };

        var response = await s3Client.PutObjectAsync(putRequest);
        if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
            throw new Exception($"S3 upload failed with status: {response.HttpStatusCode}");

        var endpoint = string.IsNullOrEmpty(_endpoint)
            ? $"https://{_bucketName}.s3.{_region}.amazonaws.com"
            : _endpoint.TrimEnd('/');
        return $"{endpoint}/{objectKey}";
    }
}
