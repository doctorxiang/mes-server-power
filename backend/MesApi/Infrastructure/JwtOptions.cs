namespace MesApi.Infrastructure;

/// <summary>JWT 配置，绑定 appsettings 的 "Jwt" 节点。</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "MesApi";
    public string Audience { get; set; } = "AuthClient";
    public string Key { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}
