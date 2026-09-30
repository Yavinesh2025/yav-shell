using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Yav.Core.Agents;

namespace Yav.Adapters;

public sealed record AdapterOptions(
    // Full path of the agent executable. Null means "find the default name on PATH".
    string? ExecutablePath = null,
    // Added to the agent's environment. Never logged.
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? RequestTimeout = null,
    TimeSpan? StartupTimeout = null,
    // The version an agent is told when it asks who is calling. Null means the version this was built with.
    string? ClientVersion = null,
    // Where the agent's diagnostic output is kept. Null means it is only kept in memory.
    string? DiagnosticsDirectory = null)
{
    public string EffectiveClientVersion => ClientVersion ?? typeof(AdapterOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(60);

    public TimeSpan EffectiveStartupTimeout => StartupTimeout ?? TimeSpan.FromSeconds(45);
}

/// <summary>The agent answered with an error, did not answer, or broke the protocol.</summary>
public class AgentProtocolException : AgentException
{
    public AgentProtocolException(string message, int? code = null, Exception? inner = null, bool refused = false)
        : base(message, inner, refused)
    {
        Code = code;
    }

    public int? Code { get; }
}

/// <summary>A session cannot be started because the credential would be exposed to code the project controls.</summary>
public sealed class CredentialIsolationException(string message) : AgentProtocolException(message, refused: true);

/// <summary>What is known about the sandbox the provider applies on this machine.</summary>
public sealed record SandboxStatus(bool OperatingSystemEnforced, string State, string Detail);

public interface ISandboxReporting
{
    Task<SandboxStatus> GetSandboxStatusAsync(CancellationToken cancellationToken);
}

/// <summary>Keeps the last part of an agent's diagnostic output, for error messages and /doctor.</summary>
internal sealed class DiagnosticTail
{
    private const int Limit = 16 * 1024;
    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();

    public void Append(string line)
    {
        lock (_gate)
        {
            _text.Append(line).Append('\n');
            if (_text.Length > Limit * 2)
            {
                _text.Remove(0, _text.Length - Limit);
            }
        }
    }

    public string Read(int maxCharacters = 600)
    {
        lock (_gate)
        {
            var text = _text.ToString().TrimEnd();
            return text.Length <= maxCharacters ? text : text[^maxCharacters..];
        }
    }
}

/// <summary>The event channel of a session, with the bookkeeping every adapter needs.</summary>
internal sealed class SessionEvents
{
    // Bounded: when the consumer is slow the adapter stops reading from the agent, which makes the agent wait.
    private readonly Channel<AgentEvent> _channel = Channel.CreateBounded<AgentEvent>(
        new BoundedChannelOptions(8192) { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });

    public ChannelReader<AgentEvent> Reader => _channel.Reader;

    public ValueTask PublishAsync(AgentEvent agentEvent) => _channel.Writer.WriteAsync(agentEvent);

    public bool TryPublish(AgentEvent agentEvent) => _channel.Writer.TryWrite(agentEvent);

    public void Complete() => _channel.Writer.TryComplete();
}

internal static class JsonReading
{
    public static string? Text(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long? Number(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    public static decimal? Money(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : null;

    public static bool? Flag(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    public static JsonElement? Child(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    /// <summary>Parses text that should be a JSON object. Returns null for anything else.</summary>
    public static JsonElement? ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
