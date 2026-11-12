using System.Text.Json;
using MesApi.Data;
using MesApi.Domain;
using MesApi.Hubs;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace MesApi.Scheduling;

/// <summary>
/// 维修超时预警：每 5 分钟扫一次，超过 SLA（默认 4 小时）仍在维修台的 SN 推看板告警。
/// 同一维修单只推一次（静态集合去重，单实例部署够用；多实例时改用 Redis 去重）。
/// </summary>
[DisallowConcurrentExecution]
public class RepairSlaAlertJob(IServiceScopeFactory scopeFactory, IBoardNotifier notifier, IConfiguration config, ILogger<RepairSlaAlertJob> logger) : IJob
{
    // 去重缓存放静态：Quartz 每次触发 new 一个 Job 实例（默认 IJobFactory 行为）
    private static readonly HashSet<int> Alerted = [];
    private static readonly object Lock = new();

    public static JobKey Key => new("RepairSlaAlert", "maintenance");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        var slaHours = Math.Max(1, config.GetValue("Maintenance:RepairSlaHours", 4));
        var deadline = DateTime.UtcNow.AddHours(-slaHours);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();

        var overdue = await db.RepairRecords.AsNoTracking()
            .Where(r => r.Status == "InProgress" && r.StartedAtUtc < deadline)
            .OrderBy(r => r.StartedAtUtc)
            .Select(r => new { r.Id, r.RepairNo, r.OperatorName, r.StationCode, r.StartedAtUtc, Sn = r.SerialUnit!.Sn })
            .Take(50)
            .ToListAsync(ct);

        if (overdue.Count == 0) return;

        var overdueIds = overdue.Select(x => x.Id).ToHashSet();
        lock (Lock) Alerted.RemoveWhere(id => !overdueIds.Contains(id));

        foreach (var item in overdue)
        {
            bool isNew;
            lock (Lock) isNew = Alerted.Add(item.Id);
            if (!isNew) continue;

            var hours = (int)(DateTime.UtcNow - item.StartedAtUtc).TotalHours;
            logger.LogWarning("维修超时：SN={Sn} 停留 {Hours}h（SLA {Sla}h）", item.Sn, hours, slaHours);
            await notifier.AlertAsync(new BoardAlertEvent(
                "RepairSla", "warning",
                $"维修超时：SN {item.Sn}（第{item.RepairNo}次维修）已在 {item.StationCode} 停留 {hours} 小时，请跟进",
                DateTime.UtcNow), ct);
        }
    }
}

/// <summary>
/// 交期预警：每 30 分钟扫一次，Released/InProduction 状态且 24 小时内要交、完成率不到 100% 的工单推看板告警。
/// </summary>
[DisallowConcurrentExecution]
public class OverdueOrderAlertJob(IServiceScopeFactory scopeFactory, IBoardNotifier notifier, ILogger<OverdueOrderAlertJob> logger) : IJob
{
    private static readonly HashSet<int> Alerted = [];
    private static readonly object Lock = new();

    public static JobKey Key => new("OverdueOrderAlert", "maintenance");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();

        var deadline = DateTime.UtcNow.AddHours(24);
        var rows = await db.WorkOrders.AsNoTracking()
            .Where(o => (o.Status == nameof(WorkOrderStatus.Released) || o.Status == nameof(WorkOrderStatus.InProduction))
                        && o.DueDateUtc < deadline)
            .Select(o => new
            {
                o.Id,
                o.OrderNo,
                o.PlanQty,
                o.DueDateUtc,
                Done = o.Units.Count(u => u.Status == nameof(UnitStatus.Completed))
            })
            .ToListAsync(ct);

        var current = rows.Select(r => r.Id).ToHashSet();
        lock (Lock) Alerted.RemoveWhere(id => !current.Contains(id));

        foreach (var r in rows.Where(r => r.Done < r.PlanQty))
        {
            bool isNew;
            lock (Lock) isNew = Alerted.Add(r.Id);
            if (!isNew) continue;

            var hours = Math.Max(0, (int)(r.DueDateUtc - DateTime.UtcNow).TotalHours);
            logger.LogWarning("交期预警：工单 {Order} 距交期 {Hours}h 完成 {Done}/{Plan}", r.OrderNo, hours, r.Done, r.PlanQty);
            await notifier.AlertAsync(new BoardAlertEvent(
                "OrderDue", "danger",
                $"交期预警：工单 {r.OrderNo} 距交期不足 {hours} 小时，仅完成 {r.Done}/{r.PlanQty} 台，请跟进",
                DateTime.UtcNow), ct);
        }
    }
}

/// <summary>
/// 夜间报表：每天 02:30（东八区）生成前一天按产线的生产日报，落 daily_report 表。
/// 晨会直接从界面取数，不再人工导 Excel。
/// </summary>
[DisallowConcurrentExecution]
public class NightlyReportJob(IServiceScopeFactory scopeFactory, ILogger<NightlyReportJob> logger) : IJob
{
    public static JobKey Key => new("NightlyReport", "reports");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();

        // 默认跑"昨天"，触发参数传 date 可手动补历史
        var date = context.MergedJobDataMap.GetString("date") ?? FactoryClock.DateOf(DateTime.UtcNow.AddDays(-1));
        var dayStart = FactoryClock.StartOfLocalDayUtc(date);
        var dayEnd = FactoryClock.StartOfLocalDayUtc(date).AddDays(1);

        var lines = await db.Lines.AsNoTracking().Where(l => l.IsActive).ToListAsync(ct);

        foreach (var line in lines)
        {
            var units = await db.Units.AsNoTracking()
                .Where(u => u.LineId == line.Id && u.ProductionDate == date)
                .Select(u => new { u.Id, u.Status })
                .ToListAsync(ct);

            var failLogs = await db.MoveLogs.AsNoTracking()
                .Where(x => x.LineId == line.Id && x.ProductionDate == date && x.Result == "Fail")
                .GroupBy(x => new { x.DefectCodeId, Code = x.DefectCode!.Code, Name = x.DefectCode.Name })
                .Select(g => new { g.Key.Code, g.Key.Name, Count = g.Count() })
                .ToListAsync(ct);

            var completed = units.Count(u => u.Status == nameof(UnitStatus.Completed));
            var input = units.Count;
            var fail = failLogs.Sum(f => f.Count);
            var yieldTenths = input == 0 ? 0 : (int)Math.Round(completed * 1000d / input);
            var topDefects = JsonSerializer.Serialize(
                failLogs.OrderByDescending(f => f.Count).Take(5)
                    .Select(f => new { f.Code, f.Name, f.Count }).ToList());

            var existing = await db.DailyReports
                .FirstOrDefaultAsync(r => r.ReportDate == date && r.LineId == line.Id, ct);
            if (existing is null)
            {
                db.DailyReports.Add(new DailyReport
                {
                    ReportDate = date,
                    LineId = line.Id,
                    InputQty = input,
                    CompletedQty = completed,
                    FailQty = fail,
                    YieldTenths = yieldTenths,
                    TopDefectsJson = topDefects
                });
            }
            else
            {
                existing.InputQty = input;
                existing.CompletedQty = completed;
                existing.FailQty = fail;
                existing.YieldTenths = yieldTenths;
                existing.TopDefectsJson = topDefects;
                existing.GeneratedAtUtc = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("夜间报表已生成：date={Date} lines={Count}", date, lines.Count);
    }
}

/// <summary>
/// 数据保养：每天 03:30 清理过期 refresh token（30 天前）与超保留期的审计日志（默认 90 天）。
/// sn_move_log 永不清理——那是追溯与审厂的原始凭证。
/// </summary>
[DisallowConcurrentExecution]
public class HousekeepingJob(IServiceScopeFactory scopeFactory, ILogger<HousekeepingJob> logger) : IJob
{
    public static JobKey Key => new("Housekeeping", "maintenance");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var tokenCutoff = DateTime.UtcNow.AddDays(-30);
        var tokens = await db.RefreshTokens
            .Where(t => t.ExpiresAtUtc < tokenCutoff || (t.RevokedAtUtc != null && t.RevokedAtUtc < tokenCutoff))
            .ExecuteDeleteAsync(ct);

        var auditDays = Math.Max(7, config.GetValue("Audit:RetentionDays", 90));
        var auditCutoff = DateTime.UtcNow.AddDays(-auditDays);
        var audits = await db.AuditLogs
            .Where(a => a.CreatedAtUtc < auditCutoff)
            .ExecuteDeleteAsync(ct);

        if (tokens + audits > 0)
            logger.LogInformation("数据保养完成：清理过期令牌 {Tokens} 条、{Days} 天前审计 {Audits} 条", tokens, auditDays, audits);
    }
}
