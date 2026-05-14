using Serilog;
using SqlSugar;
using System.Text.Json.Serialization;
using Hamster.Constant;
using Hamster.Config;
using Hamster.Service;
using Hamster.Controller;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information(SampleDataConst.LOG_STARTING_APP);

    var builder = WebApplication.CreateSlimBuilder(args);

    // 配置日志
    var logDir = Environment.GetEnvironmentVariable(ConfigConst.LOG_DIR_ENV) ?? ConfigConst.DEFAULT_LOG_DIR;
    builder.Host.UseSerilog((_, configuration) => configuration
        .MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(logDir, ConfigConst.LOG_FILE_TEMPLATE),
            rollingInterval: RollingInterval.Day));

    // 配置数据库
    var dbConnectionString = Environment.GetEnvironmentVariable(ConfigConst.DB_CONNECTION_ENV)
        ?? ConfigConst.DEFAULT_DB_CONNECTION;
    builder.Services.AddSingleton<ISqlSugarClient>(_ =>
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = dbConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });
        db.CodeFirst.InitTables(typeof(Todo));
        return db;
    });

    builder.Services.AddScoped<ITodoService, TodoService>();
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
    });
    builder.Services.AddOpenApi();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    // 注册 Controller 路由
    TodoController.RegisterRoutes(app);

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

/// <summary>
/// Todo 实体
/// </summary>
/// <param name="Id">ID</param>
/// <param name="Title">标题</param>
/// <param name="DueBy">截止日期</param>
/// <param name="IsComplete">是否完成</param>
[SugarTable("todos")]
public record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);

[JsonSerializable(typeof(Todo[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{

}