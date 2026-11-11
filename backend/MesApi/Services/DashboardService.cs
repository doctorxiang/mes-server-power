using MesApi.Contracts;
using MesApi.Data;
using MesApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

public interface IDashboardService
{
    Task<DashboardDto> GetAsync(int? lineId, string? date, CancellationToken ct);
}

/// <summary>
/// 车间看板聚合。全部为分组聚合查询，命中 (production_date, ...) 索引；
/// 200 台终端 5 秒轮询 + SignalR 增量推送，单实例即可支撑。
/// </summary>
public class DashboardService(MesDbContext db) : IDashboardService
{
    public async Task<DashboardDto> GetAsync(int? lineId, string? date, CancellationToken ct)
    {
        var day = string.IsNullOrWhiteSpace(date) ? FactoryClock.Today : date.Trim();
        if (!DateTime.TryParse(day, out _))
            throw new Infrastructure.AppException("日期格式应为 yyyy-MM-dd", StatusCodes.Status400BadRequest);

        var lines = await db.Lines.AsNoTracking().Where(l => l.IsActive)
            .OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.Name })
            .ToListAsync(ct);

        if (lineId is not null)
            lines = lines.Where(l => l.Id == lineId).ToList();

        var planByLine = await db.WorkOrders.AsNoTracking()
            .Where(w => (w.Status == nameof(WorkOrderStatus.Released) || w.Status == nameof(WorkOrderStatus.InProduction))
                        && (lineId == null || w.LineId == lineId))
            .GroupBy(w => w.LineId)
            .Select(g => new { LineId = g.Key, Plan = g.Sum(x => x.PlanQty) })
            .ToListAsync(ct);

        var unitStats = await db.Units.AsNoTracking()
            .Where(u => u.ProductionDate == day && (lineId == null || u.LineId == lineId))
            .GroupBy(u => new { u.LineId, u.Status })
            .Select(g => new { g.Key.LineId, g.Key.Status, Qty = g.Count() })
            .ToListAsync(ct);

        var moveStats = await db.MoveLogs.AsNoTracking()
            .Where(x => x.ProductionDate == day && (lineId == null || x.LineId == lineId))
            .GroupBy(x => new { x.LineId, x.Result })
            .Select(g => new { g.Key.LineId, g.Key.Result, Qty = g.Count() })
            .ToListAsync(ct);

        var hourlyRows = await db.MoveLogs.AsNoTracking()
            .Where(x => x.ProductionDate == day && (lineId == null || x.LineId == lineId))
            .GroupBy(x => new { x.ProductionHour, x.Result })
            .Select(g => new { g.Key.ProductionHour, g.Key.Result, Qty = g.Count() })
            .ToListAsync(ct);

        var wipRows = await db.Units.AsNoTracking()
            .Where(u => u.Status == nameof(UnitStatus.InProcess) && (lineId == null || u.LineId == lineId))
            .GroupBy(u => new { u.LineId, u.CurrentStepId })
            .Select(g => new { g.Key.LineId, g.Key.CurrentStepId, Qty = g.Count() })
            .ToListAsync(ct);

        var steps = await db.Steps.AsNoTracking()
            .Select(s => new { s.Id, s.Code, s.Name })
            .ToListAsync(ct);

        var topDefectRows = await db.MoveLogs.AsNoTracking()
            .Where(x => x.ProductionDate == day && x.Result == "Fail" && x.DefectCodeId != null
                        && (lineId == null || x.LineId == lineId))
            .GroupBy(x => x.DefectCodeId)
            .Select(g => new { DefectCodeId = g.Key, Qty = g.Count() })
            .OrderByDescending(x => x.Qty)
            .Take(6)
            .ToListAsync(ct);

        var defectIds = topDefectRows.Select(x => x.DefectCodeId!.Value).ToList();
        var defectDict = await db.DefectCodes.AsNoTracking()
            .Where(d => defectIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Code, d.Name, d.Category })
            .ToListAsync(ct);

        var openByLine = await db.Exceptions.AsNoTracking()
            .Where(e => e.Status == "Open" && (lineId == null || e.LineId == lineId))
            .GroupBy(e => e.LineId)
            .Select(g => new { LineId = g.Key, Qty = g.Count() })
            .ToListAsync(ct);

        var recent = await db.MoveLogs.AsNoTracking()
            .Include(x => x.Step).Include(x => x.DefectCode).Include(x => x.SerialUnit)
            .Where(x => lineId == null || x.LineId == lineId)
            .OrderByDescending(x => x.Id)
            .Take(10)
            .Select(x => new RecentLogRow(x.Id, x.SerialUnit!.Sn, x.Step!.Name, x.Result,
                x.DefectCode != null ? x.DefectCode.Name : null, x.OperatorName, x.StationCode, x.CreatedAtUtc))
            .ToListAsync(ct);

        var lineSummaries = new List<LineSummaryDto>();
        foreach (var line in lines)
        {
            var pass = moveStats.Where(m => m.LineId == line.Id && m.Result == "Pass").Sum(m => m.Qty);
            var fail = moveStats.Where(m => m.LineId == line.Id && m.Result == "Fail").Sum(m => m.Qty);
            var wipByStep = wipRows.Where(w => w.LineId == line.Id)
                .Select(w =>
                {
                    var step = steps.FirstOrDefault(s => s.Id == w.CurrentStepId);
                    return new StepWipDto(step?.Code ?? "-", step?.Name ?? "未上线", w.Qty);
                })
                .OrderByDescending(x => x.WipQty)
                .ToList();

            lineSummaries.Add(new LineSummaryDto(
                line.Id, line.Name,
                planByLine.FirstOrDefault(p => p.LineId == line.Id)?.Plan ?? 0,
                unitStats.Where(u => u.LineId == line.Id).Sum(u => u.Qty),
                unitStats.Where(u => u.LineId == line.Id && u.Status == nameof(UnitStatus.Completed)).Sum(u => u.Qty),
                unitStats.Where(u => u.LineId == line.Id && u.Status == nameof(UnitStatus.Hold)).Sum(u => u.Qty),
                pass, fail, Percent(pass, pass + fail), wipByStep));
        }

        var totalPass = moveStats.Where(m => m.Result == "Pass").Sum(m => m.Qty);
        var totalFail = moveStats.Where(m => m.Result == "Fail").Sum(m => m.Qty);

        var wipAll = wipRows
            .GroupBy(w => w.CurrentStepId)
            .Select(g =>
            {
                var step = steps.FirstOrDefault(s => s.Id == g.Key);
                return new StepWipDto(step?.Code ?? "-", step?.Name ?? "未上线", g.Sum(x => x.Qty));
            })
            .OrderByDescending(x => x.WipQty)
            .ToList();

        return new DashboardDto(
            day,
            FactoryClock.ShiftOf(DateTime.UtcNow),
            DateTime.UtcNow,
            lineSummaries.Sum(l => l.PlanQty),
            unitStats.Sum(u => u.Qty),
            unitStats.Where(u => u.Status == nameof(UnitStatus.Completed)).Sum(u => u.Qty),
            unitStats.Where(u => u.Status == nameof(UnitStatus.Hold)).Sum(u => u.Qty),
            totalPass,
            totalFail,
            Percent(totalPass, totalPass + totalFail),
            openByLine.Sum(x => x.Qty),
            lineSummaries,
            BuildHourly(hourlyRows.Select(h => (h.ProductionHour, h.Result, h.Qty)).ToList(), day),
            wipAll,
            defectDict
                .Select(d => new DefectTopDto(d.Code, d.Name, d.Category,
                    topDefectRows.First(x => x.DefectCodeId == d.Id).Qty))
                .OrderByDescending(x => x.Qty)
                .ToList(),
            recent);
    }

    private static double Percent(int numerator, int denominator)
        => denominator == 0 ? 0 : Math.Round(numerator * 100.0 / denominator, 2);

    /// <summary>小时产出补零：今天从 07:00 补到当前小时，历史日期补 07:00-23:00。</summary>
    private static List<HourPointDto> BuildHourly(List<(string Hour, string Result, int Qty)> rows, string day)
    {
        var end = day == FactoryClock.Today ? FactoryClock.NowLocal.Hour : 23;
        if (end < 7) end = 7;

        var result = new List<HourPointDto>(end - 6);
        for (var h = 7; h <= end; h++)
        {
            var key = h.ToString("00");
            result.Add(new HourPointDto(
                $"{key}:00",
                rows.Where(r => r.Hour == key && r.Result == "Pass").Sum(r => r.Qty),
                rows.Where(r => r.Hour == key && r.Result == "Fail").Sum(r => r.Qty)));
        }
        return result;
    }
}
