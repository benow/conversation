using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using benow_conversation.Configuration;
using benow_conversation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

/// <summary>In-memory + config-persisted cache of audio formats per provider/model key.</summary>
public class ProviderFormatCache
{
    private readonly ConcurrentDictionary<string, AudioFormat> _cache = new();
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<ProviderFormatCache> _logger;
    private readonly object _writeLock = new();
    private bool _loaded;
    private string? _resolvedConfigPath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ProviderFormatCache(IOptionsMonitor<AppSettings> settings, ILogger<ProviderFormatCache> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Look up format for a provider/model key. Returns null if not yet known.</summary>
    public virtual AudioFormat? Get(string key)
    {
        EnsureLoaded();
        return _cache.TryGetValue(key, out var format) ? format : null;
    }

    /// <summary>Store format in memory and persist to appsettings.json.</summary>
    public virtual void Set(string key, AudioFormat format)
    {
        _cache[key] = format;
        Persist();
    }

    /// <summary>Load all entries from AppSettings.ProviderFormats into the in-memory cache.</summary>
    public virtual void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_writeLock)
        {
            if (_loaded) return;

            var current = _settings.CurrentValue;
            if (current.ProviderFormats != null)
            {
                foreach (var (key, format) in current.ProviderFormats)
                {
                    if (!_cache.ContainsKey(key))
                        _cache[key] = format;
                    else
                        _cache.TryGetValue(key, out _);
                }
            }
            _loaded = true;
            _logger.LogInformation("ProviderFormatCache loaded {Count} entries", _cache.Count);
        }
    }

    /// <summary>Clear all entries from memory and config.</summary>
    public virtual void Clear()
    {
        _cache.Clear();
        Persist();
        _logger.LogInformation("ProviderFormatCache cleared");
    }

    /// <summary>Get all entries in memory (for serialization).</summary>
    public IReadOnlyDictionary<string, AudioFormat> GetAll() => _cache;

    private void Persist()
    {
        lock (_writeLock)
        {
            try
            {
                _resolvedConfigPath ??= Path.Combine(TtsService.FindProjectRoot(), "appsettings.json");

                string json;
                using (var fs = File.OpenRead(_resolvedConfigPath))
                {
                    var doc = JsonDocument.Parse(fs);
                    using var stream = new MemoryStream();
                    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                    doc.WriteTo(writer);
                    writer.Flush();
                    json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                }

                var appSettings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts)!;
                appSettings.ProviderFormats = _cache.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value);

                var outputJson = JsonSerializer.Serialize(appSettings, JsonOpts);
                File.WriteAllText(_resolvedConfigPath, outputJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist ProviderFormatCache to config");
            }
        }
    }
}
