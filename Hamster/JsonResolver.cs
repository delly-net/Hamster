using System.Text.Json.Serialization;
using Hamster.Attributing;
using Hamster.Modules.Example.Simple;
using Hamster.Modules.Example.Simple.Entities;

namespace Hamster;

/// <summary>
/// Json序列化上下文
/// </summary>
[MiniJsonResolver]
public sealed partial class JsonResolver;