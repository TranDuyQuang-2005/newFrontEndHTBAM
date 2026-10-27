using HTBAM.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Minio;
using Minio.DataModel.Args;

namespace HTBAM.Infrastructure.Services;

public sealed class MinioObjectStorage : IObjectStorage
{
    private readonly IMinioClient _client;
    private readonly IMinioClient _publicClient;
    private readonly string _bucket;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _ready;

    public MinioObjectStorage(IConfiguration config)
    {
        var endpoint = config["ObjectStorage:Endpoint"] ?? "localhost:9000";
        var publicEndpoint = config["ObjectStorage:PublicEndpoint"] ?? endpoint;
        var accessKey = config["ObjectStorage:AccessKey"] ?? throw new InvalidOperationException("ObjectStorage:AccessKey missing");
        var secretKey = config["ObjectStorage:SecretKey"] ?? throw new InvalidOperationException("ObjectStorage:SecretKey missing");
        _bucket = config["ObjectStorage:Bucket"] ?? "htbam-private";
        var secure = config.GetValue("ObjectStorage:Secure", false);
        _client = new MinioClient().WithEndpoint(endpoint).WithCredentials(accessKey, secretKey).WithSSL(secure).Build();
        _publicClient = new MinioClient().WithEndpoint(publicEndpoint).WithCredentials(accessKey, secretKey).WithSSL(secure).Build();
    }

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (_ready) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_ready) return;
            Exception? last = null;
            for (var attempt = 1; attempt <= 10 && !_ready; attempt++)
            {
                try
                {
                    var exists = await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucket), ct);
                    if (!exists) await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucket), ct);
                    _ready = true;
                }
                catch (Exception ex) when (attempt < 10)
                {
                    last = ex;
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            if (!_ready) throw new InvalidOperationException("Object Storage chưa sẵn sàng sau nhiều lần thử.", last);
        }
        finally { _initLock.Release(); }
    }

    public async Task PutAsync(string objectKey, Stream stream, long sizeBytes, string contentType, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await _client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(_bucket)
            .WithObject(objectKey)
            .WithStreamData(stream)
            .WithObjectSize(sizeBytes)
            .WithContentType(contentType), ct);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        try { await _client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucket).WithObject(objectKey), ct); }
        catch { /* delete best-effort; DB soft-delete vẫn ngăn sử dụng lại */ }
    }

    public async Task<string> GetReadUrlAsync(string objectKey, int expirySeconds = 900, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        expirySeconds = Math.Clamp(expirySeconds, 60, 86400);
        return await _publicClient.PresignedGetObjectAsync(new PresignedGetObjectArgs().WithBucket(_bucket).WithObject(objectKey).WithExpiry(expirySeconds));
    }

    public async Task<string> GetInternalReadUrlAsync(string objectKey, int expirySeconds = 900, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        expirySeconds = Math.Clamp(expirySeconds, 60, 86400);
        return await _client.PresignedGetObjectAsync(new PresignedGetObjectArgs().WithBucket(_bucket).WithObject(objectKey).WithExpiry(expirySeconds));
    }

    public async Task<string> GetInternalWriteUrlAsync(string objectKey, int expirySeconds = 900, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        expirySeconds = Math.Clamp(expirySeconds, 60, 86400);
        return await _client.PresignedPutObjectAsync(new PresignedPutObjectArgs().WithBucket(_bucket).WithObject(objectKey).WithExpiry(expirySeconds));
    }

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default)
        => await StatAsync(objectKey, ct) is not null;

    public async Task<ObjectStorageMetadata?> StatAsync(string objectKey, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        try
        {
            var stat = await _client.StatObjectAsync(new StatObjectArgs().WithBucket(_bucket).WithObject(objectKey), ct);
            return new ObjectStorageMetadata(stat.Size, stat.ContentType);
        }
        catch (Minio.Exceptions.ObjectNotFoundException) { return null; }
        catch (Minio.Exceptions.BucketNotFoundException) { return null; }
    }

    public async Task CopyRangeToAsync(string objectKey, long offset, long length, Stream destination, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0) return;
        await _client.GetObjectAsync(new GetObjectArgs()
            .WithBucket(_bucket)
            .WithObject(objectKey)
            .WithOffsetAndLength(offset, length)
            .WithCallbackStream((source, callbackCt) => source.CopyToAsync(destination, callbackCt)), ct);
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try { await EnsureReadyAsync(ct); return await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucket), ct); }
        catch { return false; }
    }
}
