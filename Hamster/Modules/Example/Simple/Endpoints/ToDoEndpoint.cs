using Hamster.Modules.Example.Simple.Service;
using Hamster.Modules.Example.Simple.Services;

namespace Hamster.Modules.Example.Simple.Endpoints;

/// <summary>
/// ToDo Endpoint
/// </summary>
public sealed partial class ToDoEndpoint
{
    public Delegate GetAllTodos = (IDatabaseService databaseService) =>
    {
        var todos = TodoService.GetAllTodos(databaseService);
        return Results.Ok(todos);
    };
}
