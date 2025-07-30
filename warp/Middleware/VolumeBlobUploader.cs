using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using Warp.Core.Helper;

namespace Warp.Middleware;

public class VolumeBlobUploaderOptions : MiddlewareOptions
{
    public string VolumePath { get; set; } = "/data/blobs"; // Path to shared volume
    public List<string> BlobFields { get; set; } = []; // List of form field names for blobs
    public string ObjectKeyPrefix { get; set; } = string.Empty; // Optional prefix for object key, 
    // if null or empty, a new guid prefix is generated each time
}

public sealed class VolumeBlobUploader : MiddlewareBase<VolumeBlobUploaderOptions>
{
    private readonly string _volumePath;
    private readonly List<string> _blobFields;
    private readonly string _objectKeyPrefix;

    public VolumeBlobUploader(string name, ILogger logger, IDataContext context, VolumeBlobUploaderOptions options)
        : base(name, logger, context, options)
    {
        _volumePath = options.VolumePath;
        _blobFields = options.BlobFields ?? [];
        _objectKeyPrefix = options.ObjectKeyPrefix;

        Logger.LogDebug("VolumeBlobUploader configured: VolumePath={VolumePath}", _volumePath);
    }

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        Logger.LogDebug("Starting volume blob upload for request: {Path}", context.Request.Path);
        if (!context.Request.HasFormContentType)
        {
            Logger.LogWarning("Request does not have form content type.");
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Bad Request: Expected form content type.");
            await next(context);
            return;
        }

        var form = await context.Request.ReadFormAsync();
        var uploadedPaths = new Dictionary<string, string>();
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
            var volumeFilePath = Path.Combine(_volumePath, objectKey);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(volumeFilePath)!);
                using (var fileStream = new FileStream(volumeFilePath, FileMode.Create, FileAccess.Write))
                {
                    await file.OpenReadStream().CopyToAsync(fileStream);
                }
                Logger.LogInformation("File uploaded to volume: {Path}", volumeFilePath);
                var headerName = $"x-{blobField}-blob";
                context.Request.Headers[headerName] = volumeFilePath;
                uploadedPaths[blobField] = volumeFilePath;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Volume upload failed for field {BlobField}.", blobField);
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"Volume upload failed for field {blobField}.");
                await next(context);
                return;
            }
        }
        if (anyMissing)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync($"Bad Request: Missing one or more blob fields: {string.Join(",", _blobFields.Where(bf => !uploadedPaths.ContainsKey(bf)))}.");
            await next(context);
            return;
        }
        await next(context);
    }
}
