using System.ComponentModel.DataAnnotations;

namespace MesApi.Contracts;

public record LoginRequest(
    [Required(ErrorMessage = "请输入用户名或邮箱")] string UserNameOrEmail,
    [Required(ErrorMessage = "请输入密码")] string Password);

public record RefreshRequest([Required(ErrorMessage = "缺少 refreshToken")] string RefreshToken);

public record ChangePasswordRequest(
    [Required(ErrorMessage = "请输入当前密码")] string CurrentPassword,
    [Required(ErrorMessage = "请输入新密码")]
    [StringLength(64, MinimumLength = 8, ErrorMessage = "密码长度需为 8-64 个字符")]
    string NewPassword);

/// <summary>管理员创建账号（车间不开放自助注册）。</summary>
public record CreateUserRequest(
    [Required(ErrorMessage = "用户名不能为空")]
    [StringLength(32, MinimumLength = 3, ErrorMessage = "用户名长度需为 3-32 个字符")]
    [RegularExpression("^[a-zA-Z0-9_.-]+$", ErrorMessage = "用户名只能包含字母、数字、下划线、点、横线")]
    string UserName,

    [Required(ErrorMessage = "姓名不能为空")]
    [StringLength(32, MinimumLength = 1, ErrorMessage = "姓名长度需为 1-32 个字符")]
    string DisplayName,

    [StringLength(32)]
    string? EmployeeNo,

    [Required(ErrorMessage = "密码不能为空")]
    [StringLength(64, MinimumLength = 8, ErrorMessage = "密码长度需为 8-64 个字符")]
    string Password,

    [Required(ErrorMessage = "请选择角色")]
    string Role);

public record UpdateUserRequest(
    [StringLength(32, MinimumLength = 1)] string? DisplayName,
    [StringLength(32)] string? EmployeeNo,
    string? Role,
    bool? IsActive);

public record UserDto(
    int Id, string UserName, string DisplayName, string? EmployeeNo,
    string Role, bool IsActive, DateTime CreatedAtUtc, DateTime? LastLoginAtUtc);

public record AuthResponse(string AccessToken, int ExpiresIn, string RefreshToken, UserDto User);
