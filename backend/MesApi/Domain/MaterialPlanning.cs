namespace MesApi.Domain;

/// <summary>
/// 物料规划规则：工单"计划数量"是客户要的<b>良品数</b>，不是要投多少料。
///
/// 产线一定会有报废，所以实际发料必须多备一截"冗余料"：
///     发料 = 计划 ÷ 目标直通率          （例：计划 120 台、目标 85% → 发 142 套）
///     冗余 = 发料 − 计划                （例：22 套，就是给报废准备的）
///
/// 报废从冗余里扣；扣穿了说明这批料顶不住，必须开"补料"追加发料，
/// 否则产线会停机等料 —— 这就是 IssuedQty 这个字段存在的全部原因。
/// </summary>
public static class MaterialPlanning
{
    /// <summary>目标直通率（良品数 ÷ 投料数），用来反推冗余备料比例。</summary>
    public const double TargetYield = 0.85;

    /// <summary>按计划数量反推应发料数量（含冗余）。</summary>
    public static int IssuedFor(int planQty)
        => planQty <= 0 ? 0 : Math.Max(planQty, (int)Math.Round(planQty / TargetYield, MidpointRounding.AwayFromZero));

    /// <summary>老数据 IssuedQty=0 时按计划数量兜底，避免"没备料就一台上不了线"。</summary>
    public static int EffectiveIssued(WorkOrder order)
        => order.IssuedQty > 0 ? order.IssuedQty : order.PlanQty;

    /// <summary>当前还剩多少冗余料（发料 − 计划），报废从这里扣。</summary>
    public static int Allowance(int planQty, int issuedQty)
        => Math.Max(0, (issuedQty > 0 ? issuedQty : planQty) - planQty);

    /// <summary>需要补多少料：报废数已经超过冗余量，就必须补 (报废 − 冗余) 套。</summary>
    public static int ReplenishNeed(int planQty, int issuedQty, int scrapQty)
        => Math.Max(0, scrapQty - Allowance(planQty, issuedQty));
}
