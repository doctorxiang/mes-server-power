using System.ComponentModel.DataAnnotations;

namespace MesApi.Contracts;

// ---------------- 主数据 ----------------

public record StepDto(int Id, string Code, string Name, string Kind, bool RequireDefect, bool IsKey, int Seq, int StandardSeconds, bool IsInspection);

public record ProductDto(int Id, string Code, string Name, string Category, int StandardCycleSeconds, bool IsActive, IReadOnlyList<StepDto> Route);

public record LineDto(int Id, string Code, string Name, bool IsActive);

public record WorkstationDto(int Id, string Code, string Name, int LineId, int? StepId, string? StepName);

public record DefectCodeDto(int Id, string Code, string Name, string Category);

public record MasterBootstrap(
    IReadOnlyList<LineDto> Lines,
    IReadOnlyList<WorkstationDto> Workstations,
    IReadOnlyList<DefectCodeDto> DefectCodes,
    IReadOnlyList<ProductDto> Products,
    IReadOnlyList<StepDto> Steps);

// ---------------- 工单 ----------------

public record CreateWorkOrderRequest(
    [Required(ErrorMessage = "请选择产品")] int ProductId,
    [Required(ErrorMessage = "请选择产线")] int LineId,
    [Range(1, 1_000_000, ErrorMessage = "计划数量需在 1-1000000 之间")] int PlanQty,
    [Range(1, 3)] int Priority,
    DateTime PlannedStartUtc,
    DateTime DueDateUtc,
    [StringLength(256)] string? Remark);

/// <summary>补料：报废一台补一台，追加发料后产线才能继续上线。</summary>
public record ReplenishRequest(
    [Range(1, 100_000, ErrorMessage = "补料数量需在 1-100000 之间")] int Qty,
    [StringLength(200)] string? Reason);

/// <summary>
/// 工单列表行。计划 / 发料 / 投入 / 完工 / 报废五个数字的关系：
/// 发料 = 计划（不预置冗余），报废一台补一台，所以完工单通常是 发料 = 计划 + 报废；
/// 投入 = 已上线 SN（只增不减），完工达到计划即 100%，ReplenishQty &gt; 0 表示还要补料。
/// </summary>
public record WorkOrderListItem(
    int Id, string OrderNo, string ProductCode, string ProductName, int LineId, string LineName,
    int PlanQty, int InputQty, int CompletedQty, int HoldQty, string Status,
    int Priority, DateTime PlannedStartUtc, DateTime DueDateUtc, DateTime CreatedAtUtc,
    int IssuedQty, int ScrapQty, int ReplenishQty);

public record StepProgressDto(int StepId, string StepCode, string StepName, int Seq, bool IsInspection, int PassQty, int FailQty, int WipQty);

public record WorkOrderDetail(
    WorkOrderListItem Summary,
    IReadOnlyList<StepProgressDto> Steps,
    IReadOnlyList<UnitRow> RecentUnits,
    int Fpy,
    IReadOnlyList<ScrapUnitRow> ScrapUnits);

/// <summary>报废明细：哪台、在哪个工序判的、什么不良、什么时候报废的。</summary>
public record ScrapUnitRow(
    string Sn, string StepName, string DefectName, DateTime? ScrappedAtUtc, string Reason);

public record UnitRow(
    long Id, string Sn, int CurrentSeq, string? CurrentStepName, string Status,
    DateTime CreatedAtUtc, DateTime? CompletedAtUtc, DateTime LastScanAtUtc);

// ---------------- 报工 / 过站 ----------------

public record ScanRequest(
    [Required(ErrorMessage = "请扫描 SN")] string Sn,
    [Required(ErrorMessage = "请扫描工单")] string OrderNo,
    [Required(ErrorMessage = "缺少工位")] string StationCode,
    [Required(ErrorMessage = "缺少工序")] string StepCode,
    [Required(ErrorMessage = "缺少判定结果")] string Result,
    string? DefectCode,
    [StringLength(256)] string? Remark,
    string? ClientKey);

public record ScanResponse(
    bool Accepted,
    bool Duplicated,
    string Message,
    string Sn,
    string OrderNo,
    string StepCode,
    string StepName,
    int Seq,
    int TotalSteps,
    string UnitStatus,
    string? NextStepCode,
    string? NextStepName,
    int TodayStepPassQty,
    int TodayStepFailQty);

public record RecentLogRow(
    long Id, string Sn, string StepName, string Result, string? DefectName,
    string OperatorName, string StationCode, DateTime CreatedAtUtc);

/// <summary>不良品处理：rework 放回返修（同工序重扫）/ scrap 报废。</summary>
public record HandleHoldRequest(
    [Required(ErrorMessage = "请选择处理方式")] string Action,
    [StringLength(256)] string? Remark);

public record UnitActionResponse(string Sn, string Status, string Message);

// ---------------- 维修站 ----------------

/// <summary>维修站进站：判不良隔离（Hold）的 SN 扫码进站，开维修记录。</summary>
public record RepairCheckinRequest(
    [Required(ErrorMessage = "请扫描 SN")] string Sn,
    string? DefectCode,
    [Required(ErrorMessage = "请选择维修措施")] string Action,
    [StringLength(256)] string? Remark,
    string? StationCode,
    /// <summary>超过维修次数上限时需管理员授权放行。</summary>
    bool Force = false);

/// <summary>维修站出站：repaired = 修复完成（回原工序重测）/ scrap = 报废。</summary>
public record RepairCheckoutRequest(
    [Required(ErrorMessage = "请扫描 SN")] string Sn,
    [Required(ErrorMessage = "请选择出站方式")] string Outcome,
    [StringLength(256)] string? Remark,
    string? StationCode);

/// <summary>待维修队列行：隔离中的 SN 及其最后不良信息、历史维修次数。</summary>
public record RepairPendingRow(
    long UnitId, string Sn, string OrderNo, string ProductName, string LineName,
    string CurrentStepName, int CurrentSeq, string? DefectCode, string? DefectName,
    int RepairCount, bool Repairing, DateTime FailedAtUtc);

/// <summary>维修记录行（队列页与追溯共用）。</summary>
public record RepairRecordRow(
    int Id, string Sn, int RepairNo, string SourceStepName, string? DefectName,
    string Action, string Status, string OperatorName, string? StationCode,
    string? CheckoutByName, DateTime StartedAtUtc, DateTime? CompletedAtUtc, string? Remark);

// ---------------- 追溯 ----------------

public record TraceStepDto(
    int Seq, string StepCode, string StepName, int StepId,
    string? Result, string? DefectName, string? OperatorName, string? StationCode,
    DateTime? ScanAtUtc, int DurationSeconds, string? Remark);

public record TraceResponse(
    string Sn, string OrderNo, string ProductCode, string ProductName, string LineName,
    string Status, string CurrentStepName, DateTime CreatedAtUtc, DateTime? CompletedAtUtc,
    int PassCount, int FailCount, IReadOnlyList<TraceStepDto> Steps,
    IReadOnlyList<RepairRecordRow> Repairs);

// ---------------- 看板 ----------------

public record LineSummaryDto(
    int LineId, string LineName, int PlanQty, int InputQty, int CompletedQty, int HoldQty,
    int TodayPass, int TodayFail, double Fpy, IReadOnlyList<StepWipDto> WipByStep);

public record StepWipDto(string StepCode, string StepName, int WipQty);

public record HourPointDto(string Hour, int Pass, int Fail);

public record DefectTopDto(string Code, string Name, string Category, int Qty);

public record DashboardDto(
    string Date, string Shift, DateTime GeneratedAtUtc,
    int PlanQty, int InputQty, int CompletedQty, int HoldQty,
    int TodayPass, int TodayFail, double Fpy,
    int OpenExceptions,
    IReadOnlyList<LineSummaryDto> Lines,
    IReadOnlyList<HourPointDto> Hourly,
    IReadOnlyList<StepWipDto> WipByStep,
    IReadOnlyList<DefectTopDto> TopDefects,
    IReadOnlyList<RecentLogRow> Recent);

// ---------------- 异常 ----------------

public record CreateExceptionRequest(
    [Required] string Type,
    [Required, StringLength(256, MinimumLength = 2)] string Description,
    int? WorkOrderId,
    int LineId,
    string? StationCode);

public record ExceptionRow(
    int Id, string Type, string Description, string Status, string LineName,
    string? OrderNo, string? StationCode, string ReportedByName, DateTime ReportedAtUtc,
    string? HandledByName, string? HandleNote, DateTime? HandledAtUtc);

public record CloseExceptionRequest([Required, StringLength(256, MinimumLength = 2)] string HandleNote);
