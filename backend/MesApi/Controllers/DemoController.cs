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
    /// <summary>POST /api/demo/topup —— 把在制单收尾到 85% 直通率，并追加 12 张已完工工单。</summary>
    [HttpPost("topup")]
    public async Task<IActionResult> TopUp(CancellationToken ct)
    {
        // 生产环境默认关掉：造数据接口不该出现在正式库上
        if (!(config.GetValue<bool?>("Demo:AllowTopUp") ?? false))
            return NotFound(new { message = "演示补数已关闭（配置项 Demo:AllowTopUp=false）" });

        var result = await DemoTopUp.RunAsync(db, logger, ct);
        return Ok(result);
    }
}
