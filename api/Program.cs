using Hamster.Api.Config;
using Hamster.Api.Data;
using Hamster.Api.Endpoints;
using Hamster.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// 数据库：SqlSugar + Npgsql（PostgreSQL）
builder.Services.AddHamsterDatabase(builder.Configuration);
builder.Services.AddSingleton<ISampleAccountService, SampleAccountService>();

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

if (app.Environment.IsDevelopment())
{
    app.UseCors(ConfigConst.DEV_CORS_POLICY);
    app.MapOpenApi();
}

// 数据库初始化：受 AutoMigrate 开关控制，失败不阻断启动
app.InitializeDatabase();

// 自动注册所有 IEndpoint 端点模块
app.MapHamsterEndpoints();

app.Logger.LogInformation(
    "Hamster.Api 启动中：环境 {Environment}，OpenAPI 文档（仅开发环境）路径 /openapi/v1.json",
    app.Environment.EnvironmentName);

app.Run();
