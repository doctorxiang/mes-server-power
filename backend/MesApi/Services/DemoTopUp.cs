using MesApi.Data;
using MesApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

/// <summary>补数结果：接口直接回给前端/命令行，方便核对"补了多少、计划与发料差多少"。</summary>
public record DemoTopUpResult(
    int OrdersCreated,
    int UnitsCreated,
    int LogsCreated,
    int RepairsCreated,
    int CompletedOrders,
    int PlanQty,
    int IssuedQty,
    double YieldBefore,
    double YieldAfter,
    double FpyAfter,
    string Summary);

/// <summary>单台的"命运"：一次纯净走完 / 判不良返修救回 / 判不良直接报废 / 还在中间工序 / 卡在维修站。</summary>
internal enum UnitFate
{
    Clean,      // 一次过，没判过不良
    Repaired,   // 判过不良，进维修站修好，回原工序重测通过 → 计入直通率但不算 FPY
    Scrap,      // 判过不良，修不好直接报废 → 吃冗余料
    Wip,        // 走到中间工序，还在制
    Hold        // 判了不良，正在维修站里，等出站结论
}

/// <summary>
/// 演示数据生成器 —— 按"真实工单的物料闭环"造数，不是随便凑数字：
///
///   计划 120 台（客户要的良品）
///   → 发料 142 套（120 ÷ 目标直通率 0.85，多备 22 套冗余料）
///   → 上线 142 台（每台一个 SN）
///   → 完工 120 台（正好等于计划，进度 100%）
///   → 报废 22 台（冗余料全被报废吃掉，不用补料）
///
/// 如果报废 > 冗余（不良偏高），工单就会出现"需补料 N 套"，必须走
/// POST /api/work-orders/{id}/replenish 追加发料才能继续上线 —— 这就是补料业务。
///
/// 两种入口：
///   RunAsync(reset: true)  → 清空生产数据重建一套（演示库用，--reset-demo / POST /api/demo/reset）
///   RunAsync(reset: false) → 幂等补齐（已存在的单号跳过，--topup-demo / POST /api/demo/topup）
/// </summary>
public static class DemoTopUp
{
    /// <summary>目标直通率：完工 ÷ 投料。发料量就是拿它反推出来的。</summary>
    public const double TargetYield = MaterialPlanning.TargetYield;

    /// <summary>完工品里有多少是返修救回来的（决定 FPY 与直通率的差）。</summary>
    private const double RepairShare = 0.08;

    /// <summary>不良分布权重：越靠前越常见，柏拉图前 80% 才有意义。</summary>
    private static readonly string[] DefectPriority =
    [
        "WELD-01", "VIS-01", "ELE-05", "ELE-02", "WELD-02",
        "ELE-04", "VIS-04", "WELD-03", "VIS-03", "ELE-03", "VIS-05", "PKG-01", "PKG-02"
    ];

    private static readonly string[] OperatorNames = ["张小明", "刘芳", "王强"];
    private static readonly int[] OperatorIds = [4, 5, 4];
    private static readonly string[] RepairActions = ["Replace", "Resolder", "Adjust", "Other"];

    /// <summary>历史已完工工单的计划量：都是 20 的倍数，120÷0.85=141 → 85.1%，接近整数好核对。</summary>
    private static readonly int[] HistoryPlans = [120, 140, 100, 160, 120, 100, 140, 160, 120, 160, 100, 140];

    public static async Task<DemoTopUpResult> RunAsync(
        MesDbContext db, ILogger logger, bool reset = false, CancellationToken ct = default)
    {
        var rnd = new Random(20261112);
        var steps = await db.Steps.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct);
        var products = await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
        var lines = await db.Lines.AsNoTracking().OrderBy(l => l.Id).ToListAsync(ct);
        var defectPool = await db.DefectCodes.AsNoTracking().OrderBy(d => d.Id).ToListAsync(ct);
        var defectBag = BuildDefectBag(defectPool);
        var now = DateTime.UtcNow;

        if (steps.Count == 0 || products.Count == 0 || lines.Count == 0)
            throw new InvalidOperationException("主数据为空，请先完成初始化种子数据");

        if (reset) await ResetAsync(db, ct);

        var (input0, done0, _) = await SnapshotAsync(db, ct);

        var inspectionSeqs = steps
            .Select((s, i) => (s, Seq: i + 1))
            .Where(x => x.s.Kind is StepKinds.Inspection or StepKinds.Test)
            .Select(x => x.Seq).ToArray();

        // 没有检验/测试工序的路线就退化成"任意工序都可能判不良"，避免随机取空数组
        if (inspectionSeqs.Length == 0)
            inspectionSeqs = Enumerable.Range(1, steps.Count).ToArray();

        var ctx = new GenContext(db, steps, products, lines, defectBag, inspectionSeqs, rnd, now);

        var history = await AddCompletedOrdersAsync(ctx, ct);
        var wip = await AddInProductionOrdersAsync(ctx, ct);
        await AddOpenExceptionsAsync(ctx, ct);

        var (input1, done1, fpy1) = await SnapshotAsync(db, ct);

        var ordersCreated = history.Orders + wip.Orders;
        var unitsCreated = history.Units + wip.Units;
        var logsCreated = history.Logs + wip.Logs;
        var repairsCreated = history.Repairs + wip.Repairs;

        var summary = $"新增 {ordersCreated} 张工单（{wip.Orders} 张在制 + {history.Orders} 张已完工）/ " +
                      $"{unitsCreated} 台 SN / {logsCreated} 条过站流水 / {repairsCreated} 条维修记录；" +
                      $"在制单需补料 {wip.ReplenishNeed} 套";

        logger.LogInformation("演示数据生成完成：{Summary}；整体直通率 {Before}% → {After}%，FPY {Fpy}%",
            summary, Pct(done0, input0), Pct(done1, input1), Pct(fpy1, input1));

        return new DemoTopUpResult(ordersCreated, unitsCreated, logsCreated, repairsCreated,
            history.Orders, history.PlanQty, history.IssuedQty,
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

    // ---------------- 1. 历史已完工工单：完工 == 计划 ----------------

    private static async Task<(int Orders, int Units, int Logs, int Repairs, int PlanQty, int IssuedQty)> AddCompletedOrdersAsync(
        GenContext ctx, CancellationToken ct)
    {
        var db = ctx.Db;
        var existing = (await db.WorkOrders.Select(w => w.OrderNo).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int orders = 0, units = 0, logs = 0, repairs = 0, planSum = 0, issuedSum = 0;

        for (var day = HistoryPlans.Length; day >= 1; day--)
        {
            var localDate = FactoryClock.NowLocal.Date.AddDays(-day);
            var orderNo = $"WO{localDate:yyyyMMdd}001";
            if (existing.Contains(orderNo)) continue;   // 幂等：这张单已经有了就跳过

            var idx = HistoryPlans.Length - day;
            var plan = HistoryPlans[idx];
            var issued = MaterialPlanning.IssuedFor(plan);   // 120 → 141，多备 21 套
            var scrapQty = issued - plan;                    // 冗余料全部被报废吃掉
            var repairedQty = (int)Math.Round(plan * RepairShare, MidpointRounding.AwayFromZero);

            var product = ctx.Products[idx % ctx.Products.Count];
            var line = ctx.Lines[idx % ctx.Lines.Count];
            var prefix = LinePrefix(line.Id, ctx.Lines);

            var dateStr = localDate.ToString("yyyy-MM-dd");
            var dayStartUtc = FactoryClock.StartOfLocalDayUtc(dateStr);
            var startUtc = dayStartUtc.AddHours(idx % 3 == 0 ? 20 : 8);   // 每第 3 张排夜班，班次统计才有对比
            var spacingSec = Math.Max(45, (int)(2.9 * 3600 / issued));     // 整批约 3 小时做完，不跨天

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
                Remark = $"历史完工（演示数据）· 计划 {plan} / 发料 {issued}（冗余 {scrapQty}）"
            };
            db.WorkOrders.Add(order);
            await db.SaveChangesAsync(ct);   // 先落库拿自增 Id，后面的 unit / log 才有正确外键
            orders++;
            planSum += plan;
            issuedSum += issued;

            var logBag = new List<SnMoveLog>(issued * ctx.Steps.Count + 16);
            var repairBag = new List<RepairRecord>(repairedQty);

            for (var i = 0; i < issued; i++)
            {
                // 前 plan 台是良品（其中一小截是返修救回来的），后面 issued-plan 台全部报废
                var fate = i < plan
                    ? (i < repairedQty ? UnitFate.Repaired : UnitFate.Clean)
                    : UnitFate.Scrap;

                var startAt = startUtc.AddSeconds((long)i * spacingSec + ctx.Rnd.Next(0, 20));
                EmitUnit(ctx, order, line, $"{orderNo}-{i + 1:0000}", startAt, fate, 0, logBag, repairBag);
                units++;
            }

            db.MoveLogs.AddRange(logBag);
            if (repairBag.Count > 0) db.RepairRecords.AddRange(repairBag);
            await db.SaveChangesAsync(ct);   // 逐单提交，内存与事务都不会爆

            logs += logBag.Count;
            repairs += repairBag.Count;
        }

        return (orders, units, logs, repairs, planSum, issuedSum);
    }

    // ---------------- 2. 在制工单：发料已备好，料还没用完 ----------------

    /// <param name="Plan">计划良品数</param>
    /// <param name="Completed">已完工台数（&lt; 计划，所以进度不到 100%）</param>
    /// <param name="Scrap">已报废</param>
    /// <param name="Hold">卡在维修站</param>
    /// <param name="Wip">还在工序之间</param>
    /// <param name="WindowMinutes">这批单已经开了多久</param>
    private sealed record WipSpec(int Plan, int Completed, int Scrap, int Hold, int Wip, int WindowMinutes);

    private static async Task<(int Orders, int Units, int Logs, int Repairs, int ReplenishNeed)> AddInProductionOrdersAsync(
        GenContext ctx, CancellationToken ct)
    {
        var db = ctx.Db;
        var today = FactoryClock.NowLocal.ToString("yyyyMMdd");

        // 三张在制单，故意造出三种现场：
        //   1) 顺利单：报废在冗余以内，不用补料，进度 93%
        //   2) 大批量单：有 WIP 和隔离品，进度 79%
        //   3) 异常单：不良偏高，报废 30 套 > 冗余 21 套 → 需补料 9 套（前端会亮红）
        var specs = new[]
        {
            new WipSpec(300, 280, 38, 0, 0, 360),
            new WipSpec(500, 396, 55, 5, 3, 300),
            new WipSpec(120, 93, 30, 2, 2, 200)
        };

        int orders = 0, units = 0, logs = 0, repairs = 0, replenishNeed = 0;

        for (var k = 0; k < specs.Length; k++)
        {
            var spec = specs[k];
            var orderNo = $"WO{today}{k + 1:000}";
            if (await db.WorkOrders.AnyAsync(w => w.OrderNo == orderNo, ct)) continue;   // 幂等

            var order = await FindOrCreateWipOrderAsync(db, orderNo, k, spec, ct);
            if (order is null) continue;

            var product = ctx.Products.First(p => p.Id == order.ProductId);
            var line = ctx.Lines.First(l => l.Id == order.LineId);
            var prefix = LinePrefix(line.Id, ctx.Lines);

            var issued = MaterialPlanning.EffectiveIssued(order);
            var started = spec.Completed + spec.Scrap + spec.Hold + spec.Wip;
            replenishNeed += MaterialPlanning.ReplenishNeed(order.PlanQty, issued, spec.Scrap);

            // 时间轴：从 WindowMinutes 前铺到 45 分钟前，保证最后几台的流水不会跑到"未来"
            var startBase = ctx.Now.AddMinutes(-spec.WindowMinutes);
            var spanMin = Math.Max(20, spec.WindowMinutes - 45);
            var stepMin = (double)spanMin / Math.Max(1, started);

            var fates = new List<UnitFate>(started);
            fates.AddRange(Enumerable.Repeat(UnitFate.Clean, spec.Completed - (int)Math.Round(spec.Completed * RepairShare)));
            fates.AddRange(Enumerable.Repeat(UnitFate.Repaired, (int)Math.Round(spec.Completed * RepairShare)));
            fates.AddRange(Enumerable.Repeat(UnitFate.Scrap, spec.Scrap));
            fates.AddRange(Enumerable.Repeat(UnitFate.Hold, spec.Hold));
            fates.AddRange(Enumerable.Repeat(UnitFate.Wip, spec.Wip));
            Shuffle(fates, ctx.Rnd);

            var logBag = new List<SnMoveLog>(started * ctx.Steps.Count);
            var repairBag = new List<RepairRecord>(spec.Hold + spec.Completed);

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
            orders++;
        }

        return (orders, units, logs, repairs, replenishNeed);
    }

    /// <summary>
    /// 在制单：优先接管库里已有的同号单（老数据 / SeedData 建的），
    /// 没有就按新口径新建，并把发料量补齐成"计划 + 冗余"。
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
            IssuedQty = MaterialPlanning.IssuedFor(spec.Plan),
            Status = nameof(WorkOrderStatus.InProduction),
            Priority = index == 0 ? 1 : 2,
            PlannedStartUtc = DateTime.UtcNow.AddMinutes(-spec.WindowMinutes),
            DueDateUtc = DateTime.UtcNow.AddDays(index == 2 ? 1 : 2),
            CreatedByUserId = 2,
            ReleasedAtUtc = DateTime.UtcNow.AddMinutes(-spec.WindowMinutes),
            Remark = index == 2 ? "AI 服务器配套 · 不良偏高已申请补料" : null
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

        // 判不良的工序：返修/报废/隔离都会在某个检验或测试工序被判出来
        var failSeq = fate is UnitFate.Clean or UnitFate.Wip
            ? 0
            : ctx.InspectionSeqs[ctx.Rnd.Next(ctx.InspectionSeqs.Length)];

        var lastSeq = fate == UnitFate.Wip ? Math.Clamp(stopSeq, 1, ctx.Steps.Count) : ctx.Steps.Count;

        for (var seq = 1; seq <= lastSeq; seq++)
        {
            var step = ctx.Steps[seq - 1];
            var station = $"{prefix}-{seq:00}";
            var (opId, opName) = PickOperator(ctx.Rnd);
            cursor = cursor.AddSeconds(ctx.Rnd.Next(40, 110));

            if (seq == failSeq)
            {
                var defect = PickDefect(ctx.DefectBag, ctx.Rnd);
                var failLog = NewLog(unit, order, step, seq, cursor, station, opId, opName, ctx.Rnd);
                failLog.Result = "Fail";
                failLog.DefectCodeId = defect.Id;
                failLog.Remark = defect.Name;
                logBag.Add(failLog);

                unit.LastScanAtUtc = cursor;
                unit.CurrentSeq = seq;
                unit.CurrentStepId = step.Id;

                if (fate == UnitFate.Scrap)
                {
                    // 报废件不进维修站：判不良 → 直接报废，冗余料在这里被吃掉
                    unit.Status = nameof(UnitStatus.Scrapped);
                    return;
                }

                if (fate == UnitFate.Hold)
                {
                    // 还在维修站里：维修记录保持 InProgress，维修站的待办队列才有东西
                    unit.Status = nameof(UnitStatus.Hold);
                    repairBag.Add(NewRepair(ctx, order, unit, step, defect, prefix,
                        status: "InProgress", checkoutBy: null,
                        startedAt: cursor.AddMinutes(5), completedAt: null));
                    return;
                }

                // 返修救回：进站 → 出站 → 回原工序重测 → 继续往后走（所以计入直通率，但不计入 FPY）
                var repairStart = cursor.AddMinutes(ctx.Rnd.Next(10, 40));
                var repairedAt = repairStart.AddMinutes(ctx.Rnd.Next(15, 45));
                repairBag.Add(NewRepair(ctx, order, unit, step, defect, prefix,
                    status: "Repaired", checkoutBy: "李维修",
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
        string prefix, string status, string? checkoutBy,
        DateTime startedAt, DateTime? completedAt) => new()
        {
            SerialUnit = unit,
            WorkOrderId = order.Id,
            RepairNo = 1,
            SourceStepId = step.Id,
            DefectCodeId = defect.Id,
            Action = RepairActions[ctx.Rnd.Next(RepairActions.Length)],
            Status = status,
            Remark = $"{step.Name}判{defect.Name}",
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
                Description = "1200W 机种散热片来料尺寸超差，已隔离待 MRB 判定（需补料评估）",
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

    // ---------------- 工具 ----------------

    /// <summary>一趟生成要用的东西打个包，省得每个方法都传 8 个参数。</summary>
    internal sealed record GenContext(
        MesDbContext Db,
        List<ProcessStep> Steps,
        List<Product> Products,
        List<ProductionLine> Lines,
        List<DefectCode> DefectBag,
        int[] InspectionSeqs,
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

    private static List<DefectCode> BuildDefectBag(List<DefectCode> pool)
    {
        var bag = new List<DefectCode>(64);
        for (var i = 0; i < DefectPriority.Length; i++)
        {
            var d = pool.FirstOrDefault(x => x.Code == DefectPriority[i]);
            if (d is null) continue;
            for (var w = 0; w < Math.Max(1, 13 - i); w++) bag.Add(d);
        }
        return bag.Count > 0 ? bag : pool;
    }

    private static DefectCode PickDefect(List<DefectCode> bag, Random rnd) => bag[rnd.Next(bag.Count)];

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
