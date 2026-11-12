using System.Diagnostics;
using System.Reflection;
using MesApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MesApi.Infrastructure;

/// <summary>
/// 健康检查：/health/live 只判进程活着（给负载均衡器/容器 liveness），
/// /health/ready 判依赖是否可用（DB 能连、迁移已应用、磁盘还有空间），不可用时返回 503。
/// 两者分开，避免数据库抖动时容器被反复重启。
/// </summary>
public static class HealthEndpoints
{
    public static IServiceCollection AddMesHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
        return services;
    }

    public static void MapMesHealthChecks(this WebApplication app)
    {
        var startedAt = DateTime.UtcNow;

        // 同时接受 HEAD：运维习惯用 curl -I 探活
        app.MapMethods("/health/live", ["GET", "HEAD"], () => Results.Ok(new
        {
            status = "healthy",
            uptimeSeconds = (int)(DateTime.UtcNow - startedAt).TotalSeconds,
            version = Version,
            time = DateTime.UtcNow
        })).AllowAnonymous().WithTags("health");

        app.MapMethods("/health/ready", ["GET", "HEAD"], async (
            HealthCheckService health, MesDbContext db, IConfiguration cfg,
            ICacheService cache, Messaging.IMessageBus bus, Storage.IFileStorage storage) =>
        {
            var report = await health.CheckHealthAsync(c => c.Tags.Contains("ready"));
            var pending = await PendingMigrationsAsync(db);
            var diskFreeGb = FreeDiskGb();

            // 主动读一次缓存：触发 Redis 懒连接，让探活结果反映真实中间件状态（而不是"还没连过"）
            await cache.GetAsync<string>("health:probe");

            var ok = report.Status == HealthStatus.Healthy && pending == 0;
            var body = new
            {
                status = ok ? "ready" : "not-ready",
                provider = cfg["Database:Provider"] ?? "MySql",
                version = Version,
                pendingMigrations = pending,
                diskFreeGb,
                // 三个平台组件的实际后端：redis/memory、rabbitmq/in-memory、s3-minio/local-disk
                infra = new
                {
                    cache = cache.Backend,
                    messaging = bus.Backend,
                    storage = storage.Backend,
                    scheduler = "quartz"
                },
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    durationMs = (int)e.Value.Duration.TotalMilliseconds,
                    detail = e.Value.Description
                }),
                time = DateTime.UtcNow
            };
            if (ok) return Results.Ok(body);
            return Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous().WithTags("health");

        // 兼容早期探活地址
        app.MapGet("/health", async (HealthCheckService health) =>
        {
            var report = await health.CheckHealthAsync(c => c.Tags.Contains("ready"));
            if (report.Status == HealthStatus.Healthy)
                return Results.Ok(new { status = "ok", version = Version, time = DateTime.UtcNow });
            return Results.Json(new { status = "degraded" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous().WithTags("health");
    }

    private static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private static async Task<int> PendingMigrationsAsync(MesDbContext db)
    {
        try
        {
            // Sqlite 演示模式走 EnsureCreated，没有迁移历史，视为无待应用迁移
            if (db.Database.IsSqlite()) return 0;
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToHashSet();
            return (await db.Database.GetPendingMigrationsAsync()).Count(m => !applied.Contains(m));
        }
        catch
        {
            return -1;
        }
    }

    private static double FreeDiskGb()
    {
        try
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory) ?? "/";
            var drive = new DriveInfo(root);
            return Math.Round(drive.AvailableFreeSpace / 1024d / 1024d / 1024d, 1);
        }
        catch
        {
            return -1;
        }
    }
}

/// <summary>数据库连通性检查：带 3 秒超时，避免探活自己把线程占死。</summary>
public class DatabaseHealthCheck(MesDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            var canConnect = await db.Database.CanConnectAsync(cts.Token);
            sw.Stop();

            return canConnect
                ? HealthCheckResult.Healthy($"数据库可连接（{sw.ElapsedMilliseconds}ms）")
                : HealthCheckResult.Unhealthy("数据库连接失败");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"数据库连接异常：{ex.GetType().Name}");
        }
    }
}
