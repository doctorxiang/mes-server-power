using MesApi.Contracts;
using MesApi.Domain;
using MesApi.Infrastructure;
using MesApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MesApi.Controllers;

/// <summary>
/// QC 抽检：对象是已包装的成品（过了末道包装工序）。质检员按批抽检并留证据，
/// 不合格不改 SN 状态（现场是加严抽检 + 回溯同批），只记不良用于复盘。
/// </summary>
[ApiController]
[Route("api/qc")]
[Authorize]
public class QcController(IQcInspectionService qc) : ControllerBase
{
    /// <summary>抽检记录 + 汇总。可按日期（抽检时间）/ 产线 / 机种 / 工单 / 判定筛选。</summary>
    [HttpGet("inspections")]
    public async Task<ActionResult<QcInspectionResult>> List(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? lineId,
        [FromQuery] string? product, [FromQuery] string? order, [FromQuery] string? result,
        [FromQuery] int? limit, CancellationToken ct)
        => Ok(await qc.ListAsync(from, to, lineId, product, order, result, limit ?? 100, ct));

    /// <summary>待抽检候选：已包装完工的成品（按完工时间倒序，含已抽检次数）。</summary>
    [HttpGet("candidates")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.Leader},{Roles.Qc},{Roles.Viewer}")]
    public async Task<ActionResult<IReadOnlyList<QcCandidateRow>>> Candidates(
        [FromQuery] string? order, [FromQuery] int? lineId, [FromQuery] string? product,
        [FromQuery] int? limit, CancellationToken ct)
        => Ok(await qc.CandidatesAsync(order, lineId, product, limit ?? 30, ct));

    /// <summary>录入一条抽检记录（质检员 / 班组长 / 管理员）。</summary>
    [HttpPost("inspections")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.Leader},{Roles.Qc}")]
    public async Task<ActionResult<QcInspectionRow>> Create(
        CreateQcInspectionRequest request, CancellationToken ct)
        => Ok(await qc.CreateAsync(request, User.GetUserId(), User.GetDisplayName(), ct));
}
