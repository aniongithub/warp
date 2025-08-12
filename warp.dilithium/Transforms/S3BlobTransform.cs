using Microsoft.AspNetCore.Http;
using Amazon.S3;
using Amazon.S3.Model;

namespace Warp.Dilithium.Transforms;

/// <summary>
/// Options for S3 blob transformation
/// </summary>
public class S3BlobTransformOptions
{
    public string BucketName { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string? Endpoint { get; set; } = null; // For MinIO or custom S3-compatible endpoints
    public string ObjectKeyPrefix { get; set; } = string.Empty; // Optional prefix for object keys
    public bool ForcePathStyle { get; set; } = true; // Required for MinIO and most custom endpoints
}

/// <summary>
/// Transform that converts file content to S3 blob URI and vice versa.
/// Compatible with both AWS S3 and MinIO.
/// </summary>
public class S3BlobTransform : TransformBase<S3BlobTransformOptions>
{
    public S3BlobTransform(S3BlobTransformOptions options) : base(options)
    {
        if (string.IsNullOrEmpty(options.BucketName))
            throw new ArgumentException("BucketName is required", nameof(options));
        if (string.IsNullOrEmpty(options.AccessKey))
            throw new ArgumentException("AccessKey is required", nameof(options));
        if (string.IsNullOrEmpty(options.SecretKey))
            throw new ArgumentException("SecretKey is required", nameof(options));
    }

    /// <summary>
    /// Transform file content to an S3 blob URI
    /// </summary>
    public override async Task<object?> ForwardAsync(object? input)
    {
        if (input is not IFormFile file)
            return input;

        // Generate a unique object key
        var fileId = Guid.NewGuid().ToString();
        var fileName = file.FileName ?? "uploaded_file";
        var objectKey = string.IsNullOrEmpty(Options.ObjectKeyPrefix)
            ? $"{fileId}/{fileName}"
            : $"{Options.ObjectKeyPrefix}/{fileId}/{fileName}";

        try
        {
            using var s3Client = CreateS3Client();
            
            var putRequest = new PutObjectRequest
            {
                BucketName = Options.BucketName,
                Key = objectKey,
                InputStream = file.OpenReadStream(),
                ContentType = file.ContentType ?? GetContentType(fileName),
                AutoCloseStream = true
            };

            var response = await s3Client.PutObjectAsync(putRequest);
            if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
                throw new Exception($"S3 upload failed with status: {response.HttpStatusCode}");

            // Return the S3 URI (s3://bucket/key format for internal use)
            return $"s3://{Options.BucketName}/{objectKey}";
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to upload file to S3: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reverse transform: convert S3 blob URI back to file content
    /// </summary>
    public override async Task<object?> ReverseAsync(object? transformedInput, IServiceProvider services)
    {
        // Handle different input types - could be string, JsonElement, etc.
        string? s3Uri = null;
        
        if (transformedInput is string directString)
        {
            s3Uri = directString;
        }
        else if (transformedInput is System.Text.Json.JsonElement jsonElement && jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            s3Uri = jsonElement.GetString();
        }
        else if (transformedInput != null)
        {
            s3Uri = transformedInput.ToString();
        }
        
        if (string.IsNullOrEmpty(s3Uri) || !s3Uri.StartsWith("s3://"))
            return transformedInput;

        try
        {
            // Parse the S3 URI: s3://bucket/key
            var uri = new Uri(s3Uri);
            var bucketName = uri.Host;
            var objectKey = uri.AbsolutePath.TrimStart('/');

            if (bucketName != Options.BucketName)
                throw new InvalidOperationException($"Bucket mismatch: expected {Options.BucketName}, got {bucketName}");

            using var s3Client = CreateS3Client();
            
            var getRequest = new GetObjectRequest
            {
                BucketName = bucketName,
                Key = objectKey
            };

            using var response = await s3Client.GetObjectAsync(getRequest);
            using var memoryStream = new MemoryStream();
            await response.ResponseStream.CopyToAsync(memoryStream);
            
            var fileName = Path.GetFileName(objectKey);
            
            // Create a file representation that can be used in HTTP requests
            return new BlobFileContent
            {
                Content = memoryStream.ToArray(),
                FileName = fileName,
                ContentType = response.Headers.ContentType ?? GetContentType(fileName)
            };
        }
        catch (AmazonS3Exception ex)
        {
            throw new FileNotFoundException($"S3 object not found: {s3Uri}", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to download file from S3: {ex.Message}", ex);
        }
    }

    private AmazonS3Client CreateS3Client()
    {
        if (string.IsNullOrEmpty(Options.Endpoint))
        {
            // Standard AWS S3
            return new AmazonS3Client(Options.AccessKey, Options.SecretKey, Amazon.RegionEndpoint.GetBySystemName(Options.Region));
        }
        else
        {
            // MinIO or custom S3-compatible endpoint
            var config = new AmazonS3Config
            {
                ServiceURL = Options.Endpoint.TrimEnd('/'),
                ForcePathStyle = Options.ForcePathStyle,
                UseHttp = Options.Endpoint.StartsWith("http://"), // Support both HTTP and HTTPS
                // Don't set RegionEndpoint for custom endpoints - it can cause conflicts
            };
            return new AmazonS3Client(Options.AccessKey, Options.SecretKey, config);
        }
    }

    private static string GetContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".zip" => "application/zip",
            ".rar" => "application/x-rar-compressed",
            ".7z" => "application/x-7z-compressed",
            ".tar" => "application/x-tar",
            ".gz" => "application/gzip",
            ".mp4" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".csv" => "text/csv",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream"
        };
    }
}