using Microsoft.AspNetCore.Http;
using System.Text;

namespace Warp.Dilithium.Transforms;

/// <summary>
/// Options for volume blob transformation
/// </summary>
public class VolumeBlobTransformOptions
{
    public string VolumePath { get; set; } = "uploads";
}

/// <summary>
/// Transform that converts file content to volume blob URI and vice versa.
/// Works with the VolumeBlobUploader middleware.
/// </summary>
public class VolumeBlobTransform : TransformBase<VolumeBlobTransformOptions>
{
    public VolumeBlobTransform(VolumeBlobTransformOptions options) : base(options)
    {
    }

    /// <summary>
    /// Transform file content to a volume blob URI
    /// </summary>
    public override async Task<object?> ForwardAsync(object? input)
    {
        if (input is not IFormFile file)
            return input;

        // Generate a unique file ID and path
        var fileId = Guid.NewGuid().ToString();
        var fileName = file.FileName ?? "uploaded_file";
        var fileExtension = Path.GetExtension(fileName);
        var savedFileName = $"{fileId}{fileExtension}";
        
        // Create the directory if it doesn't exist
        var volumePath = Path.GetFullPath(Options.VolumePath);
        var userDirectory = Path.Combine(volumePath, fileId);
        Directory.CreateDirectory(userDirectory);
        
        // Save the file
        var filePath = Path.Combine(userDirectory, savedFileName);
        using var fileStream = new FileStream(filePath, FileMode.Create);
        await file.CopyToAsync(fileStream);
        
        // Return the blob URI (relative path from volume root)
        var relativePath = Path.Combine(fileId, savedFileName);
        return $"volume://{relativePath}";
    }

    /// <summary>
    /// Reverse transform: convert volume blob URI back to file content
    /// </summary>
    public override async Task<object?> ReverseAsync(object? transformedInput, IServiceProvider services)
    {
        // Handle different input types - could be string, JsonElement, etc.
        string? blobUri = null;
        
        if (transformedInput is string directString)
        {
            blobUri = directString;
        }
        else if (transformedInput is System.Text.Json.JsonElement jsonElement && jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            blobUri = jsonElement.GetString();
        }
        else if (transformedInput != null)
        {
            blobUri = transformedInput.ToString();
        }
        
        if (string.IsNullOrEmpty(blobUri) || !blobUri.StartsWith("volume://"))
            return transformedInput;

        // Extract the relative path from the URI
        var relativePath = blobUri.Substring("volume://".Length);
        var volumePath = Path.GetFullPath(Options.VolumePath);
        var filePath = Path.Combine(volumePath, relativePath);
        
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Blob file not found: {filePath}");
        
        // Read the file content
        var fileBytes = await File.ReadAllBytesAsync(filePath);
        var fileName = Path.GetFileName(filePath);
        
        // Create a simple file representation that can be used in HTTP requests
        return new BlobFileContent
        {
            Content = fileBytes,
            FileName = fileName,
            ContentType = GetContentType(fileName)
        };
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
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }
}

/// <summary>
/// Represents file content that has been reverse-transformed from a blob URI
/// </summary>
public class BlobFileContent
{
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
}
