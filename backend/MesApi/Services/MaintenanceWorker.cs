using MesApi.Data;
using MesApi.Hubs;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

/// <summary>
/// 常驻维护任务（替代额外引入 Hangfire 的成本，够用且零外部依赖）：
/// 1) 每 5 分钟扫一次"维修在站超时"，推看板告警——板子压在维修台没人管，是产线最常见的隐性损失；
/// 2) 每小时清理过期 refresh token；
/// 3) 每天清理超出保留期的审计日志（默认 90 天）。
/// 注意：过站流水 sn_move_log 永远不清，它是追溯与客户审厂的原始凭证。
/// </summary>
public class MaintenanceWorker(
    IServiceScopeFactory scopeFactory,
    IBoardNotifier notifier,
    IConfiguration config,
    ILogger<MaintenanceWorker> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Maintenance:IntervalMinutes", 5)));
    private readonly int _repairSlaHours = Math.Max(1, config.GetValue("Maintenance:RepairSlaHours", 4));
    private readonly int _auditRetentionDays = Math.Max(7, config.GetValue("Audit:RetentionDays", 90));

    private DateTime _lastHourly = DateTime.MinValue;
    private DateTime _lastDaily = DateTime.MinValue;
    private readonly HashSet<int> _alertedRepairs = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先等 30 秒，别和启动初始化抢数据库连接
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 后台任务任何异常都不能让宿主退出
                logger.LogError(ex, "维护任务执行失败，将在下个周期重试");
            }
        } while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        await ScanRepairSlaAsync(ct);

        var now = DateTime.UtcNow;
        if (now - _lastHourly >= TimeSpan.FromHours(1))
        {
            _lastHourly = now;
            await CleanupRefreshTokensAsync(ct);
        }

        if (now - _lastDaily >= TimeSpan.FromHours(24))
        {
            _lastDaily = now;
            await CleanupAuditLogsAsync(ct);
        }
    }

    /// <summary>维修在站超时：超过 SLA 仍未出站，推一次告警（同一维修单只推一次，避免刷屏）。</summary>
    private async Task ScanRepairSlaAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();

        var deadline = DateTime.UtcNow.AddHours(-_repairSlaHours);
        var overdue = await db.RepairRecords
            .AsNoTracking()
            .Where(r => r.Status == "InProgress" && r.StartedAtUtc < deadline)
            .OrderBy(r => r.StartedAtUtc)
            .Select(r => new
            {
                r.Id,
                r.RepairNo,
                r.OperatorName,
                r.StationCode,
                r.StartedAtUtc,
                Sn = r.SerialUnit!.Sn
            })
            .Take(50)
            .ToListAsync(ct);

        if (overdue.Count == 0) return;

        // 已关闭的单据不再需要告警，顺手清掉缓存，防止 HashSet 无限增长
        var overdueIds = overdue.Select(x => x.Id).ToHashSet();
        _alertedRepairs.RemoveWhere(id => !overdueIds.Contains(id));

        foreach (var item in overdue)
        {
            if (!_alertedRepairs.Add(item.Id)) continue;

            var hours = (int)(DateTime.UtcNow - item.StartedAtUtc).TotalHours;
            var message = $"维修超时：SN {item.Sn}（第{item.RepairNo}次维修）已在 {item.StationCode} 停留 {hours} 小时，请跟进";

            logger.LogWarning("维修超时告警：SN={Sn} RepairNo={RepairNo} 停留 {Hours}h 维修人 {Operator}",
                item.Sn, item.RepairNo, hours, item.OperatorName);

            await notifier.AlertAsync(new BoardAlertEvent("RepairSla", "warning", message, DateTime.UtcNow), ct);
        }
    }

    private async Task CleanupRefreshTokensAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();

        // 过期超过 30 天的令牌已无复用检测价值，删掉避免表膨胀
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var deleted = await db.RefreshTokens
            .Where(t => t.ExpiresAtUtc < cutoff || (t.RevokedAtUtc != null && t.RevokedAtUtc < cutoff))
            .ExecuteDeleteAsync(ct);

        if (deleted > 0) logger.LogInformation("清理过期 refresh token {Count} 条", deleted);
    }

    private async Task CleanupAuditLogsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-_auditRetentionDays);
        var deleted = await db.AuditLogs
            .Where(a => a.CreatedAtUtc < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0) logger.LogInformation("清理 {Days} 天前审计日志 {Count} 条", _auditRetentionDays, deleted);
    }
}
