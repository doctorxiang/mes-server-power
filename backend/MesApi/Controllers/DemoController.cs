using MesApi.Data;
using MesApi.Domain;
using MesApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MesApi.Controllers;

/// <summary>
/// 演示数据补数口（仅 Admin + 配置开关）。
/// 不重启、不迁移，直接对运行中的库做幂等增量写入，改完即生效。
/// </summary>
[ApiController]
[Route("api/demo")]
[Authorize(Roles = Roles.Admin)]
public class DemoController(MesDbContext db, IConfiguration config, ILogger<DemoController> logger) : ControllerBase
{
    /// <summary>POST /api/demo/topup：幂等补齐，已有的工单号跳过。</summary>
    [HttpPost("topup")]
    public async Task<IActionResult> TopUp(CancellationToken ct)
    {
        if (!Enabled) return NotFound(new { message = "演示数据接口已关闭（配置项 Demo:AllowTopUp=false）" });

        var result = await DemoTopUp.RunAsync(db, logger, reset: false, ct);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/demo/reset：清空生产数据（工单/SN/流水/维修/异常/日报）后重建。
    /// 主数据与账号不动，前端无需重新登录。
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
