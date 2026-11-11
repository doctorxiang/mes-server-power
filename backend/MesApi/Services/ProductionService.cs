using MesApi.Contracts;
using MesApi.Data;
using MesApi.Domain;
using MesApi.Hubs;
using MesApi.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MesApi.Services;

public interface IProductionService
{
    Task<ScanResponse> ScanAsync(ScanRequest request, int operatorId, string operatorName, CancellationToken ct);
    Task<IReadOnlyList<RecentLogRow>> RecentAsync(string? stepCode, int limit, CancellationToken ct);
    Task<UnitActionResponse> HandleHoldAsync(string sn, HandleHoldRequest request, int operatorId, string operatorName, CancellationToken ct);
    Task<IReadOnlyList<RepairPendingRow>> RepairPendingAsync(int limit, CancellationToken ct);
    Task<RepairRecordRow> RepairCheckinAsync(RepairCheckinRequest request, int operatorId, string operatorName, CancellationToken ct);
    Task<UnitActionResponse> RepairCheckoutAsync(RepairCheckoutRequest request, int operatorId, string operatorName, CancellationToken ct);
}

/// <summary>
/// 扫码过站。设计要点：
/// 1) 一台一码，首站扫码自动建 SN；2) 严格按工艺路线顺序推进，禁止跳站；
/// 3) 客户端幂等键 + 唯一索引，断网重试不重复计数；4) 乐观并发，避免同 SN 被两个工位同时推进。
/// </summary>
public class ProductionService(
    MesDbContext db,
    IMemoryCache cache,
    IBoardNotifier notifier,
    ILogger<ProductionService> logger) : IProductionService
{
    private sealed record RouteStep(int Seq, int StepId, string Code, string Name, bool RequireDefect, bool IsInspection);

    /// <summary>同一 SN 允许的最大维修次数；超过后进站需管理员 force 授权，否则只能报废。</summary>
    private const int MaxRepairs = 2;

    private async Task<List<RouteStep>> GetRouteAsync(int productId, CancellationToken ct)
    {
        var key = $"route:{productId}";
        if (cache.TryGetValue(key, out List<RouteStep>? cached) && cached is not null)
            return cached;

        var route = await db.ProductRoutes
            .AsNoTracking()
            .Where(r => r.ProductId == productId && r.Step!.IsActive)
            .OrderBy(r => r.Seq)
            .Select(r => new RouteStep(r.Seq, r.StepId, r.Step!.Code, r.Step.Name, r.Step.RequireDefect, r.IsInspection))
            .ToListAsync(ct);

        cache.Set(key, route, TimeSpan.FromMinutes(5));
        return route;
    }

    public async Task<ScanResponse> ScanAsync(ScanRequest request, int operatorId, string operatorName, CancellationToken ct)
    {
        var sn = Normalize(request.Sn);
        var orderNo = Normalize(request.OrderNo);
        var stepCode = Normalize(request.StepCode);
        var stationCode = Normalize(request.StationCode);
        var passed = ParseResult(request.Result);

        if (sn.Length is < 4 or > 64)
            throw new AppException("SN 长度不合法（4-64 位）", StatusCodes.Status400BadRequest);

        var now = DateTime.UtcNow;
        var today = FactoryClock.Today;

        // 1. 幂等：同一终端重复提交（断网重试）直接返回上次结果
        if (!string.IsNullOrWhiteSpace(request.ClientKey))
        {
            var key = request.ClientKey.Trim();
            var existing = await db.MoveLogs.AsNoTracking()
                .Include(x => x.Step)
                .Include(x => x.SerialUnit)
                .FirstOrDefaultAsync(x => x.ClientKey == key, ct);

            if (existing is not null)
            {
                var routeCount = (await GetRouteAsync(existing.SerialUnit!.ProductId, ct)).Count;
                return new ScanResponse(true, true, "该扫码请求已处理（重复提交已忽略）",
                    existing.SerialUnit.Sn, orderNo, existing.Step?.Code ?? stepCode, existing.Step?.Name ?? stepCode,
                    existing.Seq, routeCount, existing.SerialUnit.Status, null, null, 0, 0);
            }
        }

        // 2. 工单
        var order = await db.WorkOrders
            .Include(w => w.Product)
            .Include(w => w.Line)
            .FirstOrDefaultAsync(w => w.OrderNo == orderNo, ct)
            ?? throw new AppException($"工单 {orderNo} 不存在", StatusCodes.Status404NotFound);

        if (order.Status is nameof(WorkOrderStatus.Draft) or nameof(WorkOrderStatus.Closed) or nameof(WorkOrderStatus.Completed))
            throw new AppException($"工单当前状态为 {order.Status}，不允许报工", StatusCodes.Status409Conflict);

        // 3. 工序 + 工位
        var step = await db.Steps.AsNoTracking().FirstOrDefaultAsync(s => s.Code == stepCode, ct)
                   ?? throw new AppException($"工序 {stepCode} 不存在", StatusCodes.Status400BadRequest);

        var station = await db.Workstations.AsNoTracking().FirstOrDefaultAsync(w => w.Code == stationCode, ct);
        if (station is null)
            throw new AppException($"工位 {stationCode} 未登记", StatusCodes.Status400BadRequest);

        if (station.StepId is not null && station.StepId != step.Id)
            throw new AppException($"工位 {station.Name} 未绑定工序「{step.Name}」", StatusCodes.Status400BadRequest);

        if (station.LineId != order.LineId)
            throw new AppException("工位与工单不属于同一产线", StatusCodes.Status400BadRequest);

        // 4. 工艺路线
        var route = await GetRouteAsync(order.ProductId, ct);
        if (route.Count == 0)
            throw new AppException($"产品 {order.Product!.Code} 未配置工艺路线", StatusCodes.Status409Conflict);

        var routeStep = route.FirstOrDefault(r => r.StepId == step.Id)
                        ?? throw new AppException($"产品 {order.Product!.Code} 的工艺路线不包含「{step.Name}」", StatusCodes.Status400BadRequest);

        var totalSteps = route.Count;
        var next = route.FirstOrDefault(r => r.Seq == routeStep.Seq + 1);

        // 5. SN
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Sn == sn, ct);

        if (unit is null)
        {
            if (routeStep.Seq != 1)
                throw new AppException($"该 SN 尚未上线，请先在首道工序「{route[0].Name}」扫码", StatusCodes.Status409Conflict);

            if (!passed)
                throw new AppException("首站上线不能判定不良，请先完成上线", StatusCodes.Status400BadRequest);

            unit = new SerialUnit
            {
                Sn = sn,
                WorkOrderId = order.Id,
                ProductId = order.ProductId,
                LineId = order.LineId,
                CurrentSeq = routeStep.Seq,
                CurrentStepId = step.Id,
                Status = nameof(UnitStatus.InProcess),
                ProductionDate = today,
                Shift = FactoryClock.ShiftOf(now),
                CreatedAtUtc = now,
                LastScanAtUtc = now
            };
            db.Units.Add(unit);
        }
        else
        {
            if (unit.WorkOrderId != order.Id)
                throw new AppException($"SN {sn} 属于其它工单，禁止混线", StatusCodes.Status409Conflict);

            if (unit.Status == nameof(UnitStatus.Completed))
                return Result(false, true, $"该 SN 已完工（{FactoryClock.DateOf(unit.CompletedAtUtc ?? now)}）",
                    sn, orderNo, routeStep, totalSteps, unit, next, null, 0, 0);

            if (unit.Status == nameof(UnitStatus.Hold))
                throw new AppException("该 SN 处于不良隔离状态，请先由班组长处理（返修 / 报废）后重新上线", StatusCodes.Status409Conflict);

            if (unit.Status == nameof(UnitStatus.Scrapped))
                throw new AppException("该 SN 已报废，禁止报工", StatusCodes.Status409Conflict);

            if (routeStep.Seq < unit.CurrentSeq)
                return Result(true, true, $"该 SN 已通过「{step.Name}」，无需重复报工",
                    sn, orderNo, routeStep, totalSteps, unit, next, null, 0, 0);

            if (routeStep.Seq > unit.CurrentSeq)
            {
                var current = route.FirstOrDefault(r => r.Seq == unit.CurrentSeq);
                throw new AppException($"跳站拦截：该 SN 当前应在「{current?.Name ?? "第" + unit.CurrentSeq + "站"}」，请按工艺顺序流转",
                    StatusCodes.Status409Conflict);
            }
        }

        // 6. 不良代码
        DefectCode? defect = null;
        if (!passed)
        {
            if (string.IsNullOrWhiteSpace(request.DefectCode))
                throw new AppException("判定不良时必须选择不良代码", StatusCodes.Status400BadRequest);

            var defectCode = Normalize(request.DefectCode);
            defect = await db.DefectCodes.AsNoTracking().FirstOrDefaultAsync(d => d.Code == defectCode, ct)
                     ?? throw new AppException($"不良代码 {defectCode} 不存在", StatusCodes.Status400BadRequest);
        }
        else if (!passed)
        {
            // 合格时才允许带不良代码：上面已校验不良代码必填
        }

        // 7. 工序耗时：与本 SN 上一站的时间差
        var lastLogAt = await db.MoveLogs
            .Where(x => x.SerialUnitId == unit.Id && x.Result == "Pass")
            .OrderByDescending(x => x.Id)
            .Select(x => (DateTime?)x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        var duration = lastLogAt is null ? 0 : (int)Math.Max(0, (now - lastLogAt.Value).TotalSeconds);

        db.MoveLogs.Add(new SnMoveLog
        {
            SerialUnit = unit,
            WorkOrderId = order.Id,
            LineId = order.LineId,
            StepId = step.Id,
            Seq = routeStep.Seq,
            Result = passed ? "Pass" : "Fail",
            DefectCodeId = defect?.Id,
            Remark = string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim(),
            OperatorId = operatorId,
            OperatorName = operatorName,
            StationCode = stationCode,
            ProductionDate = today,
            ProductionHour = FactoryClock.NowLocal.ToString("HH"),
            Shift = FactoryClock.ShiftOf(now),
            ClientKey = string.IsNullOrWhiteSpace(request.ClientKey) ? null : request.ClientKey.Trim(),
            DurationSeconds = duration,
            CreatedAtUtc = now
        });

        // 8. 推进 SN 状态
        unit.LastScanAtUtc = now;
        unit.Version++;

        if (!passed)
        {
            unit.Status = nameof(UnitStatus.Hold);
        }
        else if (next is null)
        {
            unit.Status = nameof(UnitStatus.Completed);
            unit.CurrentStepId = step.Id;
            unit.CompletedAtUtc = now;
        }
        else
        {
            unit.CurrentSeq = next.Seq;
            unit.CurrentStepId = next.StepId;
        }

        if (order.Status == nameof(WorkOrderStatus.Released))
        {
            order.Status = nameof(WorkOrderStatus.InProduction);
            order.ReleasedAtUtc ??= now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppException("该 SN 正在被其它工位处理，请重新扫描确认", StatusCodes.Status409Conflict);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // 幂等键并发重复（同一请求两个线程同时到达）
            logger.LogInformation(ex, "扫码幂等键并发冲突 sn={Sn} key={Key}", sn, request.ClientKey);
            throw new AppException("该扫码请求已处理，请勿重复提交", StatusCodes.Status409Conflict);
        }

        // 9. 工单完工判定
        if (unit.Status == nameof(UnitStatus.Completed))
            await TryCompleteOrderAsync(order, ct);

        // 10. 看板推送（失败不影响报工）
        try
        {
            await notifier.ScanAsync(new BoardScanEvent(
                sn, order.OrderNo, step.Code, step.Name, passed ? "Pass" : "Fail",
                operatorName, stationCode, now, FactoryClock.Today), ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "看板推送失败（忽略）");
        }

        var (passQty, failQty) = await TodayCountsAsync(order.LineId, step.Id, today, ct);

        return Result(true, false,
            passed
                ? (next is null ? "报工成功，该 SN 全部工序完成 🎉" : $"报工成功，已流转至「{next.Name}」")
                : $"已记录不良（{defect!.Name}），SN 已隔离待处理",
            sn, orderNo, routeStep, totalSteps, unit, next, passed ? null : defect?.Name, passQty, failQty);
    }

    private async Task TryCompleteOrderAsync(WorkOrder order, CancellationToken ct)
    {
        var completed = await db.Units.CountAsync(u => u.WorkOrderId == order.Id && u.Status == nameof(UnitStatus.Completed), ct);
        if (completed >= order.PlanQty && order.Status != nameof(WorkOrderStatus.Completed))
        {
            order.Status = nameof(WorkOrderStatus.Completed);
            order.CompletedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<(int Pass, int Fail)> TodayCountsAsync(int lineId, int stepId, string today, CancellationToken ct)
    {
        var rows = await db.MoveLogs
            .AsNoTracking()
            .Where(x => x.ProductionDate == today && x.LineId == lineId && x.StepId == stepId)
            .GroupBy(x => x.Result)
            .Select(g => new { Result = g.Key, Qty = g.Count() })
            .ToListAsync(ct);

        return (rows.FirstOrDefault(r => r.Result == "Pass")?.Qty ?? 0,
                rows.FirstOrDefault(r => r.Result == "Fail")?.Qty ?? 0);
    }

    public async Task<UnitActionResponse> HandleHoldAsync(string snInput, HandleHoldRequest request, int operatorId, string operatorName, CancellationToken ct)
    {
        var sn = Normalize(snInput);
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();

        var unit = await db.Units.FirstOrDefaultAsync(u => u.Sn == sn, ct)
                   ?? throw new AppException($"未找到 SN {sn}", StatusCodes.Status404NotFound);

        if (unit.Status != nameof(UnitStatus.Hold))
            throw new AppException($"该 SN 当前状态为 {unit.Status}，无需不良处理", StatusCodes.Status409Conflict);

        var now = DateTime.UtcNow;

        switch (action)
        {
            case "rework":
                // 兼容旧入口：若有进行中的维修记录，一并关闭为"修复出站"
                var openRepair = await db.RepairRecords
                    .FirstOrDefaultAsync(r => r.SerialUnitId == unit.Id && r.Status == "InProgress", ct);
                if (openRepair is not null)
                {
                    openRepair.Status = "Repaired";
                    openRepair.CompletedAtUtc = now;
                    openRepair.CheckoutByName = operatorName;
                }
                unit.Status = nameof(UnitStatus.InProcess);
                unit.LastScanAtUtc = now;
                break;

            case "scrap":
                unit.Status = nameof(UnitStatus.Scrapped);
                unit.CompletedAtUtc = null;
                unit.LastScanAtUtc = now;
                db.MoveLogs.Add(new SnMoveLog
                {
                    SerialUnitId = unit.Id,
                    WorkOrderId = unit.WorkOrderId,
                    LineId = unit.LineId,
                    StepId = unit.CurrentStepId ?? 1,
                    Seq = unit.CurrentSeq,
                    Result = "Scrap",
                    Remark = string.IsNullOrWhiteSpace(request.Remark) ? "报废处理" : request.Remark.Trim(),
                    OperatorId = operatorId,
                    OperatorName = operatorName,
                    StationCode = "REWORK",
                    ProductionDate = FactoryClock.Today,
                    ProductionHour = FactoryClock.NowLocal.ToString("HH"),
                    Shift = FactoryClock.ShiftOf(now),
                    CreatedAtUtc = now
                });
                break;

            default:
                throw new AppException("处理方式只能是 rework（返修放行）或 scrap（报废）", StatusCodes.Status400BadRequest);
        }

        await db.SaveChangesAsync(ct);

        return new UnitActionResponse(sn, unit.Status,
            action == "rework" ? "已放行返修，请在原工序重新扫码过站" : "已登记报废");
    }

    // ---------------- 维修站 ----------------

    /// <summary>待维修队列：隔离中的 SN + 最后不良信息 + 历史维修次数/是否维修中。</summary>
    public async Task<IReadOnlyList<RepairPendingRow>> RepairPendingAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);

        var units = await db.Units.AsNoTracking()
            .Where(u => u.Status == nameof(UnitStatus.Hold))
            .OrderBy(u => u.LastScanAtUtc)
            .Take(limit)
            .Select(u => new
            {
                u.Id,
                u.Sn,
                u.CurrentSeq,
                u.LastScanAtUtc,
                OrderNo = u.WorkOrder!.OrderNo,
                ProductName = u.WorkOrder!.Product!.Name,
                LineName = u.WorkOrder.Line!.Name,
                StepName = db.Steps.Where(s => s.Id == u.CurrentStepId).Select(s => s.Name).FirstOrDefault()
            })
            .ToListAsync(ct);

        if (units.Count == 0)
            return Array.Empty<RepairPendingRow>();

        var ids = units.Select(u => u.Id).ToList();

        // 每个 SN 最近一次 Fail 的不良代码
        var lastFails = await db.MoveLogs.AsNoTracking()
            .Where(x => ids.Contains(x.SerialUnitId) && x.Result == "Fail" && x.DefectCodeId != null)
            .OrderByDescending(x => x.Id)
            .Select(x => new { x.SerialUnitId, Code = x.DefectCode!.Code, Name = x.DefectCode.Name })
            .ToListAsync(ct);
        var failDict = lastFails
            .GroupBy(x => x.SerialUnitId)
            .ToDictionary(g => g.Key, g => g.First());

        // 历史维修次数 + 是否有进行中的维修
        var repairStats = await db.RepairRecords.AsNoTracking()
            .Where(r => ids.Contains(r.SerialUnitId))
            .GroupBy(r => r.SerialUnitId)
            .Select(g => new
            {
                SerialUnitId = g.Key,
                Count = g.Count(),
                Repairing = g.Any(x => x.Status == "InProgress")
            })
            .ToDictionaryAsync(x => x.SerialUnitId, ct);

        return units.Select(u =>
        {
            repairStats.TryGetValue(u.Id, out var stat);
            failDict.TryGetValue(u.Id, out var fail);
            return new RepairPendingRow(
                u.Id, u.Sn, u.OrderNo, u.ProductName, u.LineName,
                u.StepName ?? $"第 {u.CurrentSeq} 站", u.CurrentSeq,
                fail?.Code, fail?.Name,
                stat?.Count ?? 0, stat?.Repairing ?? false, u.LastScanAtUtc);
        }).ToList();
    }

    /// <summary>进站：确认维修，开 InProgress 记录。重复维修受 MaxRepairs 限制，管理员可 force。</summary>
    public async Task<RepairRecordRow> RepairCheckinAsync(RepairCheckinRequest request, int operatorId, string operatorName, CancellationToken ct)
    {
        var sn = Normalize(request.Sn);
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Sn == sn, ct)
                   ?? throw new AppException($"未找到 SN {sn}", StatusCodes.Status404NotFound);

        if (unit.Status != nameof(UnitStatus.Hold))
            throw new AppException($"SN {sn} 当前状态为 {unit.Status}，只有不良隔离中的 SN 才能进维修站",
                StatusCodes.Status409Conflict);

        // 已有进行中的维修 → 不允许重复进站
        var open = await db.RepairRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.SerialUnitId == unit.Id && r.Status == "InProgress", ct);
        if (open is not null)
            throw new AppException($"SN {sn} 已在维修中（第 {open.RepairNo} 次进站），请先完成出站",
                StatusCodes.Status409Conflict);

        // 重复维修上限：超过 MaxRepairs 次需管理员授权，否则引导报废
        var repairCount = await db.RepairRecords.CountAsync(r => r.SerialUnitId == unit.Id, ct);
        if (repairCount >= MaxRepairs && !request.Force)
            throw new AppException(
                $"SN {sn} 已维修 {repairCount} 次，达到上限 {MaxRepairs} 次。如需再次维修请由管理员授权（force），或直接选择报废出站",
                StatusCodes.Status409Conflict);

        if (unit.CurrentStepId is null)
            throw new AppException("无法定位该 SN 的不良工序，请先检查数据", StatusCodes.Status409Conflict);

        // 不良代码：扫码携带优先，否则取最近一次 Fail 的代码
        DefectCode? defect = null;
        if (!string.IsNullOrWhiteSpace(request.DefectCode))
        {
            var code = Normalize(request.DefectCode);
            defect = await db.DefectCodes.AsNoTracking().FirstOrDefaultAsync(d => d.Code == code, ct)
                     ?? throw new AppException($"不良代码 {code} 不存在", StatusCodes.Status400BadRequest);
        }
        else
        {
            var lastFailCodeId = await db.MoveLogs.AsNoTracking()
                .Where(x => x.SerialUnitId == unit.Id && x.Result == "Fail" && x.DefectCodeId != null)
                .OrderByDescending(x => x.Id)
                .Select(x => x.DefectCodeId)
                .FirstOrDefaultAsync(ct);
            if (lastFailCodeId is int did)
                defect = await db.DefectCodes.AsNoTracking().FirstOrDefaultAsync(d => d.Id == did, ct);
        }

        var now = DateTime.UtcNow;
        var record = new RepairRecord
        {
            SerialUnitId = unit.Id,
            WorkOrderId = unit.WorkOrderId,
            RepairNo = repairCount + 1,
            SourceStepId = unit.CurrentStepId.Value,
            DefectCodeId = defect?.Id,
            Action = ParseRepairAction(request.Action),
            Status = "InProgress",
            Remark = string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim(),
            OperatorId = operatorId,
            OperatorName = operatorName,
            StationCode = string.IsNullOrWhiteSpace(request.StationCode) ? null : Normalize(request.StationCode),
            StartedAtUtc = now
        };
        db.RepairRecords.Add(record);
        unit.LastScanAtUtc = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // (SN, RepairNo) 唯一索引：并发双击"进站"时兜底
            throw new AppException("该 SN 已进站，请勿重复提交", StatusCodes.Status409Conflict);
        }

        var sourceStep = await db.Steps.AsNoTracking().FirstAsync(s => s.Id == record.SourceStepId, ct);

        return new RepairRecordRow(record.Id, sn, record.RepairNo, sourceStep.Name, defect?.Name,
            record.Action, record.Status, operatorName, record.StationCode,
            null, record.StartedAtUtc, null, record.Remark);
    }

    /// <summary>
    /// 出站：
    /// - repaired：修复完成 → SN 放回「原判不良工序」重新过站（CurrentSeq 不变，重扫即重测）；
    /// - scrap：报废 → 终态，写 Scrap 流水。
    /// </summary>
    public async Task<UnitActionResponse> RepairCheckoutAsync(RepairCheckoutRequest request, int operatorId, string operatorName, CancellationToken ct)
    {
        var sn = Normalize(request.Sn);
        var outcome = (request.Outcome ?? string.Empty).Trim().ToLowerInvariant();
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Sn == sn, ct)
                   ?? throw new AppException($"未找到 SN {sn}", StatusCodes.Status404NotFound);

        var record = await db.RepairRecords
            .Include(r => r.SourceStep)
            .FirstOrDefaultAsync(r => r.SerialUnitId == unit.Id && r.Status == "InProgress", ct)
            ?? throw new AppException($"SN {sn} 没有进行中的维修记录，请先在维修站进站",
                StatusCodes.Status409Conflict);

        if (unit.Status != nameof(UnitStatus.Hold))
            throw new AppException($"SN {sn} 当前状态为 {unit.Status}，出站状态异常，请联系管理员核查",
                StatusCodes.Status409Conflict);

        var now = DateTime.UtcNow;
        var remark = string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim();
        if (!string.IsNullOrWhiteSpace(remark))
            record.Remark = string.IsNullOrWhiteSpace(record.Remark) ? remark : $"{record.Remark}；{remark}";

        record.CheckoutByName = operatorName;
        record.CompletedAtUtc = now;

        switch (outcome)
        {
            case "repaired":
            case "ok":
                record.Status = "Repaired";
                unit.Status = nameof(UnitStatus.InProcess);
                unit.LastScanAtUtc = now;
                // CurrentSeq 保持为原判不良工序：出站后在该工序重新扫码即重测
                break;

            case "scrap":
                record.Status = "Scrapped";
                unit.Status = nameof(UnitStatus.Scrapped);
                unit.CompletedAtUtc = null;
                unit.LastScanAtUtc = now;

                db.MoveLogs.Add(new SnMoveLog
                {
                    SerialUnitId = unit.Id,
                    WorkOrderId = unit.WorkOrderId,
                    LineId = unit.LineId,
                    StepId = record.SourceStepId,
                    Seq = unit.CurrentSeq,
                    Result = "Scrap",
                    Remark = remark ?? "维修站报废",
                    OperatorId = operatorId,
                    OperatorName = operatorName,
                    StationCode = string.IsNullOrWhiteSpace(request.StationCode) ? "REWORK" : Normalize(request.StationCode),
                    ProductionDate = FactoryClock.Today,
                    ProductionHour = FactoryClock.NowLocal.ToString("HH"),
                    Shift = FactoryClock.ShiftOf(now),
                    CreatedAtUtc = now
                });
                break;

            default:
                throw new AppException("出站方式只能是 repaired（修复完成）或 scrap（报废）",
                    StatusCodes.Status400BadRequest);
        }

        await db.SaveChangesAsync(ct);

        var message = record.Status == "Repaired"
            ? $"维修完成（第 {record.RepairNo} 次），SN 已放回「{record.SourceStep?.Name}」工序重测"
            : $"已从维修站报废（第 {record.RepairNo} 次维修后）";

        return new UnitActionResponse(sn, unit.Status, message);
    }

    private static string ParseRepairAction(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "replace" or "换料" or "换件" => "Replace",
            "resolder" or "补焊" or "重焊" => "Resolder",
            "adjust" or "调整" or "重插" => "Adjust",
            "other" or "其它" or "其他" => "Other",
            _ => throw new AppException("维修措施只能是 Replace / Resolder / Adjust / Other", StatusCodes.Status400BadRequest)
        };
    }

    public async Task<IReadOnlyList<RecentLogRow>> RecentAsync(string? stepCode, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 50);
        var query = db.MoveLogs.AsNoTracking().Include(x => x.Step).Include(x => x.DefectCode).AsQueryable();

        if (!string.IsNullOrWhiteSpace(stepCode))
        {
            var code = Normalize(stepCode);
            query = query.Where(x => x.Step!.Code == code);
        }

        return await query
            .OrderByDescending(x => x.Id)
            .Take(limit)
            .Select(x => new RecentLogRow(
                x.Id,
                x.SerialUnit!.Sn,
                x.Step!.Name,
                x.Result,
                x.DefectCode != null ? x.DefectCode.Name : null,
                x.OperatorName,
                x.StationCode,
                x.CreatedAtUtc))
            .ToListAsync(ct);
    }

    private static ScanResponse Result(
        bool accepted, bool duplicated, string message, string sn, string orderNo,
        RouteStep routeStep, int totalSteps, SerialUnit unit, RouteStep? next,
        string? defectName, int passQty, int failQty)
        => new(accepted, duplicated, message, sn, orderNo, routeStep.Code, routeStep.Name,
            routeStep.Seq, totalSteps, unit.Status, next?.Code, next?.Name, passQty, failQty);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static bool ParseResult(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "pass" or "ok" or "good" or "true" or "1" or "合格" => true,
            "fail" or "ng" or "bad" or "false" or "0" or "不良" => false,
            _ => throw new AppException("判定结果只能是 Pass 或 Fail", StatusCodes.Status400BadRequest)
        };
    }

    private static bool IsDuplicateKey(DbUpdateException ex)
        => ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true
           || ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true;
}
