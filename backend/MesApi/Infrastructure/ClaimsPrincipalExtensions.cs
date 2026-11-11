using System.Security.Claims;
using MesApi.Domain;

namespace MesApi.Infrastructure;

public static class ClaimsPrincipalExtensions
{
    /// <summary>从 JWT 的 sub 声明取用户 Id（验证端已关闭 claim 映射）。</summary>
    public static int GetUserId(this ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(sub, out var id))
            throw new AppException("令牌无效，请重新登录", StatusCodes.Status401Unauthorized);
        return id;
    }

    /// <summary>报工记录里展示的姓名（优先 displayName，回退用户名）。</summary>
    public static string GetDisplayName(this ClaimsPrincipal principal)
        => principal.FindFirstValue("displayName")
           ?? principal.FindFirstValue("name")
           ?? "未知用户";

    public static string GetRole(this ClaimsPrincipal principal)
        => principal.FindFirstValue("role") ?? Roles.Operator;
}
