using System.Text.Json.Serialization;

namespace Oblachka;

public sealed partial record ClientSnapshot
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServiceRoute[]? ServiceRoutes { get; init; }
}
