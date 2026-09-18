using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventLoom;

/// <summary>Configures deterministic JSON serialization for registered domain events.</summary>
public sealed class EventSerializationOptions
{
    private readonly List<JsonConverter> converters = [];

    /// <summary>Gets converters that are added in registration order.</summary>
    public IList<JsonConverter> Converters => converters;

    /// <summary>Gets or sets the property naming policy. The default is camel case.</summary>
    public JsonNamingPolicy? PropertyNamingPolicy { get; set; } = JsonNamingPolicy.CamelCase;

    /// <summary>Gets or sets whether JSON property names are matched case-insensitively. The default is <see langword="false"/>.</summary>
    public bool PropertyNameCaseInsensitive { get; set; }

    /// <summary>Gets or sets number handling. The default is strict JSON number handling.</summary>
    public JsonNumberHandling NumberHandling { get; set; } = JsonNumberHandling.Strict;

    /// <summary>Gets or sets reference handling. The default writes references as ordinary event JSON.</summary>
    public ReferenceHandler? ReferenceHandler { get; set; }

    internal JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = PropertyNamingPolicy,
            PropertyNameCaseInsensitive = PropertyNameCaseInsensitive,
            NumberHandling = NumberHandling,
            ReferenceHandler = ReferenceHandler
        };

        foreach (var converter in converters)
        {
            options.Converters.Add(converter ?? throw new InvalidOperationException(
                "Event serialization converters cannot contain null entries."));
        }

        return options;
    }
}
