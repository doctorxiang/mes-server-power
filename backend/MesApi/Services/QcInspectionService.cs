using MesApi.Contracts;
using MesApi.Data;
using MesApi.Domain;
using MesApi.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

public interface IQcInspectionService
{
    Task<QcInspectionResult> ListAsync(
        DateTime? fromUtc, DateTime? toUtc, int? lineId, string? productCode, string? orderNo,
        string? result, int limit, CancellationToken ct);

    Task<IReadOnlyList<QcCandidateRow>> CandidatesAsync(
        string? orderNo, int? lineId, string? productCode, int limit, CancellationToken ct);

    Task<QcInspectionRow> CreateAsync(
        CreateQcInspectionRequest request, int userId, string userName, CancellationToken ct);
}

/// <summary>
/// QC 抽检：对象是<b>已包装</b>的成品（过了末道包装工序、状态 Completed）。
///
/// 为什么不复用 sn_move_log：
///   过站流水是"产线上逐台全检"，抽检是"入库前后按批抽几台拆箱复核"，
///   前者推进工序、后者只是留质量证据；混在一张表里会让工单报告的口径全乱。
///
/// 抽检不合格不报废、不隔离 —— 现场做法是<b>加严抽检</b>（加抽一倍）并回溯同批，
/// 所以这里只记录判定与不良，SN 状态保持不变，方便继续追这一台的工序履历。
/// </summary>
public class QcInspectionService(MesDbContext db) : IQcInspectionService
{
    public async Task<QcInspectionResult> ListAsync(
        DateTime? fromUtc, DateTime? toUtc, int? lineId, string? productCode, string? orderNo,
        string? result, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 300);

        var query = db.QcInspections.AsNoTracking()
            .Include(x => x.SerialUnit).ThenInclude(u => u!.WorkOrder).ThenInclude(w => w!.Product)
            .Include(x => x.SerialUnit).ThenInclude(u => u!.WorkOrder).ThenInclude(w => w!.Line)
            .Include(x => x.DefectCode)
            .AsQueryable();

        if (fromUtc.HasValue)
        {
            var from = FactoryClock.StartOfLocalDayUtc(FactoryClock.DateOf(fromUtc.Value));
            query = query.Where(x => x.InspectedAtUtc >= from);
        }
        if (toUtc.HasValue)
        {
            var toExclusive = FactoryClock.StartOfLocalDayUtc(FactoryClock.DateOf(toUtc.Value)).AddDays(1);
            query = query.Where(x => x.InspectedAtUtc < toExclusive);
        }
        if (lineId is int lid)
            query = query.Where(x => x.LineId == lid);
        if (!string.IsNullOrWhiteSpace(productCode))
        {
            var pc = productCode.Trim();
            query = query.Where(x => x.SerialUnit!.WorkOrder!.Product!.Code == pc);
        }
        if (!string.IsNullOrWhiteSpace(orderNo))
        {
            var no = orderNo.Trim();
            query = query.Where(x => x.SerialUnit!.WorkOrder!.OrderNo.Contains(no));
        }
        if (!string.IsNullOrWhiteSpace(result))
        {
            var r = result.Trim();
            query = query.Where(x => x.Result == r);
        }

        var inspected = await query.CountAsync(ct);
        var passed = await query.CountAsync(x => x.Result == "Pass", ct);
        var orders = await query.Select(x => x.WorkOrderId).Distinct().CountAsync(ct);
        var lines = await query.Select(x => x.LineId).Distinct().CountAsync(ct);

        var rows = await query
            .OrderByDescending(x => x.InspectedAtUtc).ThenByDescending(x => x.Id)
            .Take(limit)
            .Select(x => new QcInspectionRow(
                x.Id, x.SerialUnit!.Sn, x.SerialUnit.WorkOrder!.OrderNo,
                x.SerialUnit.WorkOrder.Product!.Name, x.SerialUnit.WorkOrder.Line!.Name,
                x.Category, x.Result,
                x.DefectCode != null ? x.DefectCode.Code : null,
                x.DefectCode != null ? x.DefectCode.Name : null,
                x.InspectorName, x.InspectedAtUtc, x.Remark))
            .ToListAsync(ct);

        var summary = new QcSummary(inspected, passed, inspected - passed,
            inspected == 0 ? 0 : Math.Round(passed * 100d / inspected, 1), orders, lines);

        return new QcInspectionResult(summary, rows);
    }

    /// <summary>
    /// 待抽检候选：已包装完工的 SN，按完工时间倒序。
    /// 「还没抽过」排在前面，抽过的也留在列表里但带次数 —— 质检员往往要抽刚下线的那批。
    /// </summary>
    public async Task<IReadOnlyList<QcCandidateRow>> CandidatesAsync(
        string? orderNo, int? lineId, string? productCode, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);

        var query = db.Units.AsNoTracking()
            .Where(u => u.Status == nameof(UnitStatus.Completed));

        if (!string.IsNullOrWhiteSpace(orderNo))
        {
            var no = orderNo.Trim();
            query = query.Where(u => u.WorkOrder!.OrderNo.Contains(no));
        }
        if (lineId is int lid)
            query = query.Where(u => u.LineId == lid);
        if (!string.IsNullOrWhiteSpace(productCode))
        {
            var pc = productCode.Trim();
            query = query.Where(u => u.WorkOrder!.Product!.Code == pc);
        }

        var units = await query
            .OrderByDescending(u => u.CompletedAtUtc).ThenByDescending(u => u.Id)
            .Take(limit)
            .Select(u => new
            {
                u.Id,
                u.Sn,
                u.CompletedAtUtc,
                OrderNo = u.WorkOrder!.OrderNo,
                ProductName = u.WorkOrder.Product!.Name,
                LineName = u.WorkOrder.Line!.Name
            })
            .ToListAsync(ct);

        if (units.Count == 0) return [];

        var ids = units.Select(u => u.Id).ToList();
        var counts = await db.QcInspections.AsNoTracking()
            .Where(x => ids.Contains(x.SerialUnitId))
            .GroupBy(x => x.SerialUnitId)
            .Select(g => new { SerialUnitId = g.Key, Qty = g.Count() })
            .ToDictionaryAsync(x => x.SerialUnitId, x => x.Qty, ct);

        return units.Select(u => new QcCandidateRow(
            u.Id, u.Sn, u.OrderNo, u.ProductName, u.LineName,
            u.CompletedAtUtc, counts.GetValueOrDefault(u.Id))).ToList();
    }

    public async Task<QcInspectionRow> CreateAsync(
        CreateQcInspectionRequest request, int userId, string userName, CancellationToken ct)
    {
        var sn = request.Sn.Trim().ToUpperInvariant();
        if (sn.Length is < 4 or > 64)
            throw new AppException("SN 长度不合法（4-64 位）", StatusCodes.Status400BadRequest);

        if (!QcCategories.All.Contains(request.Category))
            throw new AppException($"抽检类别只能是 {string.Join(" / ", QcCategories.All)}", StatusCodes.Status400BadRequest);

        var result = request.Result.Trim();
        if (result != "Pass" && result != "Fail")
            throw new AppException("判定结果只能是 Pass / Fail", StatusCodes.Status400BadRequest);

        var unit = await db.Units
            .Include(u => u.WorkOrder).ThenInclude(w => w!.Product)
            .Include(u => u.WorkOrder).ThenInclude(w => w!.Line)
            .FirstOrDefaultAsync(u => u.Sn == sn, ct)
            ?? throw new AppException($"未找到 SN {sn}", StatusCodes.Status404NotFound);

        if (unit.Status == nameof(UnitStatus.Scrapped))
            throw new AppException($"SN {sn} 已报废，不能作为成品抽检", StatusCodes.Status409Conflict);

        if (unit.Status != nameof(UnitStatus.Completed))
            throw new AppException($"SN {sn} 尚未完工（当前 {unit.Status}），只有已包装成品才能抽检",
                StatusCodes.Status409Conflict);

        // 包装是末道工序：完工却没停在包装站，说明工艺路线被改过，这种要人工确认
        var lastStepKind = await db.Steps.AsNoTracking()
            .Where(s => s.Id == unit.CurrentStepId)
            .Select(s => s.Kind)
            .FirstOrDefaultAsync(ct);
        if (lastStepKind is not null && lastStepKind != StepKinds.Packing)
            throw new AppException($"SN {sn} 最后一道不是包装工序，未包装成品不能抽检", StatusCodes.Status409Conflict);

        DefectCode? defect = null;
        if (result == "Fail")
        {
            if (string.IsNullOrWhiteSpace(request.DefectCode))
                throw new AppException("抽检不合格时必须选择不良代码", StatusCodes.Status400BadRequest);

            var code = request.DefectCode.Trim();
            defect = await db.DefectCodes.AsNoTracking().FirstOrDefaultAsync(d => d.Code == code, ct)
                     ?? throw new AppException($"不良代码 {code} 不存在", StatusCodes.Status400BadRequest);
        }

        var now = DateTime.UtcNow;
        var entity = new QcInspection
        {
            SerialUnitId = unit.Id,
            WorkOrderId = unit.WorkOrderId,
            LineId = unit.LineId,
            ProductId = unit.ProductId,
            Category = request.Category,
            Result = result,
            DefectCodeId = defect?.Id,
            Remark = string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim(),
            InspectorId = userId,
            InspectorName = userName,
            ProductionDate = FactoryClock.DateOf(now),
            InspectedAtUtc = now
        };

        db.QcInspections.Add(entity);
        await db.SaveChangesAsync(ct);

        return new QcInspectionRow(entity.Id, unit.Sn, unit.WorkOrder!.OrderNo,
            unit.WorkOrder.Product!.Name, unit.WorkOrder.Line!.Name, entity.Category, entity.Result,
            defect?.Code, defect?.Name, entity.InspectorName, entity.InspectedAtUtc, entity.Remark);
    }
}
