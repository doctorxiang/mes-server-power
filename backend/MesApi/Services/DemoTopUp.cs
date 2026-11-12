using MesApi.Data;
using MesApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

/// <summary>补数结果：接口直接回给前端/命令行，方便核对"补了多少、良率变化多少"。</summary>
public record DemoTopUpResult(
    int OrdersCreated,
    int UnitsCreated,
    int LogsCreated,
    int RepairsCreated,
    int WipCompleted,
    double YieldBefore,
    double YieldAfter,
    double FpyAfter,
    string Summary);

/// <summary>
/// 演示数据"热更新"补数器 —— 不建库、不停机、不跑迁移，直接往运行中的库里追加数据。
///
/// 两件事：
/// 1) 收尾：把所有在制工单里还卡在中间的 SN 补齐剩余工序，使完工数达到投入的 85%；
/// 2) 追加：过去 12 天每天一张"已完工"工单（含完整 9 道工序流水、返修记录、隔离/报废品），
///    每张的完工率都是 85%，返修率约 6%，所以整体直通率落在 85% 附近、FPY 落在 79% 附近。
///
/// 幂等性：完工数已达标就跳过；OrderNo 已存在就跳过。重复调用不会产生脏数据。
/// </summary>
public static class DemoTopUp
{
    /// <summary>目标直通率：完工 ÷ 投入。</summary>
    public const double TargetYield = 0.85;

    private const double RepairShare = 0.08;      // 完工品里有多少是返修救回来的（决定 FPY 与直通率的差）

    /// <summary>不良分布权重：越靠前越常见，柏拉图前 80% 才有意义。</summary>
    private static readonly string[] DefectPriority =
    [
        "WELD-01", "VIS-01", "ELE-05", "ELE-02", "WELD-02",
        "ELE-04", "VIS-04", "WELD-03", "VIS-03", "ELE-03", "VIS-05", "PKG-01", "PKG-02"
    ];

    private static readonly string[] OperatorNames = ["张小明", "刘芳", "王强"];
    private static readonly int[] OperatorIds = [4, 5, 4];
    private static readonly string[] RepairActions = ["Replace", "Resolder", "Adjust", "Other"];

    public static async Task<DemoTopUpResult> RunAsync(MesDbContext db, ILogger logger, CancellationToken ct = default)
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

        var (input0, done0, _) = await SnapshotAsync(db, ct);

        var (wipCompleted, wipLogs) = await FinishWipAsync(db, steps, lines, rnd, now, ct);
        var created = await AddCompletedOrdersAsync(db, steps, products, lines, defectBag, rnd, now, ct);

        var (input1, done1, fpy1) = await SnapshotAsync(db, ct);

        var summary = $"新增 {created.Orders} 张已完工工单 / {created.Units} 台 SN / {created.Logs + wipLogs} 条过站流水 / {created.Repairs} 条维修记录；在制收尾 {wipCompleted} 台";
        logger.LogInformation("演示补数完成：{Summary}；整体直通率 {Before}% → {After}%，FPY {Fpy}%",
            summary, Pct(done0, input0), Pct(done1, input1), Pct(fpy1, input1));

        return new DemoTopUpResult(created.Orders, created.Units, created.Logs + wipLogs, created.Repairs, wipCompleted,
            Pct(done0, input0), Pct(done1, input1), Pct(fpy1, input1), summary);
    }

    // ---------------- 1. 在制收尾 ----------------

    /// <summary>
    /// 把在制工单里"已经流过一半"的 SN 补完剩余工序。
    /// 只补到完工数 = 投入 × 85% 为止，剩下的故意留在制，保证看板的 WIP 数字不为 0。
    /// </summary>
    private static async Task<(int Units, int Logs)> FinishWipAsync(
        MesDbContext db, List<ProcessStep> steps, List<ProductionLine> lines,
        Random rnd, DateTime now, CancellationToken ct)
    {
        var logsWritten = 0;
        var orders = await db.WorkOrders
            .Where(w => w.Status == nameof(WorkOrderStatus.InProduction))
            .OrderBy(w => w.Id).ToListAsync(ct);

        var done = 0;

        // 第一轮：每张在制单补到"完工数 = 投入 × 85%"
        foreach (var order in orders)
        {
            var units = await db.Units.Where(u => u.WorkOrderId == order.Id).ToListAsync(ct);
            if (units.Count == 0) continue;

            var target = (int)Math.Round(units.Count * TargetYield, MidpointRounding.AwayFromZero);
            var need = target - units.Count(u => u.Status == nameof(UnitStatus.Completed));
            if (need <= 0) continue;   // 已达标 → 跳过，保证幂等

            // 优先补"离终点最近"的，符合流水线按序出站的直觉
            var candidates = units
                .Where(u => u.Status == nameof(UnitStatus.InProcess))
                .OrderByDescending(u => u.CurrentSeq).ThenBy(u => u.Id)
                .Take(need).ToList();

            var (u, l) = CompleteUnits(db, candidates, order, steps, LinePrefix(order.LineId, lines), rnd, now);
            done += u;
            logsWritten += l;
        }

        if (done > 0) await db.SaveChangesAsync(ct);

        // 第二轮：全局校准。库里还可能有测试脚本顺手建的小单（投入 2 台、完工 1 台），
        // 它们会把整体直通率拉低零点几个点，所以这里按整体口径再补几台在制品补齐。
        var (input, completed, _) = await SnapshotAsync(db, ct);
        var gap = (int)Math.Ceiling(input * TargetYield) - completed;
        if (gap <= 0) return (done, logsWritten);

        var extra = await db.Units.Include(u => u.WorkOrder)
            .Where(u => u.Status == nameof(UnitStatus.InProcess)
                        && u.WorkOrder!.Status == nameof(WorkOrderStatus.InProduction))
            .OrderBy(u => u.LastScanAtUtc)
            .Take(gap)
            .ToListAsync(ct);

        foreach (var unit in extra)
        {
            var order = unit.WorkOrder!;
            var (u, l) = CompleteUnits(db, [unit], order, steps, LinePrefix(order.LineId, lines), rnd, now);
            done += u;
            logsWritten += l;
        }

        await db.SaveChangesAsync(ct);
        return (done, logsWritten);
    }

    /// <summary>把一批 SN 从当前工序一路 Pass 到最后一站并置为完工，返回完成台数与流水条数。</summary>
    private static (int Units, int Logs) CompleteUnits(
        MesDbContext db, List<SerialUnit> units, WorkOrder order, List<ProcessStep> steps,
        string linePrefix, Random rnd, DateTime now)
    {
        if (units.Count == 0) return (0, 0);

        var logs = new List<SnMoveLog>(units.Count * steps.Count);
        var count = 0;

        foreach (var unit in units)
        {
            var cursor = unit.LastScanAtUtc;

            for (var seq = Math.Clamp(unit.CurrentSeq, 1, steps.Count); seq <= steps.Count; seq++)
            {
                cursor = Min(cursor.AddSeconds(rnd.Next(30, 130)), now);
                var (opId, opName) = PickOperator(rnd);
                logs.Add(NewLog(unit, order, steps[seq - 1], seq, cursor, $"{linePrefix}-{seq:00}", opId, opName, rnd));
            }

            unit.Status = nameof(UnitStatus.Completed);
            unit.CurrentSeq = steps.Count;
            unit.CurrentStepId = steps[^1].Id;
            unit.CompletedAtUtc = cursor;
            unit.LastScanAtUtc = cursor;
            unit.Version++;
            count++;
        }

        db.MoveLogs.AddRange(logs);
        return (count, logs.Count);
    }

    // ---------------- 2. 追加已完工工单 ----------------

    private static async Task<(int Orders, int Units, int Logs, int Repairs)> AddCompletedOrdersAsync(
        MesDbContext db, List<ProcessStep> steps, List<Product> products, List<ProductionLine> lines,
        List<DefectCode> defectBag, Random rnd, DateTime now, CancellationToken ct)
    {
        // 12 张历史工单，投入量错开，且都是 20 的倍数 → 85% 正好整除，不做四舍五入误差
        int[] planQtys = [120, 140, 100, 160, 120, 100, 140, 160, 120, 160, 100, 140];

        var inspectionSeqs = steps
            .Select((s, i) => (s, Seq: i + 1))
            .Where(x => x.s.Kind is StepKinds.Inspection or StepKinds.Test)
            .Select(x => x.Seq).ToArray();

        var existing = await db.WorkOrders.Select(w => w.OrderNo).ToListAsync(ct);
        var existingSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        int ordersCreated = 0, unitsCreated = 0, logsCreated = 0, repairsCreated = 0;

        for (var day = planQtys.Length; day >= 1; day--)
        {
            var localDate = FactoryClock.NowLocal.Date.AddDays(-day);
            var dateStr = localDate.ToString("yyyy-MM-dd");
            var orderNo = $"WO{localDate:yyyyMMdd}001";
            if (existingSet.Contains(orderNo)) continue;   // 幂等

            var product = products[(planQtys.Length - day) % products.Count];
            var line = lines[(planQtys.Length - day) % lines.Count];
            var n = planQtys[planQtys.Length - day];

            // 单 300 台按 85% 完工 → 255 台；剩下 15% 走报废（已完工的工单不该留着长期待维修的陈货，
            // 否则维修站队列里全是十几天前的隔离品，业务上说不通）
            var completedQty = (int)Math.Round(n * TargetYield, MidpointRounding.AwayFromZero);
            var repairedQty = (int)Math.Round(completedQty * RepairShare, MidpointRounding.AwayFromZero);

            var dayStartUtc = FactoryClock.StartOfLocalDayUtc(dateStr);
            var startUtc = dayStartUtc.AddHours(day % 3 == 0 ? 20 : 8);   // 每第 3 张排夜班，班次统计才有对比
            var spacingSec = Math.Max(45, (int)(2.9 * 3600 / n));         // 整批约 3 小时做完，不跨天

            var order = new WorkOrder
            {
                OrderNo = orderNo,
                ProductId = product.Id,
                LineId = line.Id,
                PlanQty = n,
                Status = nameof(WorkOrderStatus.Completed),
                Priority = 2,
                PlannedStartUtc = startUtc,
                DueDateUtc = startUtc.AddDays(1),
                CreatedByUserId = 2,
                CreatedAtUtc = startUtc.AddDays(-1),
                ReleasedAtUtc = startUtc.AddHours(-2),
                CompletedAtUtc = startUtc.AddHours(4),
                Remark = "历史完工（演示数据）"
            };
            db.WorkOrders.Add(order);
            await db.SaveChangesAsync(ct);   // 先落库拿自增 Id，后面 unit / log 才有正确外键
            ordersCreated++;

            var prefix = LinePrefix(line.Id, lines);
            var logs = new List<SnMoveLog>(n * steps.Count + 16);
            var repairs = new List<RepairRecord>(repairedQty);

            for (var i = 0; i < n; i++)
            {
                var sn = $"{orderNo}-{i + 1:0000}";
                var unitStart = startUtc.AddSeconds((long)i * spacingSec + rnd.Next(0, 20));

                var kind = i < completedQty
                    ? (i < repairedQty ? "repaired" : "clean")
                    : "scrap";

                var failSeq = kind == "clean" ? 0 : inspectionSeqs[rnd.Next(inspectionSeqs.Length)];

                var unit = new SerialUnit
                {
                    Sn = sn,
                    WorkOrder = order,
                    WorkOrderId = order.Id,
                    ProductId = product.Id,
                    LineId = line.Id,
                    CurrentSeq = 1,
                    Status = nameof(UnitStatus.InProcess),
                    ProductionDate = FactoryClock.DateOf(unitStart),
                    Shift = FactoryClock.ShiftOf(unitStart),
                    CreatedAtUtc = unitStart,
                    LastScanAtUtc = unitStart
                };
                db.Units.Add(unit);
                unitsCreated++;

                var cursor = unitStart;
                var broken = false;

                for (var seq = 1; seq <= steps.Count; seq++)
                {
                    var step = steps[seq - 1];
                    var (opId, opName) = PickOperator(rnd);
                    var station = $"{prefix}-{seq:00}";

                    if (seq == failSeq)
                    {
                        var defect = PickDefect(defectBag, rnd);
                        cursor = cursor.AddSeconds(rnd.Next(40, 110));
                        var failLog = NewLog(unit, order, step, seq, cursor, station, opId, opName, rnd);
                        failLog.Result = "Fail";
                        failLog.DefectCodeId = defect.Id;
                        failLog.Remark = defect.Name;
                        logs.Add(failLog);

                        unit.LastScanAtUtc = cursor;
                        unit.CurrentSeq = seq;
                        unit.CurrentStepId = step.Id;

                        if (kind == "scrap")
                        {
                            // 报废件不进维修站：判不良 → 直接报废，不留"维修中"陈货
                            unit.Status = nameof(UnitStatus.Scrapped);
                            broken = true;
                            break;
                        }

                        // 返修救回：进维修站 → 出站 → 回原工序重测 → 继续往后走
                        var repairStart = cursor.AddMinutes(rnd.Next(10, 40));
                        var repairedAt = repairStart.AddMinutes(rnd.Next(15, 45));
                        repairs.Add(new RepairRecord
                        {
                            SerialUnit = unit,
                            WorkOrderId = order.Id,
                            RepairNo = 1,
                            SourceStepId = step.Id,
                            DefectCodeId = defect.Id,
                            Action = RepairActions[rnd.Next(RepairActions.Length)],
                            Status = "Repaired",
                            Remark = $"{step.Name}判{defect.Name}",
                            OperatorId = 6,
                            OperatorName = "李维修",
                            StationCode = $"{prefix}-RP",
                            CheckoutByName = "李维修",
                            StartedAtUtc = repairStart,
                            CompletedAtUtc = repairedAt
                        });

                        cursor = repairedAt.AddMinutes(rnd.Next(2, 12));
                        logs.Add(NewLog(unit, order, step, seq, cursor, station, opId, opName, rnd));
                        continue;
                    }

                    cursor = cursor.AddSeconds(rnd.Next(40, 110));
                    logs.Add(NewLog(unit, order, step, seq, cursor, station, opId, opName, rnd));
                    unit.CurrentSeq = seq;
                    unit.CurrentStepId = step.Id;
                    unit.LastScanAtUtc = cursor;
                }

                if (!broken)
                {
                    unit.Status = nameof(UnitStatus.Completed);
                    unit.CurrentSeq = steps.Count;
                    unit.CurrentStepId = steps[^1].Id;
                    unit.CompletedAtUtc = cursor;
                }
            }

            db.MoveLogs.AddRange(logs);
            if (repairs.Count > 0) db.RepairRecords.AddRange(repairs);

            await db.SaveChangesAsync(ct);   // 逐单提交，内存与事务都不会爆

            logsCreated += logs.Count;
            repairsCreated += repairs.Count;
        }

        return (ordersCreated, unitsCreated, logsCreated, repairsCreated);
    }

    // ---------------- 工具 ----------------

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

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static double Pct(int part, int total) => total == 0 ? 0 : Math.Round(part * 100d / total, 1);
}
