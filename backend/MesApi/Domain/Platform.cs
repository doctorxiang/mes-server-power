using System.ComponentModel.DataAnnotations;

namespace MesApi.Domain;

/// <summary>
/// 非结构化文件档案：图纸、SOP、检验报告、现场照片。
/// 真实文件内容存对象存储（MinIO/本地），这里只存元数据与 objectKey。
/// </summary>
public class FileDocument
{
    public int Id { get; set; }

    /// <summary>Drawing 图纸 / SOP 作业指导书 / InspectionReport 检验报告 / Photo 现场照片 / Other 其它。</summary>
    [MaxLength(24)]
    public string Category { get; set; } = "Other";

    /// <summary>关联机种（图纸/SOP 挂在机种上）。</summary>
    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>关联工单（检验报告常按工单归档）。</summary>
    public int? WorkOrderId { get; set; }
    public WorkOrder? WorkOrder { get; set; }

    /// <summary>关联 SN（单台的检验报告/照片）。</summary>
    [MaxLength(64)]
    public string? Sn { get; set; }

    [MaxLength(256)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>对象存储里的键：{category}/{yyyyMM}/{uuid}_{fileName}。</summary>
    [MaxLength(320)]
    public string ObjectKey { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [MaxLength(128)]
    public string ContentType { get; set; } = "application/octet-stream";

    [MaxLength(256)]
    public string? Remark { get; set; }

    public int UploadedByUserId { get; set; }

    [MaxLength(32)]
    public string UploadedByName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>夜间报表：每天凌晨生成的每日生产汇总，供晨会与月度报表取数。</summary>
public class DailyReport
{
    public int Id { get; set; }

    /// <summary>报表归属日期（东八区）yyyy-MM-dd。</summary>
    [MaxLength(10)]
    public string ReportDate { get; set; } = string.Empty;

    public int LineId { get; set; }

    public int InputQty { get; set; }

    public int CompletedQty { get; set; }

    public int FailQty { get; set; }

    /// <summary>直通率（完工/投入）%，保留 1 位小数 ×10 存储：97.5 存 975。</summary>
    public int YieldTenths { get; set; }

    /// <summary>TOP 不良：[{code,name,count}] JSON，避免再建关联表。</summary>
    [MaxLength(1024)]
    public string? TopDefectsJson { get; set; }

    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
}
