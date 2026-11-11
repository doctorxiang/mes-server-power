using MesApi.Contracts;
using MesApi.Domain;
using MesApi.Infrastructure;
using MesApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MesApi.Controllers;

[ApiController]
[Route("api/production")]
[Authorize]
public class ProductionController(IProductionService production) : ControllerBase
{
    /// <summary>
    /// 扫码过站（扫码枪为键盘输入，前端回车即提交）。
    /// 幂等：同一次扫码携带同一 clientKey，断网重试不会重复计数。
    /// </summary>
    [HttpPost("scan")]
    [EnableRateLimiting("scan")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.Planner},{Roles.Leader},{Roles.Operator},{Roles.Qc}")]
    public async Task<ActionResult<ScanResponse>> Scan(ScanRequest request, CancellationToken ct)
        => Ok(await production.ScanAsync(request, User.GetUserId(), User.GetDisplayName(), ct));

    /// <summary>当前工位最近过站流水（报工页实时列表）。</summary>
    [HttpGet("recent")]
    public async Task<ActionResult<IReadOnlyList<RecentLogRow>>> Recent([FromQuery] string? stepCode, [FromQuery] int limit, CancellationToken ct)
        => Ok(await production.RecentAsync(stepCode, limit, ct));

    /// <summary>不良品处理：返修放行 / 报废（班组长、质检、管理员）。</summary>
    [HttpPost("hold-handle")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.Leader},{Roles.Qc}")]
    public async Task<ActionResult<UnitActionResponse>> HandleHold([FromBody] HandleHoldRequest request, [FromQuery] string sn, CancellationToken ct)
        => Ok(await production.HandleHoldAsync(sn, request, User.GetUserId(), User.GetDisplayName(), ct));

    /// <summary>维修站：待维修队列（隔离中的 SN，含维修次数与在修标记）。</summary>
    [HttpGet("repair/pending")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.Leader},{Roles.Qc}")]
    public async Task<ActionResult<IReadOnlyList<RepairPendingRow>>> RepairPending([FromQuery] int limit, CancellationToken ct)
        => Ok(await production.RepairPendingAsync(limit, ct));

    /// <summary>维修站进站：SN 确认维修，开维修记录（重复维修超上限需管理员 force）。</summary>
    [HttpPost("repair/checkin")]
    [EnableRateLimiting("scan")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.Leader},{Roles.Qc}")]
    public async Task<ActionResult<RepairRecordRow>> RepairCheckin([FromBody] RepairCheckinRequest request, CancellationToken ct)
        => Ok(await production.RepairCheckinAsync(request, User.GetUserId(), User.GetDisplayName(), ct));

    /// <summary>维修站出站：repaired = 修复放回原工序重测 / scrap = 报废。</summary>
    [HttpPost("repair/checkout")]
    [EnableRateLimiting("scan")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.Leader},{Roles.Qc}")]
    public async Task<ActionResult<UnitActionResponse>> RepairCheckout([FromBody] RepairCheckoutRequest request, CancellationToken ct)
        => Ok(await production.RepairCheckoutAsync(request, User.GetUserId(), User.GetDisplayName(), ct));
}
