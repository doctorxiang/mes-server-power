using MesApi.Contracts;
using MesApi.Infrastructure;
using MesApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MesApi.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class BoardController(
    IDashboardService dashboard,
    ITraceService trace,
    IMasterDataService master,
    IExceptionService exceptions) : ControllerBase
{
    /// <summary>车间看板聚合数据（默认今天、全厂）。</summary>
    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardDto>> Dashboard([FromQuery] int? lineId, [FromQuery] string? date, CancellationToken ct)
        => Ok(await dashboard.GetAsync(lineId, date, ct));

    /// <summary>按 SN 追溯：完整工序履历、操作人、工位、耗时、不良。</summary>
    [HttpGet("trace/{sn}")]
    public async Task<ActionResult<TraceResponse>> Trace(string sn, CancellationToken ct)
        => Ok(await trace.TraceAsync(sn, ct));

    /// <summary>主数据引导包：终端启动时一次性拉取产线/工位/产品/工艺路线/不良代码。</summary>
    [HttpGet("master/bootstrap")]
    public async Task<ActionResult<MasterBootstrap>> Bootstrap(CancellationToken ct)
        => Ok(await master.GetBootstrapAsync(ct));

    [HttpGet("exceptions")]
    public async Task<ActionResult<IReadOnlyList<ExceptionRow>>> Exceptions([FromQuery] string? status, [FromQuery] int? lineId, CancellationToken ct)
        => Ok(await exceptions.ListAsync(status, lineId, ct));

    /// <summary>现场异常上报（缺料 / 设备故障 / 品质异常 / 其他）。</summary>
    [HttpPost("exceptions")]
    public async Task<ActionResult<ExceptionRow>> CreateException(CreateExceptionRequest request, CancellationToken ct)
        => Ok(await exceptions.CreateAsync(request, User.GetUserId(), User.GetDisplayName(), ct));

    [HttpPost("exceptions/{id:int}/close")]
    public async Task<ActionResult<ExceptionRow>> CloseException(int id, CloseExceptionRequest request, CancellationToken ct)
        => Ok(await exceptions.CloseAsync(id, request, User.GetDisplayName(), ct));
}
