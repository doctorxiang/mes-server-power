using MesApi.Contracts;
using MesApi.Domain;
using MesApi.Infrastructure;
using MesApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MesApi.Controllers;

[ApiController]
[Route("api/work-orders")]
[Authorize]
public class WorkOrdersController(IWorkOrderService orders) : ControllerBase
{
    /// <summary>
    /// 工单列表。status 支持单值，或 active（在制）/ done（已完工+关单）；
    /// from / to 按"计划开工"日期（东八区自然日）过滤。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkOrderListItem>>> List(
        [FromQuery] string? status, [FromQuery] int? lineId, [FromQuery] string? keyword,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        => Ok(await orders.ListAsync(status, lineId, keyword, from, to, ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<WorkOrderDetail>> Get(int id, CancellationToken ct)
        => Ok(await orders.GetAsync(id, ct));

    /// <summary>扫码枪扫工单条码后按单号查（报工页自动识别机型与工艺路线）。</summary>
    [HttpGet("by-no/{orderNo}")]
    public async Task<ActionResult<WorkOrderListItem>> GetByNo(string orderNo, CancellationToken ct)
        => Ok(await orders.GetByNoAsync(orderNo, ct));

    [HttpPost]
    [Authorize(Roles = $"{Roles.Admin},{Roles.Planner}")]
    public async Task<ActionResult<WorkOrderDetail>> Create(CreateWorkOrderRequest request, CancellationToken ct)
    {
        var detail = await orders.CreateAsync(request, User.GetUserId(), ct);
        return CreatedAtAction(nameof(Get), new { id = detail.Summary.Id }, detail);
    }

    /// <summary>工单状态流转：release / close / reopen。</summary>
    [HttpPost("{id:int}/status")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.Planner}")]
    public async Task<ActionResult<WorkOrderListItem>> ChangeStatus(int id, [FromQuery] string action, CancellationToken ct)
        => Ok(await orders.ChangeStatusAsync(id, action, ct));

    /// <summary>
    /// 补料：报废一台补一台，计划员/班组长追加发料。
    /// 前端在工单出现 ReplenishQty &gt; 0 时才提示，但接口本身允许提前补。
    /// </summary>
    [HttpPost("{id:int}/replenish")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.Planner},{Roles.Leader}")]
    public async Task<ActionResult<WorkOrderListItem>> Replenish(int id, ReplenishRequest request, CancellationToken ct)
        => Ok(await orders.ReplenishAsync(id, request.Qty, request.Reason, ct));
}
