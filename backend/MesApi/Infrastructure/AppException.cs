namespace MesApi.Infrastructure;

/// <summary>业务异常，由全局中间件转换为 ProblemDetails。</summary>
public class AppException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class AppErrors
{
    public static AppException InvalidCredentials() => new("用户名或密码不正确", StatusCodes.Status401Unauthorized);
    public static AppException AccountDisabled() => new("账号已被禁用，请联系管理员", StatusCodes.Status403Forbidden);
    public static AppException InvalidRefreshToken() => new("登录状态已失效，请重新登录", StatusCodes.Status401Unauthorized);
    public static AppException EmailTaken() => new("该邮箱已被注册", StatusCodes.Status409Conflict);
    public static AppException UserNameTaken() => new("该用户名已被占用", StatusCodes.Status409Conflict);
}
