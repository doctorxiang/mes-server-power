using System.ComponentModel.DataAnnotations;

namespace MesApi.Domain;

/// <summary>
/// QC 抽检记录：针对已包装成品的出货前抽样检验（AQL 抽检）。
///
/// 与测试工序（产线逐台全检）不同，抽检按批次拆箱复核（外观/包装/功能/安规），
/// 不推进工序、不改变 SN 状态，只留质量证据。
/// 记录保留 SN，便于反查该台的完整工序履历。
/// </summary>
public class QcInspection
{
    public int Id { get; set; }

    public long SerialUnitId { get; set; }
    public SerialUnit? SerialUnit { get; set; }

    /// <summary>冗余工单 / 产线 / 机种：抽检报表按这三者聚合，避免每次三连 join。</summary>
    public int WorkOrderId { get; set; }
    public int LineId { get; set; }
    public int ProductId { get; set; }

    /// <summary>抽检类别：外观 / 包装 / 功能 / 安规。</summary>
    [MaxLength(16)]
    public string Category { get; set; } = QcCategories.Package;

    /// <summary>Pass / Fail。</summary>
    [MaxLength(8)]
    public string Result { get; set; } = "Pass";

    public int? DefectCodeId { get; set; }
    public DefectCode? DefectCode { get; set; }

    [MaxLength(256)]
    public string? Remark { get; set; }

    public int InspectorId { get; set; }

    [MaxLength(32)]
    public string InspectorName { get; set; } = string.Empty;

    /// <summary>抽检日期 yyyy-MM-dd（东八区），报表按天/班统计直接走这个字段。</summary>
    [MaxLength(10)]
    public string ProductionDate { get; set; } = string.Empty;

    public DateTime InspectedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class QcCategories
{
    public const string Appearance = "外观";
    public const string Package = "包装";
    public const string Function = "功能";
    public const string Safety = "安规";

    public static readonly string[] All = [Appearance, Package, Function, Safety];
}
