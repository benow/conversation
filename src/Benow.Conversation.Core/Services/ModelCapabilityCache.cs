using System.Collections.Concurrent;

namespace Benow.Conversation.Services;

/// <summary>
/// Short-lived cache of per-model tool-calling capability flags,
/// populated from provider metadata during model listing.
/// </summary>
public interface IModelCapabilityCache
{
    /// <summary>
    /// Returns the cached tool-calling capability for the given provider+model,
    /// or <c>null</c> if not present or expired.
    /// </summary>
    bool? TryGet(string providerInstanceId, string modelId);

    /// <summary>
    /// Stores a tool-calling capability entry with a ~5-minute TTL.
    /// </summary>
    void Set(string providerInstanceId, string modelId, bool? value);
}

/// <summary>
/// In-memory cache of (providerInstanceId, modelId) → toolCalling with a lazy 5-minute TTL.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Port of NASTV nastv-ai-plugin/Services/ModelCapabilityCache.cs (2026-09-17, phase 1).
/// </summary>
public sealed class ModelCapabilityCache : IModelCapabilityCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(string ProviderInstanceId, string ModelId), (bool? Value, DateTime ExpiresAt)> _cache = new();

    public bool? TryGet(string providerInstanceId, string modelId)
    {
        if (_cache.TryGetValue((providerInstanceId, modelId), out var entry))
        {
            if (entry.ExpiresAt > DateTime.UtcNow)
                return entry.Value;

            // Lazy eviction: remove stale entry
            _cache.TryRemove((providerInstanceId, modelId), out _);
        }

        return null;
    }

    public void Set(string providerInstanceId, string modelId, bool? value)
    {
        _cache[(providerInstanceId, modelId)] = (value, DateTime.UtcNow.Add(Ttl));
    }
}
