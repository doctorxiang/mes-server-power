using MesApi.Data;
using MesApi.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Controllers;

/// <summary>
/// 审计日志查询（仅管理员）。客户审厂、质量追责、误操作回放都从这里查。
/// 过滤条件全部下推到 SQL，不在内存里筛。
/// </summary>
[ApiController]
[Route("api/audit")]
[Authorize(Roles = Roles.Admin)]
public class AuditController(MesDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? userName,
        [FromQuery] string? action,
        [FromQuery] bool? success,
        [FromQuery] int page = 1,
        [FromQuery] int size = 50,
        CancellationToken ct = default)
    {
        size = Math.Clamp(size, 1, 200);
        page = Math.Max(1, page);

        var query = db.AuditLogs.AsNoTracking().AsQueryable();

        if (from.HasValue) query = query.Where(a => a.CreatedAtUtc >= from.Value.ToUniversalTime());
        if (to.HasValue) query = query.Where(a => a.CreatedAtUtc <= to.Value.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(userName))
            query = query.Where(a => a.UserName != null && a.UserName.Contains(userName));
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action != null && a.Action.StartsWith(action));
        if (success.HasValue) query = query.Where(a => a.Success == success.Value);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(a => new
            {
                a.Id,
                a.UserId,
                a.UserName,
                a.Role,
                a.Method,
                a.Path,
                a.Action,
                a.StatusCode,
                a.Success,
                a.Message,
                a.ClientIp,
                a.RequestId,
                a.DurationMs,
                a.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Ok(new { total, page, size, items });
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Detail(long id, CancellationToken ct)
    {
        var log = await db.AuditLogs.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        return log is null ? NotFound(new { detail = "审计记录不存在" }) : Ok(log);
    }
}
