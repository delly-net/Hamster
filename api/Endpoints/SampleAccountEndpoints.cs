using Hamster.Api.Constant;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 【示例端点】账户接口，用于验证「端点 → 服务 → SqlSugar → PostgreSQL」链路是否打通。
/// 属于框架示例，不是最终业务接口。
/// </summary>
public sealed class SampleAccountEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.SAMPLE_ACCOUNT_GROUP).WithTags("示例：账户");

        group.MapGet("/", async (ISampleAccountService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.GetAllAsync(cancellationToken)))
            .WithName("GetSampleAccounts")
            .WithSummary("【示例】查询账户列表");

        group.MapPost("/", async (
                CreateSampleAccountRequest request,
                ISampleAccountService service,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["name"] = ["账户名称不能为空"],
                    });
                }

                var account = await service.CreateAsync(request.Name.Trim(), request.Balance, cancellationToken);
                return Results.Created($"{ApiPathConst.SAMPLE_ACCOUNT_GROUP}/{account.Id}", account);
            })
            .WithName("CreateSampleAccount")
            .WithSummary("【示例】创建账户");
    }
}

/// <summary>【示例】创建账户请求体。</summary>
/// <param name="Name">账户名称。</param>
/// <param name="Balance">初始余额。</param>
public sealed record CreateSampleAccountRequest(string? Name, decimal Balance);
