using MesApi.Contracts;
using MesApi.Data;
using MesApi.Domain;
using MesApi.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

public interface ITraceService
{
    Task<TraceResponse> TraceAsync(string sn, CancellationToken ct);
}

public class TraceService(MesDbContext db) : ITraceService
{
    public async Task<TraceResponse> TraceAsync(string snInput, CancellationToken ct)
    {
        var sn = (snInput ?? string.Empty).Trim().ToUpperInvariant();
        if (sn.Length == 0)
            throw new AppException("请输入 SN", StatusCodes.Status400BadRequest);

        var unit = await db.Units.AsNoTracking()
            .Include(u => u.WorkOrder).ThenInclude(w => w!.Product)
            .Include(u => u.WorkOrder).ThenInclude(w => w!.Line)
            .FirstOrDefaultAsync(u => u.Sn == sn, ct)
            ?? throw new AppException($"未找到 SN {sn} 的生产记录", StatusCodes.Status404NotFound);

        var route = await db.ProductRoutes.AsNoTracking()
            .Where(r => r.ProductId == unit.ProductId)
            .Include(r => r.Step)
            .OrderBy(r => r.Seq)
            .ToListAsync(ct);

        var logs = await db.MoveLogs.AsNoTracking()
            .Where(x => x.SerialUnitId == unit.Id)
            .Include(x => x.Step)
            .Include(x => x.DefectCode)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        var steps = route.Select(r =>
        {
            var stepLogs = logs.Where(l => l.StepId == r.StepId).OrderBy(l => l.Id).ToList();
            var pass = stepLogs.LastOrDefault(l => l.Result == "Pass");
            var fail = stepLogs.LastOrDefault(l => l.Result == "Fail");
            var latest = pass ?? fail;

            return new TraceStepDto(
                r.Seq, r.Step!.Code, r.Step.Name, r.StepId,
                latest?.Result,
                fail?.DefectCode?.Name,
                latest?.OperatorName,
                latest?.StationCode,
                latest?.CreatedAtUtc,
                stepLogs.Sum(l => l.DurationSeconds),
                latest?.Remark);
        }).ToList();

        var currentStep = route.FirstOrDefault(r => r.StepId == unit.CurrentStepId);

        // 维修记录：重复维修历史（第几次、哪道工序、什么不良、谁修的、修多久）
        var repairs = await db.RepairRecords.AsNoTracking()
            .Where(r => r.SerialUnitId == unit.Id)
            .Include(r => r.SourceStep)
            .Include(r => r.DefectCode)
            .OrderBy(r => r.RepairNo)
            .Select(r => new RepairRecordRow(
                r.Id, sn, r.RepairNo, r.SourceStep!.Name, r.DefectCode != null ? r.DefectCode.Name : null,
                r.Action, r.Status, r.OperatorName, r.StationCode,
                r.CheckoutByName, r.StartedAtUtc, r.CompletedAtUtc, r.Remark))
            .ToListAsync(ct);

        return new TraceResponse(
            unit.Sn,
            unit.WorkOrder!.OrderNo,
            unit.WorkOrder.Product!.Code,
            unit.WorkOrder.Product.Name,
            unit.WorkOrder.Line!.Name,
            unit.Status,
            unit.Status == nameof(UnitStatus.Completed) ? "已完工" : currentStep?.Step?.Name ?? "未上线",
            unit.CreatedAtUtc,
            unit.CompletedAtUtc,
            logs.Count(l => l.Result == "Pass"),
            logs.Count(l => l.Result == "Fail"),
            steps,
            repairs);
    }
}
