using MesApi.Contracts;
using MesApi.Data;
using MesApi.Domain;
using MesApi.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

public interface IWorkOrderService
{
    /// <summary>工单列表。status 支持单值，也支持 active（进行中）/ done（已完工+已关单）两个分组。</summary>
    Task<IReadOnlyList<WorkOrderListItem>> ListAsync(
        string? status, int? lineId, string? keyword, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct);
    Task<WorkOrderDetail> GetAsync(int id, CancellationToken ct);
    Task<WorkOrderDetail> CreateAsync(CreateWorkOrderRequest request, int userId, CancellationToken ct);
    Task<WorkOrderListItem> ChangeStatusAsync(int id, string action, CancellationToken ct);
    Task<WorkOrderListItem> GetByNoAsync(string orderNo, CancellationToken ct);

    /// <summary>补料：报废把冗余料吃穿后追加发料，返回更新后的工单行。</summary>
    Task<WorkOrderListItem> ReplenishAsync(int id, int qty, string? reason, CancellationToken ct);
}

public class WorkOrderService(MesDbContext db) : IWorkOrderService
{
    public async Task<IReadOnlyList<WorkOrderListItem>> ListAsync(
        string? status, int? lineId, string? keyword, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        var query = db.WorkOrders.AsNoTracking()
            .Include(w => w.Product).Include(w => w.Line)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim();
            query = s switch
            {
                // 列表页"进行中"看的是还没完工的单：已下发 + 生产中
                "active" => query.Where(w => w.Status == nameof(WorkOrderStatus.Released)
                                          || w.Status == nameof(WorkOrderStatus.InProduction)),
                "done" => query.Where(w => w.Status == nameof(WorkOrderStatus.Completed)
                                        || w.Status == nameof(WorkOrderStatus.Closed)),
                _ => query.Where(w => w.Status == s)
            };
        }

        if (lineId is not null)
            query = query.Where(w => w.LineId == lineId);

        // 日期按"计划开工"过滤（东八区自然日），计划员排产时最常按这一列找单
        if (fromUtc.HasValue)
        {
            var from = FactoryClock.StartOfLocalDayUtc(FactoryClock.DateOf(fromUtc.Value));
            query = query.Where(w => w.PlannedStartUtc >= from);
        }
        if (toUtc.HasValue)
        {
            var day = DateTime.SpecifyKind(DateTime.Parse(FactoryClock.DateOf(toUtc.Value)), DateTimeKind.Unspecified)
                .AddDays(1);
            var toExclusive = TimeZoneInfo.ConvertTimeToUtc(day, FactoryClock.TimeZone);
            query = query.Where(w => w.PlannedStartUtc < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            query = query.Where(w => w.OrderNo.Contains(kw) || w.Product!.Code.Contains(kw) || w.Product.Name.Contains(kw));
        }

        var orders = await query
            .OrderBy(w => w.Priority).ThenByDescending(w => w.Id)
            .Take(200)
            .ToListAsync(ct);

        return await BuildListAsync(orders, ct);
    }

    public async Task<WorkOrderListItem> GetByNoAsync(string orderNo, CancellationToken ct)
    {
        var no = orderNo.Trim();
        var order = await db.WorkOrders.AsNoTracking()
            .Include(w => w.Product).Include(w => w.Line)
            .FirstOrDefaultAsync(w => w.OrderNo == no, ct)
            ?? throw new AppException($"工单 {no} 不存在", StatusCodes.Status404NotFound);

        var list = await BuildListAsync([order], ct);
        return list[0];
    }

    private async Task<List<WorkOrderListItem>> BuildListAsync(List<WorkOrder> orders, CancellationToken ct)
    {
        var ids = orders.Select(o => o.Id).ToList();

        var unitStats = await db.Units.AsNoTracking()
            .Where(u => ids.Contains(u.WorkOrderId))
            .GroupBy(u => new { u.WorkOrderId, u.Status })
            .Select(g => new { g.Key.WorkOrderId, g.Key.Status, Qty = g.Count() })
            .ToListAsync(ct);

        // 返修口径：次数 = 维修记录条数（同一台修两次算两次）；
        // 台数 = 进过维修站的不同 SN 数。修两次救回来仍是良品，但 FPY 已经被扣掉了。
        var repairTimes = await db.RepairRecords.AsNoTracking()
            .Where(r => ids.Contains(r.WorkOrderId))
            .GroupBy(r => r.WorkOrderId)
            .Select(g => new { WorkOrderId = g.Key, Qty = g.Count() })
            .ToListAsync(ct);

        var repairUnits = await db.RepairRecords.AsNoTracking()
            .Where(r => ids.Contains(r.WorkOrderId))
            .Select(r => new { r.WorkOrderId, r.SerialUnitId })
            .Distinct()
            .GroupBy(x => x.WorkOrderId)
            .Select(g => new { WorkOrderId = g.Key, Qty = g.Count() })
            .ToListAsync(ct);

        return orders.Select(o =>
        {
            var scrap = unitStats.Where(s => s.WorkOrderId == o.Id && s.Status == nameof(UnitStatus.Scrapped)).Sum(s => s.Qty);
            var issued = MaterialPlanning.EffectiveIssued(o);

            return new WorkOrderListItem(
                o.Id, o.OrderNo, o.Product!.Code, o.Product.Name, o.LineId, o.Line!.Name,
                o.PlanQty,
                unitStats.Where(s => s.WorkOrderId == o.Id).Sum(s => s.Qty),
                unitStats.Where(s => s.WorkOrderId == o.Id && s.Status == nameof(UnitStatus.Completed)).Sum(s => s.Qty),
                unitStats.Where(s => s.WorkOrderId == o.Id && s.Status == nameof(UnitStatus.Hold)).Sum(s => s.Qty),
                o.Status, o.Priority, o.PlannedStartUtc, o.DueDateUtc, o.CreatedAtUtc,
                issued, scrap, MaterialPlanning.ReplenishNeed(o.PlanQty, issued, scrap),
                repairUnits.Where(r => r.WorkOrderId == o.Id).Sum(r => r.Qty),
                repairTimes.Where(r => r.WorkOrderId == o.Id).Sum(r => r.Qty)
            );
        }).ToList();
    }

    public async Task<WorkOrderDetail> GetAsync(int id, CancellationToken ct)
    {
        var order = await db.WorkOrders.AsNoTracking()
            .Include(w => w.Product).Include(w => w.Line)
            .FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new AppException("工单不存在", StatusCodes.Status404NotFound);

        var list = await BuildListAsync([order], ct);

        var route = await db.ProductRoutes.AsNoTracking()
            .Where(r => r.ProductId == order.ProductId)
            .Include(r => r.Step)
            .OrderBy(r => r.Seq)
            .ToListAsync(ct);

        var logStats = await db.MoveLogs.AsNoTracking()
            .Where(x => x.WorkOrderId == id)
            .GroupBy(x => new { x.StepId, x.Result })
            .Select(g => new { g.Key.StepId, g.Key.Result, Qty = g.Count() })
            .ToListAsync(ct);

        var wipStats = await db.Units.AsNoTracking()
            .Where(u => u.WorkOrderId == id && u.Status == nameof(UnitStatus.InProcess))
            .GroupBy(u => u.CurrentStepId)
            .Select(g => new { StepId = g.Key, Qty = g.Count() })
            .ToListAsync(ct);

        var steps = route.Select(r => new StepProgressDto(
            r.StepId, r.Step!.Code, r.Step.Name, r.Seq, r.IsInspection,
            logStats.Where(s => s.StepId == r.StepId && s.Result == "Pass").Sum(s => s.Qty),
            logStats.Where(s => s.StepId == r.StepId && s.Result == "Fail").Sum(s => s.Qty),
            wipStats.Where(s => s.StepId == r.StepId).Sum(s => s.Qty)
        )).ToList();

        var units = await db.Units.AsNoTracking()
            .Where(u => u.WorkOrderId == id)
            .OrderByDescending(u => u.LastScanAtUtc)
            .Take(50)
            .Select(u => new UnitRow(u.Id, u.Sn, u.CurrentSeq, null,
                u.Status, u.CreatedAtUtc, u.CompletedAtUtc, u.LastScanAtUtc))
            .ToListAsync(ct);

        units = units.Select(u => u with
        {
            CurrentStepName = u.CurrentSeq > 0 && route.Count >= u.CurrentSeq
                ? route[u.CurrentSeq - 1].Step!.Name
                : null
        }).ToList();

        // 直通率 FPY：一次没判过不良就完工的台数 ÷ 投入。
        // 注意别拿"工序合格率"（Pass ÷ 过站次数）冒充它 —— 一道工序判一次不良，
        // 一台机走 9 道工序，工序合格率永远 98%，看着漂亮但毫无意义。
        var failedUnitIds = await db.MoveLogs.AsNoTracking()
            .Where(l => l.WorkOrderId == id && l.Result == "Fail")
            .Select(l => l.SerialUnitId)
            .Distinct()
            .ToListAsync(ct);

        var inputQty = await db.Units.CountAsync(u => u.WorkOrderId == id, ct);
        var firstPass = await db.Units.CountAsync(u => u.WorkOrderId == id
            && u.Status == nameof(UnitStatus.Completed) && !failedUnitIds.Contains(u.Id), ct);
        var fpy = inputQty == 0 ? 0 : (int)Math.Round(firstPass * 100d / inputQty);

        var scrapUnits = await BuildScrapUnitsAsync(id, route, ct);

        return new WorkOrderDetail(list[0], steps, units, fpy, scrapUnits);
    }

    /// <summary>
    /// 报废明细：报废台数通常只有一两台，但必须能查到"哪台、哪个工序、什么不良"，
    /// 否则详情页只有一个数字，工艺和产线都没法复盘。
    /// </summary>
    private async Task<List<ScrapUnitRow>> BuildScrapUnitsAsync(
        int orderId, List<ProductRoute> route, CancellationToken ct)
    {
        var scraps = await db.Units.AsNoTracking()
            .Where(u => u.WorkOrderId == orderId && u.Status == nameof(UnitStatus.Scrapped))
            .OrderBy(u => u.LastScanAtUtc)
            .Select(u => new { u.Id, u.Sn, u.LastScanAtUtc })
            .ToListAsync(ct);

        if (scraps.Count == 0) return [];

        var ids = scraps.Select(s => s.Id).ToList();
        var fails = await db.MoveLogs.AsNoTracking()
            .Where(l => ids.Contains(l.SerialUnitId) && l.Result == "Fail")
            .Select(l => new { l.SerialUnitId, l.Seq, l.DefectCodeId, l.CreatedAtUtc })
            .ToListAsync(ct);

        var defectNames = await db.DefectCodes.AsNoTracking()
            .ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        return scraps.Select(s =>
        {
            var last = fails.Where(f => f.SerialUnitId == s.Id).OrderByDescending(f => f.Seq).FirstOrDefault();
            var stepName = last is not null && last.Seq >= 1 && last.Seq <= route.Count
                ? route[last.Seq - 1].Step!.Name
                : "—";
            var defectName = last?.DefectCodeId is int did && defectNames.TryGetValue(did, out var dn) ? dn : "未记录不良代码";
            return new ScrapUnitRow(
                s.Sn, stepName, defectName, s.LastScanAtUtc,
                last is null ? "报废（缺少不良记录）" : $"{stepName}判{defectName} → 维修站判定不可修，报废出站");
        }).ToList();
    }

    public async Task<WorkOrderDetail> CreateAsync(CreateWorkOrderRequest request, int userId, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == request.ProductId, ct)
                      ?? throw new AppException("产品不存在", StatusCodes.Status400BadRequest);

        var line = await db.Lines.FirstOrDefaultAsync(l => l.Id == request.LineId, ct)
                   ?? throw new AppException("产线不存在", StatusCodes.Status400BadRequest);

        if (!await db.ProductRoutes.AnyAsync(r => r.ProductId == product.Id, ct))
            throw new AppException($"产品 {product.Code} 尚未配置工艺路线，无法建单", StatusCodes.Status400BadRequest);

        if (request.DueDateUtc <= request.PlannedStartUtc)
            throw new AppException("交期必须晚于计划开工时间", StatusCodes.Status400BadRequest);

        var order = new WorkOrder
        {
            OrderNo = await NextOrderNoAsync(ct),
            ProductId = product.Id,
            LineId = line.Id,
            PlanQty = request.PlanQty,
            // 发料 = 计划（不预置冗余）。真报废了再走补料追加，报废率是千分之几，没必要一上来多备 15%
            IssuedQty = MaterialPlanning.IssuedFor(request.PlanQty),
            // 计划员建单即为已下发（简化流程：建单即下发，避免多一步审批）
            Status = nameof(WorkOrderStatus.Released),
            ReleasedAtUtc = DateTime.UtcNow,
            Priority = request.Priority,
            PlannedStartUtc = request.PlannedStartUtc,
            DueDateUtc = request.DueDateUtc,
            Remark = string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim(),
            CreatedByUserId = userId,
            CreatedAtUtc = DateTime.UtcNow
        };

        db.WorkOrders.Add(order);
        await db.SaveChangesAsync(ct);

        return await GetAsync(order.Id, ct);
    }

    public async Task<WorkOrderListItem> ChangeStatusAsync(int id, string action, CancellationToken ct)
    {
        var order = await db.WorkOrders.FirstOrDefaultAsync(w => w.Id == id, ct)
                    ?? throw new AppException("工单不存在", StatusCodes.Status404NotFound);

        var normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "release":
                if (order.Status != nameof(WorkOrderStatus.Draft))
                    throw new AppException("只有草稿工单可以下发", StatusCodes.Status409Conflict);
                order.Status = nameof(WorkOrderStatus.Released);
                order.ReleasedAtUtc = DateTime.UtcNow;
                break;

            case "close":
                if (order.Status == nameof(WorkOrderStatus.Closed))
                    throw new AppException("工单已关单", StatusCodes.Status409Conflict);
                order.Status = nameof(WorkOrderStatus.Closed);
                break;

            case "reopen":
                if (order.Status != nameof(WorkOrderStatus.Completed))
                    throw new AppException("只有已完工工单可以重新打开", StatusCodes.Status409Conflict);
                order.Status = nameof(WorkOrderStatus.InProduction);
                order.CompletedAtUtc = null;
                break;

            default:
                throw new AppException("不支持的操作，可选 release / close / reopen", StatusCodes.Status400BadRequest);
        }

        await db.SaveChangesAsync(ct);
        return (await ListAsync(null, null, order.OrderNo, null, null, ct)).First();
    }

    /// <summary>
    /// 补料：把发料总量往上加，产线才能继续上线新 SN。
    /// 这里不强制"必须已经欠料"——提前补料是正常排产动作，只要工单没关单就允许。
    /// </summary>
    public async Task<WorkOrderListItem> ReplenishAsync(int id, int qty, string? reason, CancellationToken ct)
    {
        if (qty <= 0)
            throw new AppException("补料数量必须大于 0", StatusCodes.Status400BadRequest);

        var order = await db.WorkOrders
            .Include(w => w.Product).Include(w => w.Line)
            .FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new AppException("工单不存在", StatusCodes.Status404NotFound);

        if (order.Status == nameof(WorkOrderStatus.Closed))
            throw new AppException("工单已关单，不能再补料", StatusCodes.Status409Conflict);

        order.IssuedQty = MaterialPlanning.EffectiveIssued(order) + qty;

        var note = $"补料 +{qty}" + (string.IsNullOrWhiteSpace(reason) ? string.Empty : $"（{reason.Trim()}）");
        order.Remark = string.IsNullOrWhiteSpace(order.Remark) ? note : $"{order.Remark}；{note}";
        if (order.Remark.Length > 256)
            order.Remark = order.Remark[^256..];

        await db.SaveChangesAsync(ct);

        var list = await BuildListAsync([order], ct);
        return list[0];
    }

    private async Task<string> NextOrderNoAsync(CancellationToken ct)    {
        var prefix = $"WO{FactoryClock.NowLocal:yyyyMMdd}";
        var last = await db.WorkOrders.AsNoTracking()
            .Where(w => w.OrderNo.StartsWith(prefix))
            .OrderByDescending(w => w.OrderNo)
            .Select(w => w.OrderNo)
            .FirstOrDefaultAsync(ct);

        var seq = 1;
        if (last is not null && int.TryParse(last[prefix.Length..], out var n))
            seq = n + 1;

        return $"{prefix}{seq:000}";
    }
}
