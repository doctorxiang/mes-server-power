using MesApi.Data;
using MesApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

/// <summary>补数结果：接口直接回给前端/命令行，方便核对"补了多少、报废多少、还缺多少料"。</summary>
public record DemoTopUpResult(
    int OrdersCreated,
    int UnitsCreated,
    int LogsCreated,
    int RepairsCreated,
    int CompletedOrders,
    int PlanQty,
    int IssuedQty,
    int ScrapQty,
    double YieldBefore,
    double YieldAfter,
    double FpyAfter,
    string Summary);

/// <summary>单台的"命运"：一次纯净走完 / 判不良返修救回 / 判不良报废 / 还在中间工序 / 卡在维修站。</summary>
internal enum UnitFate
{
    Clean,      // 一次过，没判过不良
    Repaired,   // 判过不良，进维修站修好，回原工序重测通过 → 计入良品率但不算直通率(FPY)
    Scrap,      // 判过不良，维修站判定不可修 → 报废，走补料补回这一台
    Wip,        // 走到中间工序，还在制
    Hold        // 判了不良，正在维修站里，等出站结论
}

/// <summary>
/// 演示数据生成器 —— 按"真实工单的物料闭环"造数，不是随便凑数字：
///
///   计划 120 台（客户要的良品数）
///   → 发料 120 套（<b>不预置冗余</b>，计划多少发多少）
///   → 上线 121 台（其中 1 台测试判不良、维修站判不可修 → 报废）
///   → 报废 1 台后补料 1 套，再上线 1 台 → 完工 120 台（正好等于计划，进度 100%）
///
/// 报废率按千分之七控制：一单几百台只报废一两台。
/// 不良按工序分层，这也是现场的真实逻辑：
///   目视检查 / ICT —— 虚焊、连锡、缺件、极性反这类，补焊/换件就能救回，<b>不可能报废</b>
///   老化 / 耐压 / 功能测试 —— 才有报废可能（功率器件击穿、变压器绝缘失效），且必须是电气不良
///
/// 两种入口：
///   RunAsync(reset: true)  → 清空生产数据重建一套（--reset-demo / POST /api/demo/reset）
///   RunAsync(reset: false) → 幂等补齐（已存在的单号跳过，--topup-demo / POST /api/demo/topup）
/// </summary>
public static class DemoTopUp
{
    /// <summary>目标直通率（FPY）：只用来决定"多少台会判一次不良"，不用来反推发料。</summary>
    public const double TargetFpy = MaterialPlanning.TargetFpy;

    /// <summary>经验报废率：千分之七。</summary>
    private const double ScrapRate = MaterialPlanning.ScrapRate;

    private static readonly string[] OperatorNames = ["张小明", "刘芳", "王强"];
    private static readonly int[] OperatorIds = [4, 5, 4];

    /// <summary>历史已完工工单的计划量：12 天，每天一张，白班/夜班交替。</summary>
    private static readonly int[] HistoryPlans = [120, 140, 100, 160, 120, 100, 140, 160, 120, 160, 100, 140];

    public static async Task<DemoTopUpResult> RunAsync(
        MesDbContext db, ILogger logger, bool reset = false, CancellationToken ct = default)
    {
        var rnd = new Random(20261112);
        var steps = await db.Steps.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct);
        var products = await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
        var lines = await db.Lines.AsNoTracking().OrderBy(l => l.Id).ToListAsync(ct);
        var defects = await db.DefectCodes.AsNoTracking().OrderBy(d => d.Id).ToListAsync(ct);
        var now = DateTime.UtcNow;

        if (steps.Count == 0 || products.Count == 0 || lines.Count == 0)
            throw new InvalidOperationException("主数据为空，请先完成初始化种子数据");

        if (reset) await ResetAsync(db, ct);

        var (input0, done0, _) = await SnapshotAsync(db, ct);

        var failSteps = BuildFailSteps(steps, defects);
        var ctx = new GenContext(db, steps, products, lines, failSteps, rnd, now);

        var history = await AddCompletedOrdersAsync(ctx, ct);
        var wip = await AddInProductionOrdersAsync(ctx, ct);
        await AddOpenExceptionsAsync(ctx, ct);

        var (input1, done1, fpy1) = await SnapshotAsync(db, ct);

        var ordersCreated = history.Orders + wip.Orders;
        var unitsCreated = history.Units + wip.Units;
        var logsCreated = history.Logs + wip.Logs;
        var repairsCreated = history.Repairs + wip.Repairs;

        var summary = $"新增 {ordersCreated} 张工单（{wip.Orders} 张在制 + {history.Orders} 张已完工）/ " +
                      $"{unitsCreated} 台 SN / {logsCreated} 条过站流水 / {repairsCreated} 条维修记录 / " +
                      $"报废 {history.Scrap + wip.Scrap} 台；在制单待补料 {wip.ReplenishNeed} 套";

        logger.LogInformation("演示数据生成完成：{Summary}；良品率 {Before}% → {After}%，直通率 FPY {Fpy}%",
            summary, Pct(done0, input0), Pct(done1, input1), Pct(fpy1, input1));

        return new DemoTopUpResult(ordersCreated, unitsCreated, logsCreated, repairsCreated,
            history.Orders, history.PlanQty, history.IssuedQty, history.Scrap,
            Pct(done0, input0), Pct(done1, input1), Pct(fpy1, input1), summary);
    }

    /// <summary>清空生产数据（工单 / SN / 流水 / 维修 / 异常 / 日报），主数据与账号保留。</summary>
    private static async Task ResetAsync(MesDbContext db, CancellationToken ct)
    {
        await db.RepairRecords.ExecuteDeleteAsync(ct);
        await db.MoveLogs.ExecuteDeleteAsync(ct);
        await db.Units.ExecuteDeleteAsync(ct);
        await db.Exceptions.ExecuteDeleteAsync(ct);
        await db.DailyReports.ExecuteDeleteAsync(ct);
        await db.WorkOrders.ExecuteDeleteAsync(ct);
    }

    // ---------------- 1. 历史已完工工单：完工 == 计划，报废一两台后补料 ----------------

    private static async Task<(int Orders, int Units, int Logs, int Repairs, int PlanQty, int IssuedQty, int Scrap)>
        AddCompletedOrdersAsync(GenContext ctx, CancellationToken ct)
    {
        var db = ctx.Db;
        var existing = (await db.WorkOrders.Select(w => w.OrderNo).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int orders = 0, units = 0, logs = 0, repairs = 0, planSum = 0, issuedSum = 0, scrapSum = 0;

        for (var day = HistoryPlans.Length; day >= 1; day--)
        {
            var localDate = FactoryClock.NowLocal.Date.AddDays(-day);
            var orderNo = $"WO{localDate:yyyyMMdd}001";
            if (existing.Contains(orderNo)) continue;   // 幂等：这张单已经有了就跳过

            var idx = HistoryPlans.Length - day;
            var plan = HistoryPlans[idx];

            // 报废：几千台里一两台。160 台的单偶尔报废 2 台，其余都是 1 台
            var scrap = plan >= 150 ? 2 : 1;
            if (idx % 5 == 4 && plan < 150) scrap = 0;   // 有些单运气好，一台没报废
            var input = plan + scrap;                    // 报废那台也是上线过的
            var issued = plan + scrap;                   // 报废后补料补回这一台
            // 判过不良的台数（含报废那台）：让 FPY 落在 85% 左右
            var failed = (int)Math.Round(input * (1 - TargetFpy), MidpointRounding.AwayFromZero);
            var repaired = Math.Max(0, failed - scrap);

            var product = ctx.Products[idx % ctx.Products.Count];
            var line = ctx.Lines[idx % ctx.Lines.Count];
            var prefix = LinePrefix(line.Id, ctx.Lines);

            var dateStr = localDate.ToString("yyyy-MM-dd");
            var dayStartUtc = FactoryClock.StartOfLocalDayUtc(dateStr);
            var startUtc = dayStartUtc.AddHours(idx % 3 == 0 ? 20 : 8);   // 每第 3 张排夜班，班次统计才有对比
            var spacingSec = Math.Max(45, (int)(2.9 * 3600 / input));      // 整批约 3 小时做完，不跨天

            var order = new WorkOrder
            {
                OrderNo = orderNo,
                ProductId = product.Id,
                LineId = line.Id,
                PlanQty = plan,
                IssuedQty = issued,
                Status = nameof(WorkOrderStatus.Completed),
                Priority = 2,
                PlannedStartUtc = startUtc,
                DueDateUtc = startUtc.AddDays(1),
                CreatedByUserId = 2,
                CreatedAtUtc = startUtc.AddDays(-1),
                ReleasedAtUtc = startUtc.AddHours(-2),
                CompletedAtUtc = startUtc.AddHours(4),
                Remark = $"历史完工（演示数据）· 计划 {plan} / 报废 {scrap} / 补料 {scrap} / 返修救回 {repaired}"
            };
            db.WorkOrders.Add(order);
            await db.SaveChangesAsync(ct);   // 先落库拿自增 Id，后面的 unit / log 才有正确外键
            orders++;
            planSum += plan;
            issuedSum += issued;
            scrapSum += scrap;

            var logBag = new List<SnMoveLog>(input * ctx.Steps.Count + 16);
            var repairBag = new List<RepairRecord>(repaired + scrap);

            // 先把每台 SN 的命运排好再打乱，避免"前面全是良品、后面全是报废"这种一眼假的顺序
            var fates = new List<UnitFate>(input);
            fates.AddRange(Enumerable.Repeat(UnitFate.Clean, plan - repaired));
            fates.AddRange(Enumerable.Repeat(UnitFate.Repaired, repaired));
            fates.AddRange(Enumerable.Repeat(UnitFate.Scrap, scrap));
            Shuffle(fates, ctx.Rnd);

            for (var i = 0; i < fates.Count; i++)
            {
                var startAt = startUtc.AddSeconds((long)i * spacingSec + ctx.Rnd.Next(0, 20));
                EmitUnit(ctx, order, line, $"{orderNo}-{i + 1:0000}", startAt, fates[i], 0, logBag, repairBag);
                units++;
            }

            db.MoveLogs.AddRange(logBag);
            if (repairBag.Count > 0) db.RepairRecords.AddRange(repairBag);
            await db.SaveChangesAsync(ct);   // 逐单提交，内存与事务都不会爆

            logs += logBag.Count;
            repairs += repairBag.Count;
        }

        return (orders, units, logs, repairs, planSum, issuedSum, scrapSum);
    }

    // ---------------- 2. 在制工单：料还没用完，进度不到 100% ----------------

    /// <param name="Plan">计划良品数</param>
    /// <param name="Completed">已完工台数（&lt; 计划，所以进度不到 100%）</param>
    /// <param name="Scrap">已报废（几百台的单也就一两台）</param>
    /// <param name="Hold">卡在维修站</param>
    /// <param name="Wip">还在工序之间</param>
    /// <param name="WindowMinutes">这批单已经开了多久</param>
    private sealed record WipSpec(int Plan, int Completed, int Scrap, int Hold, int Wip, int WindowMinutes);

    private static async Task<(int Orders, int Units, int Logs, int Repairs, int ReplenishNeed, int Scrap)> AddInProductionOrdersAsync(
        GenContext ctx, CancellationToken ct)
    {
        var db = ctx.Db;
        var today = FactoryClock.NowLocal.ToString("yyyyMMdd");

        // 三张在制单，故意造出三种现场：
        //   1) 顺利单：报废 2 台已补料，进度 93%
        //   2) 大批量单：还有 WIP 和维修站隔离品，进度 79%
        //   3) 异常单：报废 1 台但还没补料 → 待补料 1 套（前端会亮红，演示补料闭环）
        var specs = new[]
        {
            new WipSpec(300, 280, 2, 2, 3, 360),
            new WipSpec(500, 396, 3, 3, 5, 300),
            new WipSpec(120, 93, 1, 2, 4, 200)
        };

        int orders = 0, units = 0, logs = 0, repairs = 0, replenishNeed = 0, scrapSum = 0;

        for (var k = 0; k < specs.Length; k++)
        {
            var spec = specs[k];
            var orderNo = $"WO{today}{k + 1:000}";
            if (await db.WorkOrders.AnyAsync(w => w.OrderNo == orderNo, ct)) continue;   // 幂等

            var order = await FindOrCreateWipOrderAsync(db, orderNo, k, spec, ct);
            if (order is null) continue;

            var line = ctx.Lines.First(l => l.Id == order.LineId);

            var issued = MaterialPlanning.EffectiveIssued(order);
            var started = spec.Completed + spec.Scrap + spec.Hold + spec.Wip;
            replenishNeed += MaterialPlanning.ReplenishNeed(order.PlanQty, issued, spec.Scrap);

            // 时间轴：从 WindowMinutes 前铺到 45 分钟前，保证最后几台的流水不会跑到"未来"
            var startBase = ctx.Now.AddMinutes(-spec.WindowMinutes);
            var spanMin = Math.Max(20, spec.WindowMinutes - 45);
            var stepMin = (double)spanMin / Math.Max(1, started);

            // 判过不良的比例同样按 85% 直通率反推：返修救回 + 仍在维修站 + 报废
            var failedTotal = (int)Math.Round(started * (1 - TargetFpy), MidpointRounding.AwayFromZero);
            var repaired = Math.Max(0, failedTotal - spec.Scrap - spec.Hold);

            var fates = new List<UnitFate>(started);
            fates.AddRange(Enumerable.Repeat(UnitFate.Clean, Math.Max(0, spec.Completed - repaired)));
            fates.AddRange(Enumerable.Repeat(UnitFate.Repaired, repaired));
            fates.AddRange(Enumerable.Repeat(UnitFate.Scrap, spec.Scrap));
            fates.AddRange(Enumerable.Repeat(UnitFate.Hold, spec.Hold));
            fates.AddRange(Enumerable.Repeat(UnitFate.Wip, spec.Wip));
            Shuffle(fates, ctx.Rnd);

            var logBag = new List<SnMoveLog>(started * ctx.Steps.Count);
            var repairBag = new List<RepairRecord>(spec.Hold + repaired + spec.Scrap);

            for (var i = 0; i < fates.Count; i++)
            {
                var startAt = startBase.AddMinutes(i * stepMin);
                var stopSeq = fates[i] == UnitFate.Wip ? ctx.Rnd.Next(2, ctx.Steps.Count) : 0;
                EmitUnit(ctx, order, line, $"{orderNo}-{i + 1:0000}", startAt, fates[i], stopSeq, logBag, repairBag);
                units++;
            }

            db.MoveLogs.AddRange(logBag);
            if (repairBag.Count > 0) db.RepairRecords.AddRange(repairBag);
            await db.SaveChangesAsync(ct);

            logs += logBag.Count;
            repairs += repairBag.Count;
            scrapSum += spec.Scrap;
            orders++;
        }

        return (orders, units, logs, repairs, replenishNeed, scrapSum);
    }

    /// <summary>
    /// 在制单：优先接管库里已有的同号单（老数据 / SeedData 建的），
    /// 没有就按新口径新建。发料 = 计划，只有"异常单"故意留着不补料。
    /// </summary>
    private static async Task<WorkOrder?> FindOrCreateWipOrderAsync(
        MesDbContext db, string orderNo, int index, WipSpec spec, CancellationToken ct)
    {
        var existing = await db.WorkOrders.FirstOrDefaultAsync(w => w.OrderNo == orderNo, ct);
        if (existing is not null)
        {
            // 老数据可能没发料量：补上，但已有的 SN 不动（幂等）
            if (existing.IssuedQty <= 0) existing.IssuedQty = MaterialPlanning.IssuedFor(existing.PlanQty);
            return null;   // 已有单就不再重复造 SN，避免重复数据
        }

        var products = await db.Products.OrderBy(p => p.Id).ToListAsync(ct);
        var lines = await db.Lines.OrderBy(l => l.Id).ToListAsync(ct);
        var product = products[index % products.Count];
        var line = lines[index % lines.Count];

        var order = new WorkOrder
        {
            OrderNo = orderNo,
            ProductId = product.Id,
            LineId = line.Id,
            PlanQty = spec.Plan,
            // 报废的料已经补过（第 3 张单例外：故意留 1 套缺口演示补料）
            IssuedQty = spec.Plan + (index == 2 ? 0 : spec.Scrap),
            Status = nameof(WorkOrderStatus.InProduction),
            Priority = index == 0 ? 1 : 2,
            PlannedStartUtc = DateTime.UtcNow.AddMinutes(-spec.WindowMinutes),
            DueDateUtc = DateTime.UtcNow.AddDays(index == 2 ? 1 : 2),
            CreatedByUserId = 2,
            ReleasedAtUtc = DateTime.UtcNow.AddMinutes(-spec.WindowMinutes),
            Remark = index == 2 ? "AI 服务器配套 · 1 台报废待补料" : null
        };

        db.WorkOrders.Add(order);
        await db.SaveChangesAsync(ct);
        return order;
    }

    // ---------------- 单台 SN 的完整旅程 ----------------

    private static void EmitUnit(
        GenContext ctx, WorkOrder order, ProductionLine line, string sn, DateTime startAt,
        UnitFate fate, int stopSeq, List<SnMoveLog> logBag, List<RepairRecord> repairBag)
    {
        var unit = new SerialUnit
        {
            Sn = sn,
            WorkOrder = order,
            WorkOrderId = order.Id,
            ProductId = order.ProductId,
            LineId = order.LineId,
            CurrentSeq = 1,
            CurrentStepId = ctx.Steps[0].Id,
            Status = nameof(UnitStatus.InProcess),
            ProductionDate = FactoryClock.DateOf(startAt),
            Shift = FactoryClock.ShiftOf(startAt),
            CreatedAtUtc = startAt,
            LastScanAtUtc = startAt
        };
        ctx.Db.Units.Add(unit);

        var prefix = LinePrefix(line.Id, ctx.Lines);
        var cursor = startAt;

        // 判不良的工序 + 不良代码：报废只能落在 ICT 之后的测试工序，且必须是电气类不良
        var failSeq = 0;
        FailStep? failStep = null;
        DefectCode? failDefect = null;

        if (fate is UnitFate.Repaired or UnitFate.Hold or UnitFate.Scrap)
        {
            var point = PickFailPoint(ctx, fate == UnitFate.Scrap);
            failStep = point.Step;
            failDefect = point.Defect;
            failSeq = point.Step.Seq;
        }

        var lastSeq = fate == UnitFate.Wip ? Math.Clamp(stopSeq, 1, ctx.Steps.Count) : ctx.Steps.Count;

        for (var seq = 1; seq <= lastSeq; seq++)
        {
            var step = ctx.Steps[seq - 1];
            var station = $"{prefix}-{seq:00}";
            var (opId, opName) = PickOperator(ctx.Rnd);
            cursor = cursor.AddSeconds(ctx.Rnd.Next(40, 110));

            if (seq == failSeq && failStep is not null && failDefect is not null)
            {
                var failLog = NewLog(unit, order, step, seq, cursor, station, opId, opName, ctx.Rnd);
                failLog.Result = "Fail";
                failLog.DefectCodeId = failDefect.Id;
                failLog.Remark = failDefect.Name;
                logBag.Add(failLog);

                unit.LastScanAtUtc = cursor;
                unit.CurrentSeq = seq;
                unit.CurrentStepId = step.Id;

                if (fate == UnitFate.Scrap)
                {
                    // 报废件也要在维修站留一条"判定不可修"的记录：详情页的报废明细就是靠它讲清原因
                    unit.Status = nameof(UnitStatus.Scrapped);
                    repairBag.Add(NewRepair(ctx, order, unit, step, failDefect, prefix,
                        status: "Scrapped", checkoutBy: "李维修", action: "Other",
                        startedAt: cursor.AddMinutes(ctx.Rnd.Next(5, 20)),
                        completedAt: cursor.AddMinutes(ctx.Rnd.Next(25, 60))));
                    return;
                }

                if (fate == UnitFate.Hold)
                {
                    // 还在维修站里：维修记录保持 InProgress，维修站的待办队列才有东西
                    unit.Status = nameof(UnitStatus.Hold);
                    repairBag.Add(NewRepair(ctx, order, unit, step, failDefect, prefix,
                        status: "InProgress", checkoutBy: null, action: "Other",
                        startedAt: cursor.AddMinutes(5), completedAt: null));
                    return;
                }

                // 返修救回：进站 → 出站 → 回原工序重测 → 继续往后走（所以计入良品率，但不计入 FPY）
                var repairStart = cursor.AddMinutes(ctx.Rnd.Next(10, 40));
                var repairedAt = repairStart.AddMinutes(ctx.Rnd.Next(15, 45));
                repairBag.Add(NewRepair(ctx, order, unit, step, failDefect, prefix,
                    status: "Repaired", checkoutBy: "李维修", action: PickRepairAction(failDefect),
                    startedAt: repairStart, completedAt: repairedAt));

                cursor = repairedAt.AddMinutes(ctx.Rnd.Next(2, 12));
                logBag.Add(NewLog(unit, order, step, seq, cursor, station, opId, opName, ctx.Rnd));
                unit.CurrentSeq = seq;
                unit.CurrentStepId = step.Id;
                unit.LastScanAtUtc = cursor;
                continue;
            }

            logBag.Add(NewLog(unit, order, step, seq, cursor, station, opId, opName, ctx.Rnd));
            unit.CurrentSeq = seq;
            unit.CurrentStepId = step.Id;
            unit.LastScanAtUtc = cursor;
        }

        if (fate == UnitFate.Wip)
        {
            // 还卡在中间：下一站是 lastSeq+1
            var nextSeq = Math.Min(lastSeq + 1, ctx.Steps.Count);
            unit.CurrentSeq = nextSeq;
            unit.CurrentStepId = ctx.Steps[nextSeq - 1].Id;
            unit.Status = nameof(UnitStatus.InProcess);
            return;
        }

        unit.Status = nameof(UnitStatus.Completed);
        unit.CurrentSeq = ctx.Steps.Count;
        unit.CurrentStepId = ctx.Steps[^1].Id;
        unit.CompletedAtUtc = cursor;
        unit.LastScanAtUtc = cursor;
    }

    private static RepairRecord NewRepair(
        GenContext ctx, WorkOrder order, SerialUnit unit, ProcessStep step, DefectCode defect,
        string prefix, string status, string? checkoutBy, string action,
        DateTime startedAt, DateTime? completedAt) => new()
        {
            SerialUnit = unit,
            WorkOrderId = order.Id,
            RepairNo = 1,
            SourceStepId = step.Id,
            DefectCodeId = defect.Id,
            Action = action,
            Status = status,
            Remark = status == "Scrapped"
                ? $"{step.Name}判{defect.Name}，判定不可修"
                : $"{step.Name}判{defect.Name}",
            OperatorId = 6,
            OperatorName = "李维修",
            StationCode = $"{prefix}-RP",
            CheckoutByName = checkoutBy,
            StartedAtUtc = startedAt,
            CompletedAtUtc = completedAt
        };

    // ---------------- 3. 现场异常（看板异常计数） ----------------

    private static async Task AddOpenExceptionsAsync(GenContext ctx, CancellationToken ct)
    {
        var db = ctx.Db;
        if (await db.Exceptions.AnyAsync(ct)) return;   // 幂等

        var orders = await db.WorkOrders.OrderBy(w => w.Id).Take(3).ToListAsync(ct);
        if (orders.Count < 3) return;

        db.Exceptions.AddRange(
            new ExceptionEvent
            {
                Type = "Material",
                Description = "1200W 机种散热片来料尺寸超差，已隔离待 MRB 判定",
                LineId = orders[0].LineId,
                WorkOrderId = orders[0].Id,
                StationCode = "A-01",
                Status = "Open",
                ReportedByUserId = 3,
                ReportedByName = "王班长",
                ReportedAtUtc = ctx.Now.AddMinutes(-45)
            },
            new ExceptionEvent
            {
                Type = "Equipment",
                Description = "老化房 3# 柜温度波动，设备已报修",
                LineId = orders[2].LineId,
                WorkOrderId = orders[2].Id,
                StationCode = "B-06",
                Status = "Open",
                ReportedByUserId = 3,
                ReportedByName = "王班长",
                ReportedAtUtc = ctx.Now.AddMinutes(-20)
            });

        await db.SaveChangesAsync(ct);
    }

    // ---------------- 不良分布：按工序分层 ----------------

    /// <summary>
    /// 一个"可能判不良"的工序：带自己的不良代码池和报废概率。
    /// <para>ScrapShare = 0 表示这道工序判出来的不良一定能修好（虚焊、连锡、缺件、极性反…）。</para>
    /// </summary>
    private sealed record FailStep(int Seq, string StepCode, string StepName, List<DefectCode> Pool, double Weight, double ScrapShare);

    private static List<FailStep> BuildFailSteps(List<ProcessStep> steps, List<DefectCode> pool)
    {
        var byCode = pool.ToDictionary(d => d.Code, d => d, StringComparer.OrdinalIgnoreCase);
        List<DefectCode> Take(params string[] codes)
            => codes.Where(c => byCode.ContainsKey(c)).Select(c => byCode[c]).ToList();

        // 外观 + 焊接：目视和 ICT 能发现，全部可返修，绝不报废
        var visual = Take("VIS-01", "VIS-02", "VIS-03", "VIS-04", "WELD-01", "WELD-02", "WELD-03", "WELD-04");
        var ict = Take("VIS-01", "VIS-02", "VIS-03", "WELD-01", "WELD-02", "WELD-04");
        // 电气：只有老化/耐压/功能测试才判得出来，才有报废可能
        var burnin = Take("ELE-01", "ELE-02", "ELE-04", "ELE-05");
        var hipot = Take("ELE-03", "ELE-01");
        var fct = Take("ELE-01", "ELE-02", "ELE-05");

        var list = new List<FailStep>();
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var seq = i + 1;
            switch (s.Code)
            {
                case "VISUAL": list.Add(new FailStep(seq, s.Code, s.Name, visual, 0.42, 0)); break;
                case "ICT": list.Add(new FailStep(seq, s.Code, s.Name, ict, 0.24, 0)); break;
                case "BURNIN": list.Add(new FailStep(seq, s.Code, s.Name, burnin, 0.13, 0.30)); break;
                case "HIPOT": list.Add(new FailStep(seq, s.Code, s.Name, hipot, 0.06, 0.60)); break;
                case "FCT": list.Add(new FailStep(seq, s.Code, s.Name, fct, 0.15, 0.20)); break;
            }
        }

        // 自定义路线兜底：拿所有检验/测试工序，配全量不良池，且不允许报废（避免造出"虚焊报废"的假数据）
        if (list.Count == 0)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].Kind is StepKinds.Inspection or StepKinds.Test)
                    list.Add(new FailStep(i + 1, steps[i].Code, steps[i].Name, pool, 1.0, 0));
            }
        }

        return list;
    }

    /// <summary>
    /// 挑一个判不良的点。报废只从 ScrapShare &gt; 0 的工序（ICT 之后的测试）里挑，
    /// 且不良必须是该工序能判出来的电气类；返修则按各工序的实际不良占比加权。
    /// </summary>
    private static (FailStep Step, DefectCode Defect) PickFailPoint(GenContext ctx, bool scrap)
    {
        var candidates = ctx.FailSteps
            .Where(f => f.Pool.Count > 0 && (!scrap || f.ScrapShare > 0))
            .ToList();

        if (candidates.Count == 0)
            candidates = ctx.FailSteps.Where(f => f.Pool.Count > 0).ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException("没有可判不良的检验/测试工序，无法生成演示数据");

        var total = candidates.Sum(f => f.Weight * (scrap ? f.ScrapShare : 1));
        if (total <= 0) return (candidates[0], candidates[0].Pool[ctx.Rnd.Next(candidates[0].Pool.Count)]);

        var roll = ctx.Rnd.NextDouble() * total;
        var step = candidates[^1];
        foreach (var f in candidates)
        {
            roll -= f.Weight * (scrap ? f.ScrapShare : 1);
            if (roll <= 0)
            {
                step = f;
                break;
            }
        }

        return (step, step.Pool[ctx.Rnd.Next(step.Pool.Count)]);
    }

    /// <summary>维修动作要和不良类型对得上：虚焊是补焊，缺件/错件是换件，划伤只能处理外观。</summary>
    private static string PickRepairAction(DefectCode defect) => defect.Category switch
    {
        "焊接" => "Resolder",
        "外观" => defect.Code is "VIS-01" or "VIS-02" or "VIS-03" ? "Replace" : "Other",
        "电气" => "Replace",
        _ => "Other"
    };

    // ---------------- 工具 ----------------

    /// <summary>一趟生成要用的东西打个包，省得每个方法都传 8 个参数。</summary>
    private sealed record GenContext(
        MesDbContext Db,
        List<ProcessStep> Steps,
        List<Product> Products,
        List<ProductionLine> Lines,
        List<FailStep> FailSteps,
        Random Rnd,
        DateTime Now);

    private static async Task<(int Input, int Completed, int Fpy)> SnapshotAsync(MesDbContext db, CancellationToken ct)
    {
        var input = await db.Units.CountAsync(ct);
        var completed = await db.Units.CountAsync(u => u.Status == nameof(UnitStatus.Completed), ct);
        var failedUnitIds = db.MoveLogs.Where(l => l.Result == "Fail").Select(l => l.SerialUnitId).Distinct();
        var fpy = await db.Units.CountAsync(u => u.Status == nameof(UnitStatus.Completed) && !failedUnitIds.Contains(u.Id), ct);
        return (input, completed, fpy);
    }

    private static SnMoveLog NewLog(
        SerialUnit unit, WorkOrder order, ProcessStep step, int seq, DateTime atUtc,
        string stationCode, int operatorId, string operatorName, Random rnd) => new()
        {
            SerialUnit = unit,
            WorkOrderId = order.Id,
            LineId = order.LineId,
            StepId = step.Id,
            Seq = seq,
            Result = "Pass",
            OperatorId = operatorId,
            OperatorName = operatorName,
            StationCode = stationCode,
            ProductionDate = FactoryClock.DateOf(atUtc),
            ProductionHour = FactoryClock.HourOf(atUtc),
            Shift = FactoryClock.ShiftOf(atUtc),
            DurationSeconds = rnd.Next(25, 120),
            CreatedAtUtc = atUtc
        };

    private static (int Id, string Name) PickOperator(Random rnd)
    {
        var i = rnd.Next(OperatorNames.Length);
        return (OperatorIds[i], OperatorNames[i]);
    }

    private static string LinePrefix(int lineId, List<ProductionLine> lines)
    {
        var line = lines.FirstOrDefault(l => l.Id == lineId);
        return line is null ? "A" : line.Code[^1..];
    }

    private static void Shuffle<T>(IList<T> list, Random rnd)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rnd.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static double Pct(int part, int total) => total == 0 ? 0 : Math.Round(part * 100d / total, 1);
}
