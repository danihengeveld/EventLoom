using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EventLoom;

/// <summary>Configures deterministic JSON serialization for registered domain events.</summary>
public sealed class EventSerializationOptions
{
    private readonly List<JsonSerializerContext> contexts = [];
    private readonly List<JsonConverter> converters = [];

    /// <summary>Gets source-generated contexts that are composed in registration order.</summary>
    public IList<JsonSerializerContext> Contexts => contexts;

    /// <summary>Gets converters that are added in registration order.</summary>
    public IList<JsonConverter> Converters => converters;

    /// <summary>Gets or sets whether reflection metadata is used when composed resolvers do not contain a type. The default is <see langword="true"/>.</summary>
    public bool ReflectionFallback { get; set; } = true;

    /// <summary>Gets or sets the property naming policy. The default is camel case.</summary>
    public JsonNamingPolicy? PropertyNamingPolicy { get; set; } = JsonNamingPolicy.CamelCase;

    /// <summary>Gets or sets whether JSON property names are matched case-insensitively. The default is <see langword="false"/>.</summary>
    public bool PropertyNameCaseInsensitive { get; set; }

    /// <summary>Gets or sets number handling. The default is strict JSON number handling.</summary>
    public JsonNumberHandling NumberHandling { get; set; } = JsonNumberHandling.Strict;

    /// <summary>Gets or sets reference handling. The default writes references as ordinary event JSON.</summary>
    public ReferenceHandler? ReferenceHandler { get; set; }

    /// <summary>Gets or sets an additional metadata resolver composed after registered contexts.</summary>
    public IJsonTypeInfoResolver? TypeInfoResolver { get; set; }

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

        var resolvers = contexts.Cast<IJsonTypeInfoResolver>()
            .Concat(TypeInfoResolver is null ? [] : [TypeInfoResolver])
            .ToList();
        if (ReflectionFallback)
        {
            resolvers.Add(new DefaultJsonTypeInfoResolver());
        }

        if (resolvers.Count > 0 || !ReflectionFallback)
        {
            options.TypeInfoResolver = JsonTypeInfoResolver.Combine(resolvers.ToArray());
        }

        return options;
    }
}
