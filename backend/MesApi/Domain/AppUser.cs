using System.ComponentModel.DataAnnotations;

namespace MesApi.Domain;

/// <summary>系统用户（工厂内部账号，不开放自助注册，由管理员创建）。</summary>
public class AppUser
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>统一小写存储，保证唯一性判断可靠。</summary>
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>PBKDF2 哈希串，格式 pbkdf2-sha256$iterations$salt$hash。</summary>
    [MaxLength(256)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>显示姓名，报工记录与看板上直接展示。</summary>
    [MaxLength(32)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>工号，现场按工号核对身份。</summary>
    [MaxLength(32)]
    public string? EmployeeNo { get; set; }

    [MaxLength(32)]
    public string Role { get; set; } = Roles.Operator;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAtUtc { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}

/// <summary>角色定义：与前端路由/按钮权限一一对应。</summary>
public static class Roles
{
    public const string Admin = "Admin";        // 系统管理员：主数据、用户、全部权限
    public const string Planner = "Planner";    // PMC 计划员：建单、下发、改交期
    public const string Leader = "Leader";      // 班组长：派工、异常处理、看板
    public const string Operator = "Operator";  // 操作工：扫码过站、报工
    public const string Qc = "Qc";              // 质检员：检验、不良判定
    public const string Viewer = "Viewer";      // 只读：车间主管看板

    public static readonly string[] All = [Admin, Planner, Leader, Operator, Qc, Viewer];
}
