using MesApi.Data;
using MesApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

// ---------------- 契约 ----------------

public record ApsScheduleRow(
    int OrderId,
    string OrderNo,
    string ProductName,
    string LineName,
    int Qty,
    string Status,
    string BottleneckStep,
    int BottleneckTaktSeconds,
    double UnitsPerHour,
    string StartLocal,
    string EndLocal,
    string DueDateLocal,
    bool OnTime,
    double LoadHours);

public record ApsResult(
    string GeneratedAtLocal,
    double TotalHours,
    int PlannedOrders,
    IReadOnlyList<ApsScheduleRow> Rows,
    IReadOnlyList<string> BottleneckNotes);

public interface IApsService
{
    Task<ApsResult> RunAsync(bool includeDraft, CancellationToken ct);
}

/// <summary>
/// 有限产能正排（Forward Scheduling），APS 的最小可用版本：
/// 1) 取开放工单（已下发，可选草稿），按 优先级 asc → 交期 asc 排队；
/// 2) 每个工单瓶颈节拍 = 该机型工艺路线中 TaktSeconds 最大的工序（流水线由最慢工序决定产量）；
/// 3) 时产能 = 3600 ÷ 瓶颈节拍 ÷ 良率折损（默认按 97% 折损预留返修）；
/// 4) 同一条产线串行累加，得到每单的开工/完工时刻，并对比交期给 OnTime 标记。
/// 简化项（文档已说明）：按自然日连续产能估算，不展开班次日历与换型时间。
/// </summary>
public class ApsService(MesDbContext db, IConfiguration config) : IApsService
{
    public async Task<ApsResult> RunAsync(bool includeDraft, CancellationToken ct)
    {
        var yieldLoss = Math.Clamp(config.GetValue("Aps:YieldLossPercent", 3), 0, 50) / 100d;
        var efficiency = 1d - yieldLoss;

        // 在制工单也要排在前面（继续做完剩余量），否则排程结果会漏掉产线上正在跑的活
        var statuses = includeDraft
            ? new[] { nameof(WorkOrderStatus.InProduction), nameof(WorkOrderStatus.Released), nameof(WorkOrderStatus.Draft) }
            : new[] { nameof(WorkOrderStatus.InProduction), nameof(WorkOrderStatus.Released) };

        var orders = await db.WorkOrders.AsNoTracking()
            .Where(o => statuses.Contains(o.Status))
            .OrderBy(o => o.Status == nameof(WorkOrderStatus.InProduction) ? 0 : 1)
            .ThenBy(o => o.Priority).ThenBy(o => o.DueDateUtc).ThenBy(o => o.Id)
            .Select(o => new
            {
                o.Id,
                o.OrderNo,
                o.PlanQty,
                Done = o.Units.Count(u => u.Status == nameof(UnitStatus.Completed)),
                o.Status,
                o.DueDateUtc,
                ProductName = o.Product!.Name,
                LineName = o.Line!.Name,
                LineId = o.LineId,
                // 瓶颈 = 路线里节拍最大的工序（流水线由最慢工序决定产量）
                BottleneckStep = o.Product!.Routes.Where(r => r.Step!.IsActive)
                    .OrderByDescending(r => r.Step!.TaktSeconds).First().Step!.Name,
                BottleneckStepId = o.Product.Routes.Where(r => r.Step!.IsActive)
                    .OrderByDescending(r => r.Step!.TaktSeconds).First().StepId,
                BottleneckTakt = o.Product.Routes.Where(r => r.Step!.IsActive)
                    .Max(r => (int?)r.Step!.TaktSeconds) ?? 60
            })
            .ToListAsync(ct);

        // 瓶颈工序上的并行工位数：只有这道工序开了几个工位，产能才乘几倍
        // （用整条线工位数会把产能算大 10 倍，这正是常见的 APS 拍脑袋错误）
        var stationCounts = await db.Workstations.AsNoTracking()
            .Where(w => w.IsActive)
            .GroupBy(w => new { w.LineId, w.StepId })
            .Select(g => new { g.Key.LineId, g.Key.StepId, Count = g.Count() })
            .ToListAsync(ct);

        // 每条产线一个游标，从当前时间起串行往后排
        var nowLocal = FactoryClock.NowLocal;
        var cursors = orders.Select(o => o.LineId).Distinct().ToDictionary(id => id, _ => nowLocal);
        var rows = new List<ApsScheduleRow>(orders.Count);
        var bottleneckNotes = new List<string>();

        foreach (var o in orders)
        {
            var qty = Math.Max(1, o.PlanQty - o.Done); // 在制单只排剩余量
            var stations = Math.Max(1, stationCounts
                .FirstOrDefault(s => s.LineId == o.LineId && s.StepId == o.BottleneckStepId)?.Count ?? 1);
            var unitsPerHour = 3600d / Math.Max(1, o.BottleneckTakt) * stations * efficiency;
            var loadHours = unitsPerHour > 0 ? qty / unitsPerHour : 0;

            var start = cursors[o.LineId];
            var end = start.AddHours(loadHours);
            cursors[o.LineId] = end;

            var dueLocal = TimeZoneInfo.ConvertTimeFromUtc(o.DueDateUtc, FactoryClock.TimeZone);
            rows.Add(new ApsScheduleRow(
                o.Id, o.OrderNo, o.ProductName, o.LineName, qty, o.Status,
                o.BottleneckStep, o.BottleneckTakt, Math.Round(unitsPerHour, 1),
                start.ToString("yyyy-MM-dd HH:mm"), end.ToString("yyyy-MM-dd HH:mm"),
                dueLocal.ToString("yyyy-MM-dd HH:mm"), end <= dueLocal, Math.Round(loadHours, 1)));
        }

        // 瓶颈提示：跨工单统计哪种工序最常成为瓶颈，给工艺改善排优先级
        foreach (var g in rows.GroupBy(r => r.BottleneckStep).OrderByDescending(g => g.Count()).Take(3))
        {
            bottleneckNotes.Add($"「{g.Key}」在 {g.Count()} 张工单中都是瓶颈，节拍压缩优先投这里");
        }
        var late = rows.Count(r => !r.OnTime);
        if (late > 0) bottleneckNotes.Add($"有 {late} 张工单按当前产能排不到交期内，需插单/加班/外协决策");

        var totalHours = cursors.Values.Count > 0 ? cursors.Values.Max(v => (v - nowLocal).TotalHours) : 0;

        return new ApsResult(
            nowLocal.ToString("yyyy-MM-dd HH:mm"),
            Math.Round(totalHours, 1),
            rows.Count,
            rows,
            bottleneckNotes);
    }
}
