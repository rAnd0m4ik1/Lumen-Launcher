using System.Text.Json;
using System.Text.Json.Serialization;

namespace CraftDeck.Services;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Load<T>(string text) => JsonSerializer.Deserialize<T>(text, Options);

    public static string Save(object value) => JsonSerializer.Serialize(value, Options);

    public static JsonDocument Document(string text) => JsonDocument.Parse(text);
}
