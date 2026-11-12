using MesApi.Data;
using MesApi.Domain;
using MesApi.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MesApi.Services;

/// <summary>
/// 服务器电源产线的初始主数据 + 演示数据。
/// 只在空库时执行一次；演示数据用于让看板/追溯开箱就有内容。
/// </summary>
public static class SeedData
{
    public static async Task EnsureSeededAsync(MesDbContext db, IConfiguration config, ILogger logger, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct))
            return;

        var now = DateTime.UtcNow;

        // ---------- 账号 ----------
        var adminPwd = config["Seed:AdminPassword"] ?? "Admin@12345";
        db.Users.AddRange(
            NewUser("admin", "系统管理员", "A0001", Roles.Admin, adminPwd),
            NewUser("planner", "李计划", "P0001", Roles.Planner, "Planner@123"),
            NewUser("leader", "王班长", "L0001", Roles.Leader, "Leader@123"),
            NewUser("operator1", "张小明", "O0001", Roles.Operator, "Operator@123"),
            NewUser("operator2", "刘芳", "O0002", Roles.Operator, "Operator@123"),
            NewUser("qc1", "陈质检", "Q0001", Roles.Qc, "Qc@12345678")
        );

        // ---------- 产线 ----------
        var lineA = new ProductionLine { Code = "LINE-A", Name = "电源一线（CRPS）" };
        var lineB = new ProductionLine { Code = "LINE-B", Name = "电源二线（大功率）" };
        db.Lines.AddRange(lineA, lineB);

        // ---------- 工序（服务器电源实际工艺） ----------
        var stepDefs = new (string Code, string Name, string Kind, bool RequireDefect, bool IsKey, int Seconds)[]
        {
            ("PLUGIN",  "插件",     StepKinds.Normal,     false, true,  60),
            ("WAVE",    "过锡炉",   StepKinds.Normal,     false, true,  45),
            ("TOUCHUP", "补焊剪脚", StepKinds.Normal,     false, false, 40),
            ("VISUAL",  "目视检查", StepKinds.Inspection, true,  true,  35),
            ("ICT",     "ICT测试",  StepKinds.Test,       true,  false, 50),
            ("BURNIN",  "老化测试", StepKinds.Test,       true,  true,  90),
            ("HIPOT",   "耐压测试", StepKinds.Test,       true,  true,  30),
            ("FCT",     "功能测试", StepKinds.Test,       true,  false, 45),
            ("PACK",    "包装",     StepKinds.Packing,    false, false, 40)
        };

        var steps = stepDefs.Select(s => new ProcessStep
        {
            Code = s.Code,
            Name = s.Name,
            Kind = s.Kind,
            RequireDefect = s.RequireDefect,
            IsKey = s.IsKey,
            TaktSeconds = s.Seconds
        }).ToList();
        db.Steps.AddRange(steps);

        // ---------- 产品 ----------
        var p800 = new Product { Code = "SPS-800W", Name = "800W CRPS 服务器电源", StandardCycleSeconds = 45 };
        var p1200 = new Product { Code = "SPS-1200W", Name = "1200W CRPS 服务器电源", StandardCycleSeconds = 55 };
        var p2000 = new Product { Code = "SPS-2000W", Name = "2000W 冗余电源模块", StandardCycleSeconds = 70 };
        db.Products.AddRange(p800, p1200, p2000);

        // ---------- 不良代码 ----------
        var defects = new (string Code, string Name, string Category)[]
        {
            ("VIS-01", "缺件", "外观"), ("VIS-02", "错件", "外观"), ("VIS-03", "极性反", "外观"),
            ("VIS-04", "元件浮高", "外观"), ("VIS-05", "外壳划伤", "外观"),
            ("WELD-01", "虚焊", "焊接"), ("WELD-02", "连锡", "焊接"), ("WELD-03", "锡珠", "焊接"), ("WELD-04", "漏焊", "焊接"),
            ("ELE-01", "无输出", "电气"), ("ELE-02", "输出电压超差", "电气"), ("ELE-03", "耐压不良", "电气"),
            ("ELE-04", "老化掉电", "电气"), ("ELE-05", "纹波超标", "电气"),
            ("PKG-01", "标签错误", "包装"), ("PKG-02", "包装破损", "包装"),
            ("MAT-01", "来料不良", "来料")
        };
        var defectEntities = defects.Select(d => new DefectCode { Code = d.Code, Name = d.Name, Category = d.Category }).ToList();
        db.DefectCodes.AddRange(defectEntities);

        await db.SaveChangesAsync(ct);

        // ---------- 工艺路线 ----------
        foreach (var product in new[] { p800, p1200, p2000 })
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                db.ProductRoutes.Add(new ProductRoute
                {
                    ProductId = product.Id,
                    StepId = s.Id,
                    Seq = i + 1,
                    StandardSeconds = stepDefs[i].Seconds,
                    IsInspection = s.Kind is StepKinds.Inspection or StepKinds.Test
                });
            }
        }

        // ---------- 工位（每条线每个工序一个扫码点） ----------
        var stations = new List<Workstation>();
        foreach (var (line, prefix) in new[] { (lineA, "A"), (lineB, "B") })
        {
            for (var i = 0; i < steps.Count; i++)
            {
                stations.Add(new Workstation
                {
                    Code = $"{prefix}-{i + 1:00}",
                    Name = $"{steps[i].Name}工位",
                    LineId = line.Id,
                    StepId = steps[i].Id
                });
            }
        }
        // ---------- 维修站（不绑定工序，进出站走维修记录而非工艺路线） ----------
        stations.Add(new Workstation { Code = "A-RP", Name = "一线维修站", LineId = lineA.Id, StepId = null });
        stations.Add(new Workstation { Code = "B-RP", Name = "二线维修站", LineId = lineB.Id, StepId = null });
        db.Workstations.AddRange(stations);

        await db.SaveChangesAsync(ct);

        // ---------- 工单 ----------
        var today = FactoryClock.NowLocal.ToString("yyyyMMdd");
        var orders = new[]
        {
            new WorkOrder
            {
                OrderNo = $"WO{today}001", ProductId = p1200.Id, LineId = lineA.Id, PlanQty = 300,
                IssuedQty = MaterialPlanning.IssuedFor(300),   // 计划 300 → 发料 353（多备 53 套冗余）
                Status = nameof(WorkOrderStatus.InProduction), Priority = 1,
                PlannedStartUtc = now.AddHours(-6), DueDateUtc = now.AddDays(2),
                CreatedByUserId = 2, ReleasedAtUtc = now.AddHours(-6), Remark = "客户加急订单"
            },
            new WorkOrder
            {
                OrderNo = $"WO{today}002", ProductId = p800.Id, LineId = lineA.Id, PlanQty = 500,
                IssuedQty = MaterialPlanning.IssuedFor(500),
                Status = nameof(WorkOrderStatus.InProduction), Priority = 2,
                PlannedStartUtc = now.AddHours(-4), DueDateUtc = now.AddDays(3),
                CreatedByUserId = 2, ReleasedAtUtc = now.AddHours(-4)
            },
            new WorkOrder
            {
                OrderNo = $"WO{today}003", ProductId = p2000.Id, LineId = lineB.Id, PlanQty = 120,
                IssuedQty = MaterialPlanning.IssuedFor(120),
                Status = nameof(WorkOrderStatus.InProduction), Priority = 1,
                PlannedStartUtc = now.AddHours(-3), DueDateUtc = now.AddDays(1),
                CreatedByUserId = 2, ReleasedAtUtc = now.AddHours(-3), Remark = "AI 服务器配套"
            }
        };
        db.WorkOrders.AddRange(orders);
        await db.SaveChangesAsync(ct);

        // ---------- 演示生产数据 ----------
        await SeedProductionAsync(db, orders, steps, defectEntities, now, ct);

        logger.LogWarning("MES 演示数据已初始化：6 个账号 / 2 条产线 / 3 张工单 / 9 道工序。管理员默认密码见 README，请及时修改。");
    }

    private static AppUser NewUser(string userName, string displayName, string employeeNo, string role, string password) => new()
    {
        UserName = userName,
        DisplayName = displayName,
        EmployeeNo = employeeNo,
        Role = role,
        Email = $"{userName}@factory.local",
        PasswordHash = PasswordHasher.Hash(password)
    };

    private static async Task SeedProductionAsync(
        MesDbContext db, WorkOrder[] orders, List<ProcessStep> steps,
        List<DefectCode> defects, DateTime now, CancellationToken ct)
    {
        var rnd = new Random(20261110);
        var operators = new[] { ("张小明", "A-01"), ("刘芳", "A-02"), ("王强", "A-03") };
        var defectPool = defects.Where(d => d.Category != "来料").ToList();

        var units = new List<SerialUnit>();
        var logs = new List<SnMoveLog>();

        var planByOrder = new[] { 96, 140, 42 };

        for (var oi = 0; oi < orders.Length; oi++)
        {
            var order = orders[oi];
            var count = planByOrder[oi];

            for (var i = 0; i < count; i++)
            {
                var sn = $"{order.OrderNo}-{i + 1:0000}";
                // 时间：过去 6 小时均匀分布，模拟连续生产
                var startLocalMinutesAgo = rnd.Next(10, 350);
                var startAt = now.AddMinutes(-startLocalMinutesAgo);

                // 大多数产品已流过若干工序；留一部分在制，形成 WIP
                var targetSeq = Math.Clamp(1 + (int)Math.Round((1 - (double)i / count) * 8), 1, 9);
                if (rnd.Next(100) < 55) targetSeq = 9;              // 55% 已完工
                else if (targetSeq < 5) targetSeq = rnd.Next(1, 6); // 少量在制

                var unit = new SerialUnit
                {
                    Sn = sn,
                    WorkOrderId = order.Id,
                    ProductId = order.ProductId,
                    LineId = order.LineId,
                    CurrentSeq = 1,
                    Status = nameof(UnitStatus.InProcess),
                    ProductionDate = FactoryClock.DateOf(startAt),
                    Shift = FactoryClock.ShiftOf(startAt),
                    CreatedAtUtc = startAt,
                    LastScanAtUtc = startAt
                };

                var cursor = startAt;
                var failed = false;

                for (var seq = 1; seq <= targetSeq; seq++)
                {
                    var step = steps[seq - 1];
                    cursor = cursor.AddSeconds(rnd.Next(20, 140));

                    var isInspection = step.Kind is StepKinds.Inspection or StepKinds.Test;
                    var fail = isInspection && rnd.Next(1000) < 38; // 约 3.8% 不良

                    var (opName, _) = operators[rnd.Next(operators.Length)];
                    var stationCode = $"{(order.LineId == 1 ? "A" : "B")}-{seq:00}";

                    var defect = fail ? defectPool[rnd.Next(defectPool.Count)] : null;

                    logs.Add(new SnMoveLog
                    {
                        SerialUnit = unit,
                        WorkOrderId = order.Id,
                        LineId = order.LineId,
                        StepId = step.Id,
                        Seq = seq,
                        Result = fail ? "Fail" : "Pass",
                        DefectCodeId = defect?.Id,
                        OperatorId = 4,
                        OperatorName = opName,
                        StationCode = stationCode,
                        ProductionDate = FactoryClock.DateOf(cursor),
                        ProductionHour = FactoryClock.HourOf(cursor),
                        Shift = FactoryClock.ShiftOf(cursor),
                        DurationSeconds = rnd.Next(25, 120),
                        CreatedAtUtc = cursor
                    });

                    if (fail)
                    {
                        unit.Status = nameof(UnitStatus.Hold);
                        unit.CurrentSeq = seq;
                        unit.CurrentStepId = step.Id;
                        failed = true;
                        break;
                    }

                    unit.CurrentSeq = seq;
                    unit.CurrentStepId = step.Id;
                    unit.LastScanAtUtc = cursor;
                }

                if (!failed)
                {
                    if (targetSeq >= steps.Count)
                    {
                        unit.Status = nameof(UnitStatus.Completed);
                        unit.CompletedAtUtc = cursor;
                    }
                    else
                    {
                        unit.CurrentSeq = targetSeq + 1;
                        unit.CurrentStepId = steps[targetSeq].Id;
                    }
                }

                units.Add(unit);
            }
        }

        db.Units.AddRange(units);
        db.MoveLogs.AddRange(logs);
        await db.SaveChangesAsync(ct);

        // 两条现场异常，让看板有异常计数
        db.Exceptions.AddRange(
            new ExceptionEvent
            {
                Type = "Material", Description = "1200W 机种散热片来料尺寸超差，已隔离待 MRB 判定",
                LineId = orders[0].LineId, WorkOrderId = orders[0].Id, StationCode = "A-01",
                Status = "Open", ReportedByUserId = 3, ReportedByName = "王班长", ReportedAtUtc = now.AddMinutes(-45)
            },
            new ExceptionEvent
            {
                Type = "Equipment", Description = "老化房 3# 柜温度波动，设备已报修",
                LineId = orders[2].LineId, WorkOrderId = orders[2].Id, StationCode = "B-06",
                Status = "Open", ReportedByUserId = 3, ReportedByName = "王班长", ReportedAtUtc = now.AddMinutes(-20)
            });

        await db.SaveChangesAsync(ct);
    }
}
