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
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkOrderListItem>>> List(
        [FromQuery] string? status, [FromQuery] int? lineId, [FromQuery] string? keyword, CancellationToken ct)
        => Ok(await orders.ListAsync(status, lineId, keyword, ct));

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
}
