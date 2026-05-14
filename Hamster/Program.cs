using Serilog;
using SqlSugar;
using Hamster.Constant;
using Hamster.Config;
using Hamster.Modules.Example.Simple;
using Hamster.Modules.Example.Simple.Service;
using Hamster.Modules;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information(SampleDataConst.LOG_STARTING_APP);

    var builder = WebApplication.CreateSlimBuilder(args);

    // 配置日志
    var logPath = Environment.GetEnvironmentVariable(ConfigConst.LOG_PATH_ENV) ?? ConfigConst.DEFAULT_LOG_PATH;
    builder.Host.UseSerilog((_, configuration) => configuration
        .MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(logPath, ConfigConst.LOG_FILE_TEMPLATE),
            rollingInterval: RollingInterval.Day));

    // 配置数据库
    var connectionString = Environment.GetEnvironmentVariable(ConfigConst.DB_CONNECTION_ENV)
        ?? ConfigConst.DEFAULT_CONNECTION;
    builder.Services.AddSingleton<ISqlSugarClient>(_ =>
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });
        db.CodeFirst.InitTables(typeof(Todo));
        return db;
    });

    builder.Services.AddScoped<ITodoService, TodoService>();
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.TypeInfoResolverChain.Insert(0, TodoJsonSerializerContext.Default);
    });
    builder.Services.AddOpenApi();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    // 注册 Controller 路由
    app.Register();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, SampleDataConst.LOG_APP_TERMINATED_UNEXPECTEDLY);
}
finally
{
    Log.CloseAndFlush();
}
