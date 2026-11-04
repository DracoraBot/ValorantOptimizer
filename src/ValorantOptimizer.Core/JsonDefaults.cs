using System.Text.Json;
using System.Text.Json.Serialization;

namespace ValorantOptimizer.Core;

/// <summary>
/// Central JSON settings used for backups, elevation requests/responses and logs.
/// Property names are camelCase and enums are strings so files stay readable and versionable.
/// </summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
