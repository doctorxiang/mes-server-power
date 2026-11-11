namespace MesApi.Domain;

/// <summary>
/// 刷新令牌。数据库只保存 SHA-256 摘要，明文仅返回给客户端一次。
/// </summary>
public class RefreshToken
{
    public long Id { get; set; }

    public int UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>原始令牌的 SHA-256（Base64），唯一索引。</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [System.ComponentModel.DataAnnotations.MaxLength(45)]
    public string? CreatedByIp { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>轮换后指向新令牌摘要，用于复用检测。</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(64)]
    public string? ReplacedByTokenHash { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
    public bool IsRevoked => RevokedAtUtc is not null;
    public bool IsActive => !IsRevoked && !IsExpired;
}
