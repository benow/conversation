namespace Benow.Conversation.Providers;

/// <summary>
/// Neutral settings-field descriptor (port of NASTV's SDK PluginConfigField — the plugin
/// maps these 1:1 onto PluginConfigField at phase-4 adoption; the desktop app renders them
/// directly). Type is a UI hint: "password" (secret), "string" (e.g. base URL).
/// </summary>
public sealed class ProviderConfigField
{
    public required string Key { get; init; }
    public required string Type { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public string? Placeholder { get; init; }
}

/// <summary>Dropdown option (neutral port of the SDK SelectOption).</summary>
public sealed record SelectOption(string Value, string Label);

[Flags]
public enum AiCapability
{
    None = 0,
    Embedding = 1,
    Tts = 2,
    Stt = 4,
    Llm = 8
}

public record ModelInfo(string Id, string DisplayName, bool? ToolCalling = null);

public interface IAiProvider
{
    string Id { get; }
    string DisplayName { get; }
    AiCapability Capabilities { get; }

    /// <summary>Config fields this provider needs rendered on the settings page.</summary>
    ProviderConfigField[] GetConfigFields();

    /// <summary>Fetch available models from the provider's API. Returns empty list if unsupported.</summary>
    Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string apiKey, string? baseUrl, HttpClient http, CancellationToken ct);

    /// <summary>Base URL for the provider's API, or empty string if user supplies it (OpenAI-compatible).</summary>
    string GetBaseUrl();
}
