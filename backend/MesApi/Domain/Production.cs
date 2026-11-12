using System.ComponentModel.DataAnnotations;

namespace MesApi.Domain;

public enum WorkOrderStatus
{
    Draft = 0,        // 草稿
    Released = 1,     // 已下发（可上线生产）
    InProduction = 2, // 生产中
    Completed = 3,    // 已完工
    Closed = 4        // 已关单
}

public enum UnitStatus
{
    InProcess = 0,  // 在制
    Hold = 1,       // 不良待处理（隔离）
    Completed = 2,  // 已完工
    Scrapped = 3    // 报废
}

/// <summary>工单：一条产线、一个机型、一个计划数量。</summary>
public class WorkOrder
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string OrderNo { get; set; } = string.Empty;

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public int LineId { get; set; }
    public ProductionLine? Line { get; set; }

    public int PlanQty { get; set; }

    [MaxLength(16)]
    public string Status { get; set; } = nameof(WorkOrderStatus.Draft);

    /// <summary>优先级 1=急件 2=正常 3=低。</summary>
    public int Priority { get; set; } = 2;

    public DateTime PlannedStartUtc { get; set; } = DateTime.UtcNow;

    public DateTime DueDateUtc { get; set; } = DateTime.UtcNow.AddDays(3);

    [MaxLength(256)]
    public string? Remark { get; set; }

    public int CreatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ReleasedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public ICollection<SerialUnit> Units { get; set; } = new List<SerialUnit>();
}

/// <summary>单台产品（SN）。服务器电源一台一码，是全流程追溯的主键。</summary>
public class SerialUnit
{
    public long Id { get; set; }

    [MaxLength(64)]
    public string Sn { get; set; } = string.Empty;

    public int WorkOrderId { get; set; }
    public WorkOrder? WorkOrder { get; set; }

    public int ProductId { get; set; }

    /// <summary>冗余产线 Id：看板按线聚合时避免多一层 join，200 终端写入下更省。</summary>
    public int LineId { get; set; }

    /// <summary>当前所在工序顺序（1 起）。等于路线总步数且已过站即完工。</summary>
    public int CurrentSeq { get; set; } = 1;

    public int? CurrentStepId { get; set; }

    [MaxLength(16)]
    public string Status { get; set; } = nameof(UnitStatus.InProcess);

    /// <summary>生产日期 yyyy-MM-dd（东八区），看板按天聚合走这个字段，避免时区换算。</summary>
    [MaxLength(10)]
    public string ProductionDate { get; set; } = string.Empty;

    [MaxLength(8)]
    public string Shift { get; set; } = "白班";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public DateTime LastScanAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>乐观并发令牌：200 终端同时扫同一 SN 时靠它兜底，避免重复推进工序。</summary>
    public int Version { get; set; }
}

/// <summary>SN 过站流水，只追加不修改，追溯与报表的唯一事实来源。</summary>
public class SnMoveLog
{
    public long Id { get; set; }

    public long SerialUnitId { get; set; }
    public SerialUnit? SerialUnit { get; set; }

    public int WorkOrderId { get; set; }

    public int LineId { get; set; }

    public int StepId { get; set; }
    public ProcessStep? Step { get; set; }

    public int Seq { get; set; }

    /// <summary>Pass / Fail。</summary>
    [MaxLength(8)]
    public string Result { get; set; } = "Pass";

    public int? DefectCodeId { get; set; }
    public DefectCode? DefectCode { get; set; }

    [MaxLength(256)]
    public string? Remark { get; set; }

    public int OperatorId { get; set; }

    [MaxLength(32)]
    public string OperatorName { get; set; } = string.Empty;

    [MaxLength(32)]
    public string StationCode { get; set; } = string.Empty;

    [MaxLength(10)]
    public string ProductionDate { get; set; } = string.Empty;

    /// <summary>生产小时（东八区，00-23），看板小时产出直接按此聚合，避免数据库时区函数差异。</summary>
    [MaxLength(2)]
    public string ProductionHour { get; set; } = string.Empty;

    [MaxLength(8)]
    public string Shift { get; set; } = "白班";

    /// <summary>客户端幂等键（扫码枪终端生成 UUID）。断网重试不会重复计数。</summary>
    [MaxLength(64)]
    public string? ClientKey { get; set; }

    /// <summary>该工序耗时（秒），由上一站时间推算，用于节拍分析。</summary>
    public int DurationSeconds { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 维修记录：SN 在维修站的进站/出站档案。
/// 进站 = 判不良隔离（Hold）后扫码确认进站，开一条 InProgress 记录；
/// 出站 = 修复完成（回原判不良工序重测）或 报废，关闭记录。
/// 同一台 SN 可多次进出（重复维修），RepairNo 递增，超过上限需管理员授权。
/// </summary>
public class RepairRecord
{
    public int Id { get; set; }

    public long SerialUnitId { get; set; }
    public SerialUnit? SerialUnit { get; set; }

    public int WorkOrderId { get; set; }

    /// <summary>第几次维修（1 起），进站时按该 SN 历史记录数 + 1 生成。</summary>
    public int RepairNo { get; set; }

    /// <summary>哪个工序判的不良（出站后 SN 回到该工序重测）。</summary>
    public int SourceStepId { get; set; }
    public ProcessStep? SourceStep { get; set; }

    public int? DefectCodeId { get; set; }
    public DefectCode? DefectCode { get; set; }

    /// <summary>维修措施：Replace 换料 / Resolder 补焊 / Adjust 调整 / Other 其它。</summary>
    [MaxLength(16)]
    public string Action { get; set; } = "Other";

    /// <summary>InProgress 维修中 / Repaired 修复出站 / Scrapped 报废出站。</summary>
    [MaxLength(16)]
    public string Status { get; set; } = "InProgress";

    [MaxLength(256)]
    public string? Remark { get; set; }

    public int OperatorId { get; set; }

    [MaxLength(32)]
    public string OperatorName { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? StationCode { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(32)]
    public string? CheckoutByName { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}

/// <summary>现场异常（缺料、设备故障、品质异常、停线）。</summary>
public class ExceptionEvent
{
    public int Id { get; set; }

    public int? WorkOrderId { get; set; }
    public WorkOrder? WorkOrder { get; set; }

    public int LineId { get; set; }

    [MaxLength(32)]
    public string? StationCode { get; set; }

    /// <summary>Material / Equipment / Quality / Other。</summary>
    [MaxLength(16)]
    public string Type { get; set; } = "Other";

    [MaxLength(256)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Open / Closed。</summary>
    [MaxLength(8)]
    public string Status { get; set; } = "Open";

    public int ReportedByUserId { get; set; }

    [MaxLength(32)]
    public string ReportedByName { get; set; } = string.Empty;

    public DateTime ReportedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(32)]
    public string? HandledByName { get; set; }

    [MaxLength(256)]
    public string? HandleNote { get; set; }

    public DateTime? HandledAtUtc { get; set; }
}

/// <summary>工厂时钟：所有"生产日期/班次"按东八区计算，存储用 UTC。</summary>
public static class FactoryClock
{
    private static readonly TimeZoneInfo Tz = Resolve();

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "China Standard Time", "Asia/Shanghai" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("CST+8", TimeSpan.FromHours(8), "CST+8", "CST+8");
    }

    public static DateTime NowLocal => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz);

    /// <summary>工厂时区（东八区），供外部做 UTC → 本地显示的转换。</summary>
    public static TimeZoneInfo TimeZone => Tz;

    public static string Today => NowLocal.ToString("yyyy-MM-dd");

    public static string DateOf(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Tz).ToString("yyyy-MM-dd");

    /// <summary>本地小时（00-23），用于看板小时产出聚合。</summary>
    public static string HourOf(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Tz).ToString("HH");

    /// <summary>白班 08:00-20:00，其余算夜班。</summary>
    public static string ShiftOf(DateTime utc)
    {
        var hour = TimeZoneInfo.ConvertTimeFromUtc(utc, Tz).Hour;
        return hour is >= 8 and < 20 ? "白班" : "夜班";
    }

    /// <summary>本地某天 00:00 对应的 UTC 时刻，用于时间范围查询。</summary>
    public static DateTime StartOfLocalDayUtc(string date)
    {
        var local = DateTime.SpecifyKind(DateTime.Parse(date), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, Tz);
    }

    public static DateTime StartOfTodayUtc() => StartOfLocalDayUtc(Today);
}
