namespace MesApi.Domain;

/// <summary>
/// 物料规划规则（V2：不预置冗余料，报废后按实际补料）。
///
/// V1 曾经用"发料 = 计划 ÷ 目标良率"倒推冗余备料，结果一单 300 台要发 353 套，
/// 多出来的 53 套最后全被当作报废吃掉 —— 库存和在制数量都虚高，和真实产线对不上。
/// 服务器电源这种产品，现场报废率只有千分之几，一单几百台也就报废一两台，
/// 所以正确做法是：<b>计划多少发多少，报废一台补一台</b>。
///
/// 现在的口径：
///   发料 = 计划                        （建单时 IssuedQty = PlanQty）
///   报废 1 台 → 补料 1 套 → 再上线 1 台 → 完工补回计划数量
///   完工单的 IssuedQty 通常 = 计划 + 报废数（补料后的累计发料）
///
/// 三条硬规则：
///   1) 物料闸门：已上线台数 ≥ IssuedQty 时不允许再建新 SN（409，提示先补料）
///   2) 报废即补料：ReplenishNeed = 报废 − 已补料量（Allowance）
///   3) 进度 = 完工 ÷ 计划，达到计划即完工，报废不会让进度永远差几台
/// </summary>
public static class MaterialPlanning
{
    /// <summary>
    /// 目标直通率（FPY = 一次通过率）。注意这是<b>改善目标</b>，不再用来反推发料量：
    /// 达不到 85% 也照发计划数量，多出来的损失由"返修"消化，报废才走补料。
    /// </summary>
    public const double TargetFpy = 0.85;

    /// <summary>经验报废率：千分之七（几百台的单报废一两台），只用于预估提示。</summary>
    public const double ScrapRate = 0.007;

    /// <summary>发料 = 计划，不预置冗余备料。</summary>
    public static int IssuedFor(int planQty) => Math.Max(0, planQty);

    /// <summary>按经验报废率预估这张单大概会报废几台（提示用，不参与发料计算）。</summary>
    public static int ExpectedScrap(int planQty)
        => planQty <= 0 ? 0 : (int)Math.Round(planQty * ScrapRate, MidpointRounding.AwayFromZero);

    /// <summary>老数据 IssuedQty=0 时按计划数量兜底，避免"没备料就一台上不了线"。</summary>
    public static int EffectiveIssued(WorkOrder order)
        => order.IssuedQty > 0 ? order.IssuedQty : order.PlanQty;

    /// <summary>已补料的总量（发料 − 计划）。报废先由它抵消，抵消不完才需要继续补。</summary>
    public static int Allowance(int planQty, int issuedQty)
        => Math.Max(0, (issuedQty > 0 ? issuedQty : planQty) - planQty);

    /// <summary>还缺多少料：报废数超过已补料量，就得再补 (报废 − 已补) 套。</summary>
    public static int ReplenishNeed(int planQty, int issuedQty, int scrapQty)
        => Math.Max(0, scrapQty - Allowance(planQty, issuedQty));
}
