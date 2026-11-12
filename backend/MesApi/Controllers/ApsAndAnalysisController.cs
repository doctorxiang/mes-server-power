using MesApi.Domain;
using MesApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MesApi.Controllers;

/// <summary>APS 有限产能排程（简化版正排算法，见 ApsService 注释）。</summary>
[ApiController]
[Route("api/aps")]
[Authorize(Roles = $"{Roles.Admin},{Roles.Planner},{Roles.Leader}")]
public class ApsController(IApsService aps) : ControllerBase
{
    [HttpPost("run")]
    public async Task<ApsResult> Run([FromQuery] bool includeDraft = false, CancellationToken ct = default)
        => await aps.RunAsync(includeDraft, ct);
}

/// <summary>工单报告：按工单/机种看良品率、直通率与不良分布。</summary>
[ApiController]
[Route("api/analysis")]
[Authorize]
public class AnalysisController(IYieldAnalysisService analysis) : ControllerBase
{
    /// <summary>
    /// by=order 按工单 / by=product 按机种；时间按生产日期（东八区）过滤；
    /// product / order 用于工单报告页的机种与工单下钻。
    /// </summary>
    [HttpGet("yield")]
    public async Task<YieldResult> Yield(
        [FromQuery] string? by, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? product, [FromQuery] string? order,
        CancellationToken ct = default)
        => await analysis.QueryAsync(by ?? "order", from, to, product, order, ct);
}
