using System.Text;
using System.Threading.RateLimiting;
using MesApi.Data;
using MesApi.Hubs;
using MesApi.Infrastructure;
using MesApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------- 配置 ----------
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.Configure<JwtOptions>(jwtSection);
var jwt = jwtSection.Get<JwtOptions>() ?? new JwtOptions();

if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key 未配置或长度不足 32 个字符，请检查 appsettings 或环境变量 Jwt__Key。");

// ---------- 数据库：MySql（默认）/ Sqlite（本地快速验证）----------
var provider = builder.Configuration["Database:Provider"] ?? "MySql";
builder.Services.AddDbContext<MesDbContext>(options =>
{
    if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        // Sqlite 仅用于本地快速验证：迁移按 MySQL 方言生成，忽略 provider 差异导致的模型校验警告
        options.UseSqlite(builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=mes.db")
               .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }
    else
    {
        var cs = builder.Configuration.GetConnectionString("MySql")
                 ?? throw new InvalidOperationException("缺少连接字符串 ConnectionStrings:MySql");
        // 固定版本，避免启动探测；EnableRetryOnFailure 应对车间网络抖动
        options.UseMySql(cs, new MySqlServerVersion(new Version(8, 0, 36)),
            my => my.EnableRetryOnFailure(3, TimeSpan.FromSeconds(3), null));
    }
});

builder.Services.AddMemoryCache();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IProductionService, ProductionService>();
builder.Services.AddScoped<IWorkOrderService, WorkOrderService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<ITraceService, TraceService>();
builder.Services.AddScoped<IMasterDataService, MasterDataService>();
builder.Services.AddScoped<IExceptionService, ExceptionService>();
builder.Services.AddSingleton<IBoardNotifier, BoardNotifier>();

// ---------- 认证 / 授权 ----------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // 保持短 claim 名，避免 role/sub 被重映射
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role"
        };
        // SignalR 通过 query string 传 token（浏览器 WebSocket 无法自定义 Header）
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 32 * 1024);

// ---------- CORS ----------
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? ["http://localhost:5173", "http://127.0.0.1:5173"];
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

// ---------- 限流 ----------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // 登录防爆破：按 IP 每分钟 20 次
    options.AddPolicy("auth", httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"auth:{key}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });

    // 扫码：按登录用户限流。200 台终端大多是操作工，单人多工位共用账号也不至于超限
    options.AddPolicy("scan", httpContext =>
    {
        var key = httpContext.User.FindFirst("sub")?.Value
                  ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"scan:{key}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            status = StatusCodes.Status429TooManyRequests,
            title = "请求过于频繁",
            detail = "操作太频繁，请稍后再试"
        }, ct);
    };
});

// ---------- 统一错误处理 / 控制器 / OpenAPI ----------
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// 反向代理（Nginx / IIS）后取真实客户端 IP
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseExceptionHandler();
app.UseCors("frontend");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapControllers();
app.MapHub<BoardHub>("/hubs/board");
app.MapGet("/health", () => Results.Ok(new { status = "ok", provider, time = DateTime.UtcNow })).AllowAnonymous();

// ---------- 单端口部署：前端构建产物放 wwwroot 时由后端直接托管 ----------
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

// ---------- 启动时建表 + 种子数据 ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Sqlite 走 EnsureCreated（迁移脚本是 MySQL 方言，自增列不通用）；MySQL 走正式迁移
    if (db.Database.IsSqlite())
        await db.Database.EnsureCreatedAsync();
    else
        await db.Database.MigrateAsync();

    await SeedData.EnsureSeededAsync(db, app.Configuration, logger);
}

app.Run();
