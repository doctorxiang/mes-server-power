using MesApi.Contracts;
using MesApi.Domain;
using MesApi.Infrastructure;
using MesApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MesApi.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService auth) : ControllerBase
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>用户名 / 工号 / 邮箱 + 密码登录。</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
        => Ok(await auth.LoginAsync(request, ClientIp, ct));

    /// <summary>用 refreshToken 换取新的 accessToken（旧 refreshToken 立即失效）。</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken ct)
        => Ok(await auth.RefreshAsync(request.RefreshToken, ClientIp, ct));

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct)
    {
        await auth.LogoutAsync(request.RefreshToken, User.GetUserId(), ct);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        var me = await auth.GetUserAsync(User.GetUserId(), ct);
        return me is null ? NotFound() : Ok(me);
    }

    /// <summary>修改密码，成功后返回新令牌（旧会话全部下线）。</summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<ActionResult<AuthResponse>> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
        => Ok(await auth.ChangePasswordAsync(User.GetUserId(), request, ClientIp, ct));

    // ---------------- 账号管理（仅管理员） ----------------

    [HttpGet("users")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> Users(CancellationToken ct)
        => Ok(await auth.ListUsersAsync(ct));

    [HttpPost("users")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<UserDto>> CreateUser(CreateUserRequest request, CancellationToken ct)
        => Ok(await auth.CreateUserAsync(request, ct));

    [HttpPut("users/{id:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<UserDto>> UpdateUser(int id, UpdateUserRequest request, CancellationToken ct)
        => Ok(await auth.UpdateUserAsync(id, request, ct));
}
