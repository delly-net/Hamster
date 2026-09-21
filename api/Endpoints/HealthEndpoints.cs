using Hamster.Api.Config;
using Hamster.Api.Constant;
using SqlSugar;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 健康检查端点：<c>/health</c> 存活探针、<c>/health/db</c> 数据库探针。
/// </summary>
public sealed class HealthEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.HEALTH_GROUP).WithTags("健康检查");

        group.MapGet("/", () => Results.Ok(new
            {
                status = "ok",
                service = "Hamster.Api",
                timestamp = DateTimeOffset.UtcNow,
            }))
            .WithName("GetHealth")
            .WithSummary("存活探针")
            .WithDescription("仅表示进程可正常响应，不检测数据库连通性。");

        group.MapGet("/db", (ISqlSugarClient db, DatabaseOptions options, ILogger<HealthEndpoints> logger) =>
            {
                try
                {
                    // 用一次真实往返查询判断连通性；CheckConnection() 在部分版本为 void，无法反映结果
                    var probe = db.Ado.GetScalar("SELECT 1");
                    if (probe is not null)
                    {
                        return Results.Ok(new
                        {
                            status = "ok",
                            database = options.DbTypeLabel,
                            connected = true,
                        });
                    }

                    return DatabaseUnavailable(options.DbTypeLabel, "探活查询未返回结果");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "数据库健康检查失败");
                    return DatabaseUnavailable(options.DbTypeLabel, ex.Message);
                }
            })
            .WithName("GetHealthDatabase")
            .WithSummary("数据库探针")
            .WithDescription("检测当前数据库（Sqlite 或 PostgreSQL）连通性；不可用时返回 503，便于容器编排做就绪判断。");
    }

    private static IResult DatabaseUnavailable(string dbTypeLabel, string? error) =>
        Results.Json(
            new
            {
                status = "unavailable",
                database = dbTypeLabel,
                connected = false,
                error,
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
