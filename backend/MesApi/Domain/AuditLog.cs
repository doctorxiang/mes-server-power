using System.ComponentModel.DataAnnotations;

namespace MesApi.Domain;

/// <summary>
/// 审计日志：谁、什么时候、从哪台终端、调了哪个接口、结果如何。
/// 只追加不修改，用于质量追责、合规检查（客户审厂必查）与异常排查。
/// 注意：过站扫码本身已有 sn_move_log 全量流水，默认不再重复审计，避免写入翻倍。
/// </summary>
public class AuditLog
{
    public long Id { get; set; }

    public int? UserId { get; set; }

    [MaxLength(64)]
    public string? UserName { get; set; }

    [MaxLength(32)]
    public string? Role { get; set; }

    [MaxLength(8)]
    public string Method { get; set; } = string.Empty;

    [MaxLength(256)]
    public string Path { get; set; } = string.Empty;

    /// <summary>语义化动作名，如 WorkOrder.Release / Production.Scan / Auth.Login，便于按动作检索。</summary>
    [MaxLength(64)]
    public string? Action { get; set; }

    public int StatusCode { get; set; }

    public bool Success { get; set; }

    /// <summary>脱敏后的请求体（password/token 一律替换为 ***），超长截断。</summary>
    [MaxLength(1024)]
    public string? Payload { get; set; }

    /// <summary>失败原因（业务异常的 Message）。</summary>
    [MaxLength(512)]
    public string? Message { get; set; }

    [MaxLength(64)]
    public string? ClientIp { get; set; }

    [MaxLength(256)]
    public string? UserAgent { get; set; }

    /// <summary>链路追踪 Id，前后端日志可据此串起来（响应头 X-Request-Id 同值）。</summary>
    [MaxLength(64)]
    public string? RequestId { get; set; }

    public int DurationMs { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
