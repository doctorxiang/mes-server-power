using MesApi.Contracts;
using MesApi.Data;
using MesApi.Domain;
using MesApi.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

public interface IExceptionService
{
    Task<IReadOnlyList<ExceptionRow>> ListAsync(string? status, int? lineId, CancellationToken ct);
    Task<ExceptionRow> CreateAsync(CreateExceptionRequest request, int userId, string operatorName, CancellationToken ct);
    Task<ExceptionRow> CloseAsync(int id, CloseExceptionRequest request, string operatorName, CancellationToken ct);
}

public class ExceptionService(MesDbContext db) : IExceptionService
{
    public async Task<IReadOnlyList<ExceptionRow>> ListAsync(string? status, int? lineId, CancellationToken ct)
    {
        var query = db.Exceptions.AsNoTracking()
            .Include(e => e.WorkOrder)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(e => e.Status == status);

        if (lineId is not null)
            query = query.Where(e => e.LineId == lineId);

        var lineNames = await db.Lines.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        var rows = await query.OrderByDescending(e => e.Id).Take(100).ToListAsync(ct);

        return rows.Select(e => new ExceptionRow(
            e.Id, e.Type, e.Description, e.Status,
            lineNames.GetValueOrDefault(e.LineId, "-"),
            e.WorkOrder?.OrderNo, e.StationCode, e.ReportedByName, e.ReportedAtUtc,
            e.HandledByName, e.HandleNote, e.HandledAtUtc
        )).ToList();
    }

    public async Task<ExceptionRow> CreateAsync(CreateExceptionRequest request, int userId, string operatorName, CancellationToken ct)
    {
        if (!new[] { "Material", "Equipment", "Quality", "Other" }.Contains(request.Type))
            throw new AppException("异常类型不合法", StatusCodes.Status400BadRequest);

        var entity = new ExceptionEvent
        {
            Type = request.Type,
            Description = request.Description.Trim(),
            WorkOrderId = request.WorkOrderId,
            LineId = request.LineId,
            StationCode = string.IsNullOrWhiteSpace(request.StationCode) ? null : request.StationCode.Trim(),
            Status = "Open",
            ReportedByUserId = userId,
            ReportedByName = operatorName,
            ReportedAtUtc = DateTime.UtcNow
        };

        db.Exceptions.Add(entity);
        await db.SaveChangesAsync(ct);

        var lineName = await db.Lines.Where(l => l.Id == entity.LineId).Select(l => l.Name).FirstOrDefaultAsync(ct) ?? "-";
        var orderNo = entity.WorkOrderId is null ? null
            : await db.WorkOrders.Where(w => w.Id == entity.WorkOrderId).Select(w => w.OrderNo).FirstOrDefaultAsync(ct);

        return new ExceptionRow(entity.Id, entity.Type, entity.Description, entity.Status, lineName,
            orderNo, entity.StationCode, entity.ReportedByName, entity.ReportedAtUtc, null, null, null);
    }

    public async Task<ExceptionRow> CloseAsync(int id, CloseExceptionRequest request, string operatorName, CancellationToken ct)
    {
        var entity = await db.Exceptions.FirstOrDefaultAsync(e => e.Id == id, ct)
                     ?? throw new AppException("异常记录不存在", StatusCodes.Status404NotFound);

        if (entity.Status == "Closed")
            throw new AppException("该异常已关闭", StatusCodes.Status409Conflict);

        entity.Status = "Closed";
        entity.HandledByName = operatorName;
        entity.HandleNote = request.HandleNote.Trim();
        entity.HandledAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        var row = await ListAsync(null, entity.LineId, ct);
        return row.First(x => x.Id == id);
    }
}
