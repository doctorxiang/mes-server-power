using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;

namespace MesApi.Infrastructure.Storage;

/// <summary>
/// 非结构化文件存储门面：图纸、SOP、检验报告、现场照片。
/// 配置了 MinIO 就走 S3 协议（MinIO 完全兼容 AWS S3），否则降级本地磁盘目录（演示模式）。
/// </summary>
public interface IFileStorage
{
    Task SaveAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>打开文件流（调用方负责 Dispose）。</summary>
    Task<Stream?> OpenAsync(string objectKey, CancellationToken ct = default);

    Task DeleteAsync(string objectKey, CancellationToken ct = default);

    string Backend { get; }
}

/// <summary>本地磁盘实现：演示模式用，文件放 files/ 目录。</summary>
public sealed class LocalFileStorage(IHostEnvironment env, ILogger<LocalFileStorage> logger) : IFileStorage
{
    private readonly string _root = Path.Combine(env.ContentRootPath, "files");

    public string Backend => "local-disk";

    public async Task SaveAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = SafePath(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = File.Create(path);
        await content.CopyToAsync(fs, ct);
    }

    public Task<Stream?> OpenAsync(string objectKey, CancellationToken ct = default)
    {
        var path = SafePath(objectKey);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        var path = SafePath(objectKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>防目录穿越：objectKey 必须落在根目录内，upload/../../etc/passwd 这类直接拒绝。</summary>
    private string SafePath(string objectKey)
    {
        var full = Path.GetFullPath(Path.Combine(_root, objectKey));
        if (!full.StartsWith(Path.GetFullPath(_root), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("非法的对象键");
        logger.LogDebug("本地存储操作：{Key}", objectKey);
        return full;
    }
}

/// <summary>MinIO / 任意 S3 兼容存储实现。</summary>
public sealed class S3FileStorage(IConfiguration config, ILogger<S3FileStorage> logger) : IFileStorage, IDisposable
{
    private readonly string _bucket = config["Minio:Bucket"] ?? "mes-docs";
    private readonly AmazonS3Client? _client = CreateClient(config);

    public string Backend => _client is not null ? "s3/minio" : "local-disk";

    private static AmazonS3Client? CreateClient(IConfiguration config)
    {
        var endpoint = config["Minio:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        var s3Config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true, // MinIO 必须用 path-style，不能用虚拟主机寻址
            AuthenticationRegion = "us-east-1",
        };
        return new AmazonS3Client(config["Minio:AccessKey"] ?? "", config["Minio:SecretKey"] ?? "", s3Config);
    }

    public async Task SaveAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default)
    {
        var client = Require();
        await EnsureBucketAsync(ct);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, ct);
    }

    public async Task<Stream?> OpenAsync(string objectKey, CancellationToken ct = default)
    {
        var client = Require();
        try
        {
            var resp = await client.GetObjectAsync(_bucket, objectKey, ct);
            return resp.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        var client = Require();
        await client.DeleteObjectAsync(_bucket, objectKey, ct);
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        try
        {
            var exists = await AmazonS3Util.DoesS3BucketExistV2Async(_client, _bucket);
            if (!exists) await _client.PutBucketAsync(_bucket, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "创建/检查 bucket 失败（{Bucket}）", _bucket);
        }
    }

    private AmazonS3Client Require()
    {
        if (_client is null) throw new InvalidOperationException("MinIO 未配置，请检查 appsettings 的 Minio 节");
        return _client;
    }

    public void Dispose() => _client?.Dispose();
}
