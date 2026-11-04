using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;

namespace TodoSaaS.Api.Services;

public interface IFileStorageService
{
    Task EnsureBucketExistsAsync();
    Task<string> GetPresignedUploadUrlAsync(string fileName, string contentType);
    Task<string> UploadFileAsync(string fileName, Stream content, string contentType);
    Task<string> GetDownloadUrlAsync(string key);
}

public class S3FileStorageService : IFileStorageService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly ILogger<S3FileStorageService> _logger;

    public S3FileStorageService(IAmazonS3 s3Client, IConfiguration config, ILogger<S3FileStorageService> logger)
    {
        _s3Client = s3Client;
        _bucketName = config["Storage:BucketName"] ?? "todosaas-files";
        _logger = logger;
    }

    public async Task EnsureBucketExistsAsync()
    {
        try
        {
            var exists = await AmazonS3Util.DoesS3BucketExistV2Async(_s3Client, _bucketName);
            if (!exists)
            {
                await _s3Client.PutBucketAsync(new PutBucketRequest { BucketName = _bucketName });
                _logger.LogInformation("Created S3 bucket: {Bucket}", _bucketName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify/create S3 bucket {Bucket}", _bucketName);
        }
    }

    public async Task<string> GetPresignedUploadUrlAsync(string fileName, string contentType)
    {
        await EnsureBucketExistsAsync();

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = $"uploads/{Guid.NewGuid()}_{fileName}",
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.AddMinutes(15),
            ContentType = contentType
        };

        return await _s3Client.GetPreSignedURLAsync(request);
    }

    public async Task<string> UploadFileAsync(string fileName, Stream content, string contentType)
    {
        await EnsureBucketExistsAsync();

        var key = $"uploads/{Guid.NewGuid()}_{fileName}";
        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            InputStream = content,
            ContentType = contentType
        };

        await _s3Client.PutObjectAsync(request);
        _logger.LogInformation("File uploaded to S3/MinIO: {Key}", key);
        return key;
    }

    public Task<string> GetDownloadUrlAsync(string key)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddHours(1)
        };

        var url = _s3Client.GetPreSignedURL(request);
        return Task.FromResult(url);
    }
}
