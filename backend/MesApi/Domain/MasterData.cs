using System.ComponentModel.DataAnnotations;

namespace MesApi.Domain;

/// <summary>产线（服务器电源：一线/二线...）。</summary>
public class ProductionLine
{
    public int Id { get; set; }

    [MaxLength(16)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

/// <summary>工序字典。服务器电源：插件 → 过锡炉 → 目视 → ICT → 老化 → 耐压 → 功能 → 包装。</summary>
public class ProcessStep
{
    public int Id { get; set; }

    [MaxLength(16)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Normal / Inspection / Test / Packing，用于看板分组与质检判定。</summary>
    [MaxLength(16)]
    public string Kind { get; set; } = StepKinds.Normal;

    /// <summary>不良判定是否必须填写不良代码（目视、测试类工序通常为 true）。</summary>
    public bool RequireDefect { get; set; }

    /// <summary>是否关键工序：不良需班组长确认后才可放行（MVP 仅做标记与看板提醒）。</summary>
    public bool IsKey { get; set; }

    /// <summary>标准节拍：单台在该工序的理论耗时（秒）。APS 排程与产能测算的输入。</summary>
    public int TaktSeconds { get; set; } = 60;

    public bool IsActive { get; set; } = true;
}

public static class StepKinds
{
    public const string Normal = "Normal";
    public const string Inspection = "Inspection";
    public const string Test = "Test";
    public const string Packing = "Packing";
}

/// <summary>产品（机型），如 800W 服务器电源。</summary>
public class Product
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Category { get; set; } = "服务器电源";

    /// <summary>单台标准节拍（秒），用于产线平衡与达成率估算。</summary>
    public int StandardCycleSeconds { get; set; } = 60;

    public bool IsActive { get; set; } = true;

    public ICollection<ProductRoute> Routes { get; set; } = new List<ProductRoute>();
}

/// <summary>产品工艺路线：产品 + 工序 + 顺序 + 标准工时。</summary>
public class ProductRoute
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public int StepId { get; set; }
    public ProcessStep? Step { get; set; }

    /// <summary>从 1 开始的工序顺序，SN 过站严格按此推进。</summary>
    public int Seq { get; set; }

    public int StandardSeconds { get; set; } = 60;

    /// <summary>该工序是否为检验/测试工序（决定报工页是否弹出不良选择）。</summary>
    public bool IsInspection { get; set; }
}

/// <summary>工位（扫码点）。一个工位绑定一条产线与一道工序。</summary>
public class Workstation
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    public int LineId { get; set; }
    public ProductionLine? Line { get; set; }

    public int? StepId { get; set; }
    public ProcessStep? Step { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>不良代码字典，按类别归纳便于 TOP 不良分析。</summary>
public class DefectCode
{
    public int Id { get; set; }

    [MaxLength(16)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Name { get; set; } = string.Empty;

    /// <summary>外观 / 焊接 / 电气 / 包装 / 来料。</summary>
    [MaxLength(16)]
    public string Category { get; set; } = "外观";

    public bool IsActive { get; set; } = true;
}
