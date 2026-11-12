using MesApi.Data;
using MesApi.Domain;
using MesApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MesApi.Controllers;

/// <summary>
/// 演示数据补数口（仅 Admin + 配置开关）。
///
/// 它存在的意义不只是"造数据"，而是一个"数据热更新"的最小样板：
/// 不重启进程、不重建数据库、不跑 EF 迁移，直接对着正在跑的库做幂等增量写入，
/// 下一个请求查到的就是新数据 —— 因为良率分析是实时查库、主数据缓存 2 分钟自动过期。
/// </summary>
[ApiController]
[Route("api/demo")]
[Authorize(Roles = Roles.Admin)]
public class DemoController(MesDbContext db, IConfiguration config, ILogger<DemoController> logger) : ControllerBase
{
    /// <summary>POST /api/demo/topup —— 幂等补齐：已有的工单号跳过，只补缺的部分。</summary>
    [HttpPost("topup")]
    public async Task<IActionResult> TopUp(CancellationToken ct)
    {
        if (!Enabled) return NotFound(new { message = "演示数据接口已关闭（配置项 Demo:AllowTopUp=false）" });

        var result = await DemoTopUp.RunAsync(db, logger, reset: false, ct);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/demo/reset —— 清空生产数据（工单/SN/流水/维修/异常/日报）后按"发料=计划、报废补料"口径重建。
    /// 主数据与账号不动，所以前端不用重新登录。
    /// </summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        if (!Enabled) return NotFound(new { message = "演示数据接口已关闭（配置项 Demo:AllowTopUp=false）" });

        logger.LogWarning("演示数据重置：清空生产数据后重建，操作人={User}", User.Identity?.Name);
        var result = await DemoTopUp.RunAsync(db, logger, reset: true, ct);
        return Ok(result);
    }

    private bool Enabled => config.GetValue<bool?>("Demo:AllowTopUp") ?? false;
}
