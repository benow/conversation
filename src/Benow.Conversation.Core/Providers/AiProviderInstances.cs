using System.Text.Json;

namespace Benow.Conversation.Providers;

/// <summary>
/// The minimal config surface the catalog helpers need: the AiProviders catalog JSON plus
/// the legacy flat keys. NASTV's PluginConfig implements this as-is at phase-4 adoption;
/// the desktop app's settings POCO implements it too.
/// </summary>
public interface ILegacyProviderConfig
{
    string AiProviders { get; }
    string OpenRouterApiKey { get; }
    string OpenAiCompatUrl { get; }
    string OpenAiCompatApiKey { get; }
    string ReplicateApiKey { get; }
    string GroqApiKey { get; }
}

/// <summary>
/// A configured instance of an AI provider type (e.g. "My OpenRouter" of type "openrouter").
/// Stored as a JSON array under the "AiProviders" key — the single source of truth that
/// round-trips NASTV config.json ↔ typed config ↔ DB plugin_settings, and the desktop
/// settings file. Params are keyed by the provider's config field keys (e.g.
/// "OpenRouterApiKey"), so settings UIs render them generically by field type.
/// Port of NASTV nastv-ai-plugin/Models/AiProviderInstances.cs (2026-09-17, phase 1).
/// </summary>
public class AiProviderInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string TypeId { get; set; } = "";
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Parse/serialize/resolve helpers for the AiProviders instance catalog.
/// Legacy synthesis keeps default config values (AIProvider="openrouter", SttProvider="groq", ...)
/// resolvable after upgrade before the catalog is persisted for the first time.
/// </summary>
public static class AiProviderInstances
{
    // Shared wire-JSON convention (camelCase + case-insensitive) — mirrors NastvShared.JsonDefaults.
    public static List<AiProviderInstance> Parse(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? new List<AiProviderInstance>()
            : JsonSerializer.Deserialize<List<AiProviderInstance>>(json, ConversationJson.Options) ?? new();

    public static string Serialize(IEnumerable<AiProviderInstance> instances) =>
        JsonSerializer.Serialize(instances.ToList(), ConversationJson.Options);

    /// <summary>
    /// All configured instances; when the catalog is empty, synthesizes one instance per
    /// known provider type with ids equal to the type ids (so legacy flat config values resolve).
    /// </summary>
    public static List<AiProviderInstance> ResolveAll(ILegacyProviderConfig cfg)
    {
        var instances = Parse(cfg.AiProviders);
        if (instances.Count == 0)
        {
            // Synthesize one instance per provider type from legacy flat config keys.
            // Skip types that have no configured values — e.g. OpenAI Compatible with
            // no URL or key is an unwanted ghost entry that pollutes the catalog UI.
            var synthesized = new List<AiProviderInstance>
            {
                Legacy("openrouter", "OpenRouter", new Dictionary<string, string>
                {
                    ["OpenRouterApiKey"] = cfg.OpenRouterApiKey
                }),
                Legacy("openai-compatible", "OpenAI Compatible", new Dictionary<string, string>
                {
                    ["OpenAiCompatUrl"] = cfg.OpenAiCompatUrl,
                    ["OpenAiCompatApiKey"] = cfg.OpenAiCompatApiKey
                }),
                Legacy("replicate", "Replicate", new Dictionary<string, string>
                {
                    ["ReplicateApiKey"] = cfg.ReplicateApiKey
                }),
                Legacy("groq", "Groq", new Dictionary<string, string>
                {
                    ["GroqApiKey"] = cfg.GroqApiKey
                })
            };
            instances = synthesized.Where(i => i.Params.Values.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
        }
        return instances;

        static AiProviderInstance Legacy(string id, string name, Dictionary<string, string> p) => new()
        {
            Id = id,
            Name = name,
            TypeId = id,
            Params = p
        };
    }

    public static AiProviderInstance? Find(ILegacyProviderConfig cfg, string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        var all = ResolveAll(cfg);
        var match = all.FirstOrDefault(i => i.Id == instanceId)
            // Legacy type id (e.g. default TtsProvider="replicate") while the catalog holds a
            // real instance of that type with the API key in Params — resolve to THAT instance,
            // not an empty synthesized one (root cause of the 2026-08-19 TTS "No API key" outage).
            ?? all.FirstOrDefault(i => i.TypeId == instanceId);
        if (match != null) return match;
        // No catalog instance has this id, but it may be a legacy type id (e.g.
        // AIProvider="openrouter") persisted before the catalog existed, or referencing a
        // provider whose instance was renamed. If it names a known provider type, synthesize
        // a legacy instance so ResolveApiKey/ResolveBaseUrl fall back to the flat legacy
        // config keys (OpenRouterApiKey, ...) instead of resolving to empty.
        return AiProviderRegistry.Get(instanceId) == null
            ? null
            : new AiProviderInstance { Id = instanceId, Name = instanceId, TypeId = instanceId, Params = new() };
    }

    /// <summary>
    /// Provider type behind an id: accepts either a legacy type id ("groq") or an
    /// instance id ("a1b2c3d4"). Returns null for unknown ids.
    /// </summary>
    public static IAiProvider? ResolveProviderType(ILegacyProviderConfig cfg, string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        var direct = AiProviderRegistry.Get(instanceId);
        if (direct != null) return direct;
        var instance = Find(cfg, instanceId);
        return instance == null ? null : AiProviderRegistry.Get(instance.TypeId);
    }

    /// <summary>
    /// API key for an instance: the value of its password-typed config field, falling
    /// back to the legacy flat key for that provider type when the param is empty.
    /// </summary>
    public static string ResolveApiKey(ILegacyProviderConfig cfg, AiProviderInstance instance)
    {
        var provider = AiProviderRegistry.Get(instance.TypeId);
        if (provider == null) return "";
        var passwordField = provider.GetConfigFields().FirstOrDefault(f => f.Type == "password");
        if (passwordField != null &&
            instance.Params.TryGetValue(passwordField.Key, out var v) &&
            !string.IsNullOrWhiteSpace(v))
            return v;
        return LegacyFlatKey(cfg, instance.TypeId);
    }

    /// <summary>
    /// Base URL for an instance: the provider's built-in default when it has one, otherwise
    /// the instance's string-typed URL field, falling back to the legacy OpenAiCompatUrl.
    /// </summary>
    public static string ResolveBaseUrl(ILegacyProviderConfig cfg, AiProviderInstance instance)
    {
        var provider = AiProviderRegistry.Get(instance.TypeId);
        if (provider == null) return "";
        var defaultUrl = provider.GetBaseUrl();
        if (!string.IsNullOrWhiteSpace(defaultUrl)) return defaultUrl;

        var urlField = provider.GetConfigFields().FirstOrDefault(f => f.Type == "string");
        if (urlField != null &&
            instance.Params.TryGetValue(urlField.Key, out var url) &&
            !string.IsNullOrWhiteSpace(url))
            return url;
        return cfg.OpenAiCompatUrl ?? "";
    }

    /// <summary>Set flag names of an AiCapability mask, e.g. ["Embedding","Llm"].</summary>
    public static string[] CapabilityNames(AiCapability caps)
    {
        var names = new List<string>();
        foreach (var name in Enum.GetNames<AiCapability>())
        {
            if (name == "None") continue;
            if ((caps & (AiCapability)Enum.Parse(typeof(AiCapability), name)) != 0)
                names.Add(name);
        }
        return names.ToArray();
    }

    private static string LegacyFlatKey(ILegacyProviderConfig cfg, string providerId) => providerId switch
    {
        "openrouter" => cfg.OpenRouterApiKey,
        "openai-compatible" => cfg.OpenAiCompatApiKey,
        "replicate" => cfg.ReplicateApiKey,
        "groq" => cfg.GroqApiKey,
        _ => ""
    };
}
