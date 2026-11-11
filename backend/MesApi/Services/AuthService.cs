using MesApi.Contracts;
using MesApi.Data;
using MesApi.Domain;
using MesApi.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MesApi.Services;

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request, string? ip, CancellationToken ct);
    Task<AuthResponse> RefreshAsync(string refreshToken, string? ip, CancellationToken ct);
    Task LogoutAsync(string refreshToken, int userId, CancellationToken ct);
    Task<AuthResponse> ChangePasswordAsync(int userId, ChangePasswordRequest request, string? ip, CancellationToken ct);
    Task<UserDto?> GetUserAsync(int userId, CancellationToken ct);
    Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct);
    Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct);
    Task<UserDto> UpdateUserAsync(int id, UpdateUserRequest request, CancellationToken ct);
}

public class AuthService(
    MesDbContext db,
    ITokenService tokens,
    IOptions<JwtOptions> jwtOptions,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly int _refreshDays = jwtOptions.Value.RefreshTokenDays;

    public async Task<AuthResponse> LoginAsync(LoginRequest request, string? ip, CancellationToken ct)
    {
        var key = request.UserNameOrEmail.Trim();
        var lower = key.ToLowerInvariant();

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Email == lower || u.UserName == key || u.EmployeeNo == key, ct);

        if (user is null || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            throw AppErrors.InvalidCredentials();

        if (!user.IsActive)
            throw AppErrors.AccountDisabled();

        user.LastLoginAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return await IssueTokensAsync(user, ip, ct);
    }

    public async Task<AuthResponse> RefreshAsync(string refreshToken, string? ip, CancellationToken ct)
    {
        var hash = tokens.HashRefreshToken(refreshToken);

        var stored = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null)
            throw AppErrors.InvalidRefreshToken();

        if (stored.IsRevoked)
        {
            logger.LogWarning("检测到刷新令牌复用，撤销用户 {UserId} 全部会话", stored.UserId);
            await RevokeAllAsync(stored.UserId, ct);
            throw AppErrors.InvalidRefreshToken();
        }

        if (stored.IsExpired)
            throw AppErrors.InvalidRefreshToken();

        if (stored.User is null || !stored.User.IsActive)
            throw AppErrors.AccountDisabled();

        return await IssueTokensAsync(stored.User, ip, ct, rotatedFrom: stored);
    }

    public async Task LogoutAsync(string refreshToken, int userId, CancellationToken ct)
    {
        var hash = tokens.HashRefreshToken(refreshToken);
        var stored = await db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, ct);

        if (stored is null || stored.IsRevoked)
            return;

        stored.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<AuthResponse> ChangePasswordAsync(int userId, ChangePasswordRequest request, string? ip, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new AppException("用户不存在", StatusCodes.Status404NotFound);

        if (!PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            throw new AppException("当前密码不正确", StatusCodes.Status400BadRequest);

        EnsurePasswordStrength(request.NewPassword);

        if (request.CurrentPassword == request.NewPassword)
            throw new AppException("新密码不能与当前密码相同", StatusCodes.Status400BadRequest);

        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        await db.SaveChangesAsync(ct);

        await RevokeAllAsync(userId, ct);
        return await IssueTokensAsync(user, ip, ct);
    }

    public async Task<UserDto?> GetUserAsync(int userId, CancellationToken ct)
        => await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserDto(u.Id, u.UserName, u.DisplayName, u.EmployeeNo, u.Role, u.IsActive, u.CreatedAtUtc, u.LastLoginAtUtc))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct)
        => await db.Users
            .AsNoTracking()
            .OrderBy(u => u.Id)
            .Select(u => new UserDto(u.Id, u.UserName, u.DisplayName, u.EmployeeNo, u.Role, u.IsActive, u.CreatedAtUtc, u.LastLoginAtUtc))
            .ToListAsync(ct);

    public async Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct)
    {
        var userName = request.UserName.Trim();
        if (await db.Users.AnyAsync(u => u.UserName == userName, ct))
            throw AppErrors.UserNameTaken();

        if (!Roles.All.Contains(request.Role))
            throw new AppException("角色不合法", StatusCodes.Status400BadRequest);

        EnsurePasswordStrength(request.Password);

        var user = new AppUser
        {
            UserName = userName,
            DisplayName = request.DisplayName.Trim(),
            EmployeeNo = string.IsNullOrWhiteSpace(request.EmployeeNo) ? null : request.EmployeeNo.Trim(),
            Email = $"{userName.ToLowerInvariant()}@factory.local",
            PasswordHash = PasswordHasher.Hash(request.Password),
            Role = request.Role
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return (await GetUserAsync(user.Id, ct))!;
    }

    public async Task<UserDto> UpdateUserAsync(int id, UpdateUserRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
                   ?? throw new AppException("用户不存在", StatusCodes.Status404NotFound);

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
            user.DisplayName = request.DisplayName.Trim();

        if (request.EmployeeNo is not null)
            user.EmployeeNo = string.IsNullOrWhiteSpace(request.EmployeeNo) ? null : request.EmployeeNo.Trim();

        if (request.Role is not null)
        {
            if (!Roles.All.Contains(request.Role))
                throw new AppException("角色不合法", StatusCodes.Status400BadRequest);
            user.Role = request.Role;
        }

        if (request.IsActive is not null)
        {
            if (!request.IsActive.Value && user.Role == Roles.Admin && await IsLastAdminAsync(user.Id, ct))
                throw new AppException("至少需要保留一个启用的管理员账号", StatusCodes.Status400BadRequest);
            user.IsActive = request.IsActive.Value;
        }

        await db.SaveChangesAsync(ct);
        return (await GetUserAsync(user.Id, ct))!;
    }

    private async Task<bool> IsLastAdminAsync(int excludeUserId, CancellationToken ct)
        => !await db.Users.AnyAsync(u => u.Role == Roles.Admin && u.IsActive && u.Id != excludeUserId, ct);

    private async Task<AuthResponse> IssueTokensAsync(AppUser user, string? ip, CancellationToken ct, RefreshToken? rotatedFrom = null)
    {
        var (accessToken, expiresAt) = tokens.CreateAccessToken(user);
        var rawRefresh = tokens.CreateRefreshToken();
        var refreshHash = tokens.HashRefreshToken(rawRefresh);

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(_refreshDays),
            CreatedByIp = ip
        });

        if (rotatedFrom is not null)
        {
            rotatedFrom.RevokedAtUtc = DateTime.UtcNow;
            rotatedFrom.ReplacedByTokenHash = refreshHash;
        }

        await db.SaveChangesAsync(ct);

        var expiresIn = (int)Math.Max(0, (expiresAt - DateTime.UtcNow).TotalSeconds);
        return new AuthResponse(accessToken, expiresIn, rawRefresh,
            new UserDto(user.Id, user.UserName, user.DisplayName, user.EmployeeNo, user.Role, user.IsActive, user.CreatedAtUtc, user.LastLoginAtUtc));
    }

    private async Task RevokeAllAsync(int userId, CancellationToken ct)
    {
        var active = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ToListAsync(ct);

        foreach (var token in active)
            token.RevokedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    private static void EnsurePasswordStrength(string password)
    {
        if (password.Length < 8 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new AppException("密码至少 8 位，且需同时包含字母和数字", StatusCodes.Status400BadRequest);
    }
}
