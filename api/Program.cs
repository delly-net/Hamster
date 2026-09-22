using System.Text;
using Hamster.Api.Config;
using Hamster.Api.Data;
using Hamster.Api.Endpoints;
using Hamster.Api.Security;
using Hamster.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// 数据库：SqlSugar + Sqlite / PostgreSQL（类型与连接串均可由环境变量指定）
builder.Services.AddHamsterDatabase(builder.Configuration);
builder.Services.AddSingleton<ISampleAccountService, SampleAccountService>();
builder.Services.AddSingleton<IUserService, UserService>();
builder.Services.AddSingleton<IAccountSetService, AccountSetService>();
builder.Services.AddSingleton<IAccountService, AccountService>();

// 默认管理员播种与密码重置链接所需配置（均由环境变量优先）
builder.Services.AddSingleton(AdminSeedOptions.From(builder.Configuration));
builder.Services.AddSingleton(PublicUrlOptions.From(builder.Configuration));

// 认证：JWT（签名密钥可由 HAMSTER_JWT_KEY 指定，缺省时随机生成）
var jwtOptions = JwtOptions.From(builder.Configuration);

builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton<JwtTokenService>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // 关闭入站声明重映射，保证 sub / name 与签发端一致
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = ConfigConst.CLAIM_USER_NAME,
        };
    });
builder.Services.AddAuthorization();

// OpenAPI：开发环境通过 MapOpenApi 暴露文档
builder.Services.AddOpenApi();

// 开发环境放行前端 dev server，避免本地联调被 CORS 拦截
builder.Services.AddCors(options => options.AddPolicy(
    ConfigConst.DEV_CORS_POLICY,
    policy => policy
        .WithOrigins(ConfigConst.FRONTEND_DEV_ORIGIN)
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

// 启动信息：数据库类型、连接（脱敏）、Sqlite 文件位置与 JWT 配置
LogStartupInfo(app);

if (app.Environment.IsDevelopment())
{
    app.UseCors(ConfigConst.DEV_CORS_POLICY);
    app.MapOpenApi();
}

// 数据库初始化：受 AutoMigrate 开关控制，失败不阻断启动
app.InitializeDatabase();

// 默认管理员播种：仅在同名账户不存在时创建，失败不阻断启动
app.SeedDefaultAdmin();

app.UseAuthentication();
app.UseAuthorization();

// 自动注册所有 IEndpoint 端点模块
app.MapHamsterEndpoints();

app.Run();

// 输出启动信息：数据库类型、连接串与 JWT 密钥均在此打印，便于部署时确认实际生效的配置
static void LogStartupInfo(WebApplication app)
{
    var logger = app.Logger;
    var database = app.Services.GetRequiredService<DatabaseOptions>();
    var jwt = app.Services.GetRequiredService<JwtOptions>();

    logger.LogInformation("数据库类型：{DbType}", database.DbTypeLabel);
    logger.LogInformation("数据库连接（已脱敏）：{ConnectionString}", database.MaskedConnectionString);
    if (database.SqliteFilePath is { Length: > 0 } sqlitePath)
    {
        logger.LogInformation("Sqlite 数据库文件：{Path}", Path.GetFullPath(sqlitePath));
    }

    logger.LogInformation("自动建表：{AutoMigrate}", database.AutoMigrate ? "已开启" : "已关闭");

    logger.LogInformation("JWT 签发者：{Issuer}，接收方：{Audience}，有效期：{Days} 天", jwt.Issuer, jwt.Audience, jwt.ExpireDays);
    if (jwt.IsKeyGenerated)
    {
        // 随机密钥每次启动都会变化，进程重启后此前签发的令牌将全部失效
        logger.LogWarning(
            "JWT 签名密钥（未配置 {Env}，本次随机生成，重启后旧令牌失效）：{Key}",
            ConfigConst.JWT_KEY_ENV,
            jwt.Key);
    }
    else
    {
        logger.LogInformation("JWT 签名密钥：已由 {Env} 或 appsettings.json 指定", ConfigConst.JWT_KEY_ENV);
    }

    if (jwt.IsKeyTooShort)
    {
        logger.LogWarning("JWT 签名密钥长度不足 32 字节，建议改用 32 字节以上的随机密钥");
    }

    logger.LogInformation("Hamster.Api 启动中：环境 {Environment}，OpenAPI 文档（仅开发环境）路径 /openapi/v1.json", app.Environment.EnvironmentName);
}
