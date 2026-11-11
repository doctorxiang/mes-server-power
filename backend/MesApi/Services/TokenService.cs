using System.Security.Cryptography;
using System.Text;
using MesApi.Domain;
using MesApi.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MesApi.Services;

public interface ITokenService
{
    (string Token, DateTime ExpiresAtUtc) CreateAccessToken(AppUser user);
    string CreateRefreshToken();
    string HashRefreshToken(string refreshToken);
}

public class TokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _opt = options.Value;

    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(AppUser user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_opt.AccessTokenMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Key));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _opt.Issuer,
            Audience = _opt.Audience,
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            // 统一使用短 claim 名（配合验证端 MapInboundClaims = false）
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                ["name"] = user.UserName,
                ["displayName"] = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName,
                ["email"] = user.Email,
                ["role"] = user.Role
            }
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return (token, expiresAt);
    }

    /// <summary>256 位随机令牌（Base64Url），仅此一次返回给客户端。</summary>
    public string CreateRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));

    public string HashRefreshToken(string refreshToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));
        return Convert.ToBase64String(bytes);
    }
}
