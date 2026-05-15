using Serilog;
using Hamster;
using Hamster.Constant;
using Hamster.Modules.Example;
using Hamster.Modules.Example.Simple.Services;
using Hamster.Core;

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
    builder.Services.AddSingleton<IDatabaseService>(_ => new DatabaseService(connectionString));

    builder.Services.AddOpenApi();
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.TypeInfoResolver = AppJsonSerializerContext.Default;
    });

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    // 注册模块
    app.MapMiniModule<ExampleModule>();

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