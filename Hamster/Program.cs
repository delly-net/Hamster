using Serilog;
using SqlSugar;
using Hamster.Constant;
using Hamster.Modules.Example;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information(LogMessageConst.LOG_STARTING_APP);

    var builder = WebApplication.CreateSlimBuilder(args);

    // 配置日志
    var logPath = Environment.GetEnvironmentVariable(SystemConfigConst.LOG_PATH_ENV) ?? SystemConfigConst.DEFAULT_LOG_PATH;
    builder.Host.UseSerilog((_, configuration) => configuration
        .MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(logPath, SystemConfigConst.LOG_FILE_TEMPLATE),
            rollingInterval: RollingInterval.Day));

    // 配置数据库
    var connectionString = Environment.GetEnvironmentVariable(SystemConfigConst.DB_CONNECTION_ENV)
        ?? SystemConfigConst.DEFAULT_CONNECTION;
    builder.Services.AddSingleton<ISqlSugarClient>(_ =>
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });
        return db;
    });

    builder.Services.AddOpenApi();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    // 注册路由
    ExampleRouter.Register(app);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, LogMessageConst.LOG_APP_TERMINATED_UNEXPECTEDLY);
}
finally
{
    Log.CloseAndFlush();
}
