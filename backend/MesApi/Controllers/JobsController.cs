using MesApi.Domain;
using MesApi.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quartz;

namespace MesApi.Controllers;

/// <summary>
/// 调度任务管理：查看下次触发时间、手动补跑（夜间报表失败/补历史数据时用）。
/// 仅管理员可见。
/// </summary>
[ApiController]
[Route("api/jobs")]
[Authorize(Roles = Roles.Admin)]
public class JobsController(ISchedulerFactory factory) : ControllerBase
{
    public record JobInfo(string Name, string Group, string Description, DateTime? NextFireTimeUtc);

    // 调度任务是代码里静态注册的，这里用固定清单而不是遍历调度器，避免依赖 Quartz 内部 API
    private static readonly JobKey[] Known =
    [
        RepairSlaAlertJob.Key, OverdueOrderAlertJob.Key, NightlyReportJob.Key, HousekeepingJob.Key
    ];

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var scheduler = await factory.GetScheduler(ct);
        var items = new List<JobInfo>();
        foreach (var key in Known)
        {
            var triggers = await scheduler.GetTriggersOfJob(key, ct);
            var next = triggers.Select(t => t.NextFireTimeUtc).Where(t => t.HasValue)
                .OrderBy(t => t).FirstOrDefault();
            items.Add(new JobInfo(key.Name, key.Group, JobDescription(key.Name), next?.UtcDateTime));
        }
        return Ok(items);
    }

    /// <summary>立即执行一次（不影响原有触发计划）。可选 date=yyyy-MM-dd 补指定日期报表。</summary>
    [HttpPost("{name}/run")]
    public async Task<IActionResult> Run(string name, [FromQuery] string? date, CancellationToken ct)
    {
        var scheduler = await factory.GetScheduler(ct);
        var key = Known.FirstOrDefault(k => k.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (key is null) return NotFound(new { detail = $"任务不存在：{name}" });

        var data = new JobDataMap();
        if (!string.IsNullOrWhiteSpace(date)) data["date"] = date;

        await scheduler.TriggerJob(key, data, ct);
        return Ok(new { message = $"已触发 {key.Name}，执行结果见日志" });
    }

    private static string JobDescription(string name) => name switch
    {
        "RepairSlaAlert" => "维修超时预警（每 5 分钟）",
        "OverdueOrderAlert" => "交期预警（每 30 分钟）",
        "NightlyReport" => "夜间报表（每天 02:30）",
        "Housekeeping" => "数据保养：过期令牌与审计清理（每天 03:30）",
        _ => name
    };
}
