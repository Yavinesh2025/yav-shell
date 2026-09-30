using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Runs;

namespace Yav.Storage;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ChangeSet))]
[JsonSerializable(typeof(EvidenceBinding))]
[JsonSerializable(typeof(TokenCounts))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<Finding>))]
[JsonSerializable(typeof(List<JournalEntry>))]
internal sealed partial class StorageJson : JsonSerializerContext
{
    public static string Write<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.Serialize(value, info);

    public static T Read<T>(string json, JsonTypeInfo<T> info) =>
        JsonSerializer.Deserialize(json, info) ?? throw new InvalidDataException($"Stored {typeof(T).Name} is empty.");

    public static string WriteList(IReadOnlyList<string> values) => JsonSerializer.Serialize(values.ToList(), Default.ListString);

    public static IReadOnlyList<string> ReadList(string json) => JsonSerializer.Deserialize(json, Default.ListString) ?? [];
}
