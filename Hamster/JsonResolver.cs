using Hamster.Attributing;
using Hamster.Modules.Example.Simple.Entities;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Hamster;

/// <summary>
/// Json序列化上下文
/// </summary>
[MiniJsonResolver]
public partial class JsonResolver;