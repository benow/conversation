namespace Benow.Conversation.Providers;

/// <summary>Port of NASTV nastv-ai-plugin/Providers/AiProviderRegistry.cs (2026-09-17, phase 1).</summary>
public static class AiProviderRegistry
{
    public static readonly IAiProvider OpenRouter = new OpenRouterProvider();
    public static readonly IAiProvider OpenAiCompatible = new OpenAiCompatibleProvider();
    public static readonly IAiProvider Replicate = new ReplicateProvider();
    public static readonly IAiProvider Groq = new GroqProvider();

    private static readonly Dictionary<string, IAiProvider> _providers = new()
    {
        [OpenRouter.Id] = OpenRouter,
        [OpenAiCompatible.Id] = OpenAiCompatible,
        [Replicate.Id] = Replicate,
        [Groq.Id] = Groq
    };

    public static IReadOnlyList<IAiProvider> All => _providers.Values.ToList();

    public static IAiProvider? Get(string id) =>
        _providers.TryGetValue(id, out var p) ? p : null;

    public static IReadOnlyList<IAiProvider> ForCapability(AiCapability cap) =>
        All.Where(p => (p.Capabilities & cap) != 0).ToList();

    /// <summary>All provider IDs as SelectOption objects for settings dropdowns.</summary>
    public static SelectOption[] ProviderOptions() =>
        All.Select(p => new SelectOption(p.Id, p.DisplayName)).ToArray();
}
