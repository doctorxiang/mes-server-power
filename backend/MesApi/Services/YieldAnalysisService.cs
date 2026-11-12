using MesApi.Data;
using MesApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

// ---------------- 契约 ----------------

public record YieldStepRow(string StepName, int PassQty, int FailQty, double YieldPercent);

public record YieldDefectRow(string Code, string Name, int Count, double CumulativePercent);

public record YieldRow(
    string Key,
    string DisplayName,
    string? OrderStatus,
    int InputQty,
    int CompletedQty,
    int HoldQty,
    int FailQty,
    double YieldPercent,
    double FirstPassYield,
    IReadOnlyList<YieldStepRow> Steps,
    IReadOnlyList<YieldDefectRow> TopDefects);

public record YieldResult(
    string By,
    IReadOnlyList<YieldRow> Rows);

public interface IYieldAnalysisService
{
    /// <summary>by=order 按工单 / by=product 按机种；可按机种编码、工单号进一步收窄。</summary>
    Task<YieldResult> QueryAsync(
        string by, DateTime? fromUtc, DateTime? toUtc, string? productCode, string? orderNo, CancellationToken ct);
}

/// <summary>
/// 良率/不良率分析：数据全部来自 sn_move_log（只追加的过站流水）+ serial_unit。
/// 两个口径：
/// - 直通率 Yield = 完工数 ÷ 投入数（含返修后完工）
/// - 一次通过率 FPY = 无任何不良记录且完工的 SN ÷ 投入数（衡量工艺真实水平）
/// 过滤条件全部下推 SQL，机种维度按 work_order.product 分组。
/// </summary>
public class YieldAnalysisService(MesDbContext db) : IYieldAnalysisService
{
    public async Task<YieldResult> QueryAsync(
        string by, DateTime? fromUtc, DateTime? toUtc, string? productCode, string? orderNo, CancellationToken ct)
    {
        by = by == "product" ? "product" : "order";

        var unitsQuery = db.Units.AsNoTracking().AsQueryable();
        var logsQuery = db.MoveLogs.AsNoTracking().AsQueryable();

        // 机种 / 工单收窄：工单报告页要先选机种再看该机种下每一单的表现
        if (!string.IsNullOrWhiteSpace(productCode))
        {
            var pc = productCode.Trim();
            unitsQuery = unitsQuery.Where(u => u.WorkOrder!.Product!.Code == pc);
            logsQuery = logsQuery.Where(l => l.SerialUnit!.WorkOrder!.Product!.Code == pc);
        }
        if (!string.IsNullOrWhiteSpace(orderNo))
        {
            var no = orderNo.Trim();
            unitsQuery = unitsQuery.Where(u => u.WorkOrder!.OrderNo.Contains(no));
            logsQuery = logsQuery.Where(l => l.SerialUnit!.WorkOrder!.OrderNo.Contains(no));
        }

        if (fromUtc.HasValue)
        {
            var from = FactoryClock.DateOf(fromUtc.Value);
            unitsQuery = unitsQuery.Where(u => string.Compare(u.ProductionDate, from) >= 0);
            logsQuery = logsQuery.Where(l => string.Compare(l.ProductionDate, from) >= 0);
        }
        if (toUtc.HasValue)
        {
            var to = FactoryClock.DateOf(toUtc.Value);
            unitsQuery = unitsQuery.Where(u => string.Compare(u.ProductionDate, to) <= 0);
            logsQuery = logsQuery.Where(l => string.Compare(l.ProductionDate, to) <= 0);
        }

        // 先在数据库里聚合单元指标（工单维度 / 机种维度两条 SQL）
        var unitRows = by == "order"
            ? await unitsQuery.GroupBy(u => new { Key = u.WorkOrder!.OrderNo, u.WorkOrder.Product!.Name, u.WorkOrder.Status })
                .Select(g => new
                {
                    g.Key.Key,
                    g.Key.Name,
                    Status = (string?)g.Key.Status,
                    Input = g.Count(),
                    Completed = g.Count(u => u.Status == nameof(UnitStatus.Completed)),
                    Hold = g.Count(u => u.Status == nameof(UnitStatus.Hold))
                })
                .ToListAsync(ct)
            : await unitsQuery.GroupBy(u => new { Key = u.WorkOrder!.Product!.Code, u.WorkOrder.Product!.Name })
                .Select(g => new
                {
                    g.Key.Key,
                    g.Key.Name,
                    Status = (string?)null,
                    Input = g.Count(),
                    Completed = g.Count(u => u.Status == nameof(UnitStatus.Completed)),
                    Hold = g.Count(u => u.Status == nameof(UnitStatus.Hold))
                })
                .ToListAsync(ct);

        // FPY：完工且从未判过不良
        var failedUnitIds = logsQuery.Where(l => l.Result == "Fail").Select(l => l.SerialUnitId).Distinct();
        var fpyRows = by == "order"
            ? await unitsQuery.Where(u => u.Status == nameof(UnitStatus.Completed) && !failedUnitIds.Contains(u.Id))
                .GroupBy(u => u.WorkOrder!.OrderNo)
                .Select(g => new { Key = g.Key, Fpy = g.Count() })
                .ToListAsync(ct)
            : await unitsQuery.Where(u => u.Status == nameof(UnitStatus.Completed) && !failedUnitIds.Contains(u.Id))
                .GroupBy(u => u.WorkOrder!.Product!.Code)
                .Select(g => new { Key = g.Key, Fpy = g.Count() })
                .ToListAsync(ct);

        // 不良次数 + TOP 不良代码
        var failRows = by == "order"
            ? await logsQuery.Where(l => l.Result == "Fail")
                .GroupBy(l => l.SerialUnit!.WorkOrder!.OrderNo)
                .Select(g => new { Key = g.Key, Count = g.Count() })
                .ToListAsync(ct)
            : await logsQuery.Where(l => l.Result == "Fail")
                .GroupBy(l => l.SerialUnit!.WorkOrder!.Product!.Code)
                .Select(g => new { Key = g.Key, Count = g.Count() })
                .ToListAsync(ct);

        // 工序良率
        var stepRows = by == "order"
            ? await logsQuery.GroupBy(l => new { Key = l.SerialUnit!.WorkOrder!.OrderNo, l.Step!.Name })
                .Select(g => new
                {
                    g.Key.Key,
                    g.Key.Name,
                    Fail = g.Count(x => x.Result == "Fail"),
                    Total = g.Count()
                })
                .ToListAsync(ct)
            : await logsQuery.GroupBy(l => new { Key = l.SerialUnit!.WorkOrder!.Product!.Code, l.Step!.Name })
                .Select(g => new
                {
                    g.Key.Key,
                    g.Key.Name,
                    Fail = g.Count(x => x.Result == "Fail"),
                    Total = g.Count()
                })
                .ToListAsync(ct);

        // TOP 不良代码（每单/机种前 5）
        var defectRows = by == "order"
            ? await logsQuery.Where(l => l.Result == "Fail" && l.DefectCodeId != null)
                .GroupBy(l => new { Key = l.SerialUnit!.WorkOrder!.OrderNo, l.DefectCode!.Code, l.DefectCode.Name })
                .Select(g => new { g.Key.Key, g.Key.Code, g.Key.Name, Count = g.Count() })
                .ToListAsync(ct)
            : await logsQuery.Where(l => l.Result == "Fail" && l.DefectCodeId != null)
                .GroupBy(l => new { Key = l.SerialUnit!.WorkOrder!.Product!.Code, l.DefectCode!.Code, l.DefectCode.Name })
                .Select(g => new { g.Key.Key, g.Key.Code, g.Key.Name, Count = g.Count() })
                .ToListAsync(ct);

        var fpyDict = fpyRows.ToDictionary(x => x.Key, x => x.Fpy);
        var failDict = failRows.ToDictionary(x => x.Key, x => x.Count);

        var rows = new List<YieldRow>(unitRows.Count);
        foreach (var u in unitRows)
        {
            var yieldPct = u.Input == 0 ? 0 : Math.Round(u.Completed * 100d / u.Input, 1);
            var fpy = u.Input == 0 ? 0 : Math.Round(fpyDict.GetValueOrDefault(u.Key) * 100d / u.Input, 1);

            var steps = stepRows.Where(s => s.Key == u.Key)
                .Select(s => new YieldStepRow(s.Name, s.Total - s.Fail, s.Fail,
                    s.Total == 0 ? 100 : Math.Round((s.Total - s.Fail) * 100d / s.Total, 1)))
                .OrderBy(s => s.StepName)
                .ToList();

            // 柏拉图：按不良次数降序 + 累计占比
            var defects = defectRows.Where(d => d.Key == u.Key).OrderByDescending(d => d.Count).ToList();
            var totalFail = defects.Sum(d => d.Count);
            var cum = 0;
            var topDefects = defects.Take(5)
                .Select(d =>
                {
                    cum += d.Count;
                    return new YieldDefectRow(d.Code, d.Name, d.Count,
                        totalFail == 0 ? 0 : Math.Round(cum * 100d / totalFail, 1));
                })
                .ToList();

            rows.Add(new YieldRow(u.Key, u.Name, u.Status, u.Input, u.Completed, u.Hold,
                failDict.GetValueOrDefault(u.Key), yieldPct, fpy, steps, topDefects));
        }

        return new YieldResult(by, rows);
    }
}
