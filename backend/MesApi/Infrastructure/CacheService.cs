using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace MesApi.Infrastructure;

/// <summary>
/// 缓存门面：配置了 Redis 就用 Redis，没配置（或 Redis 挂了）自动降级进程内缓存。
/// 好处：演示模式零依赖可跑；上产线把连接串配上，无需改任何业务代码。
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key);

    Task SetAsync<T>(string key, T value, TimeSpan ttl);

    Task RemoveAsync(string key);

    /// <summary>当前生效的后端，健康检查与排障用。</summary>
    string Backend { get; }
}

public sealed class CacheService : ICacheService, IDisposable
{
    private readonly IMemoryCache _memory = new MemoryCache(new MemoryCacheOptions());
    private readonly ILogger<CacheService> _logger;
    private readonly string? _connectionString;

    private ConnectionMultiplexer? _redis;
    private IDatabase? _db;
    private DateTime _lastRetryAt = DateTime.MinValue;
    private readonly TimeSpan _retryInterval = TimeSpan.FromSeconds(30);

    public string Backend => _db is not null ? "redis" : "memory";

    public CacheService(IConfiguration config, ILogger<CacheService> logger)
    {
        _logger = logger;
        _connectionString = config["Redis:ConnectionString"];
    }

    /// <summary>懒连接：第一次用到才拨号，失败 30 秒后再试，绝不阻塞请求、绝不抛给业务。</summary>
    private IDatabase? TryGetRedis()
    {
        if (_db is not null) return _db;
        if (string.IsNullOrWhiteSpace(_connectionString)) return null;
        if (DateTime.UtcNow - _lastRetryAt < _retryInterval) return null;

        _lastRetryAt = DateTime.UtcNow;
        try
        {
            _redis = ConnectionMultiplexer.Connect(_connectionString);
            _db = _redis.GetDatabase();
            _logger.LogInformation("Redis 缓存已连接");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis 连接失败，降级进程内缓存（30 秒后重试）");
        }
        return _db;
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        var db = TryGetRedis();
        if (db is null) return _memory.Get<T?>(key);

        try
        {
            var value = await db.StringGetAsync(key);
            return value.HasValue ? JsonSerializer.Deserialize<T>(value.ToString()) : default;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis 读取失败，降级内存缓存");
            return _memory.Get<T?>(key);
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl)
    {
        var db = TryGetRedis();
        if (db is null)
        {
            _memory.Set(key, value, ttl);
            return;
        }

        try
        {
            await db.StringSetAsync(key, JsonSerializer.Serialize(value), ttl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis 写入失败，降级内存缓存");
            _memory.Set(key, value, ttl);
        }
    }

    public async Task RemoveAsync(string key)
    {
        var db = TryGetRedis();
        if (db is null)
        {
            _memory.Remove(key);
            return;
        }

        try { await db.KeyDeleteAsync(key); }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis 删除失败"); _memory.Remove(key); }
    }

    public void Dispose()
    {
        _redis?.Dispose();
        _memory.Dispose();
    }
}
