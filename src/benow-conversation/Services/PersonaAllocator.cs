using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using benow_conversation.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

/// <summary>Allocates TTS voice personas to characters with gender matching, random rotation, and 48-hour staleness expiry.</summary>
public class PersonaAllocator : IPersonaAllocator
{
    private readonly ConcurrentDictionary<string, string> _characterToPersona = new();
    private readonly Dictionary<string, PersonaUsageEntry> _usage;
    private readonly string _currentModel;
    private readonly Dictionary<string, VoicePersona> _personas;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly string? _configPath;
    private readonly ILogger<PersonaAllocator> _logger;

    public PersonaAllocator(IOptions<AppSettings> settings, ILogger<PersonaAllocator> logger)
    {
        var s = settings.Value;
        _logger = logger;
        _currentModel = s.OpenRouter.TtsModel;
        _personas = new Dictionary<string, VoicePersona>(s.Personas);
        _usage = new Dictionary<string, PersonaUsageEntry>(s.PersonaUsage);
        _configPath = FindConfigPath();

        foreach (var kvp in s.CharacterAssignments)
        {
            if (_personas.ContainsKey(kvp.Value))
            {
                _characterToPersona[kvp.Key] = kvp.Value;
                _logger.LogInformation("Restored character '{Character}' → persona '{Persona}' from persisted state", kvp.Key, kvp.Value);
            }
        }
    }

    /// <inheritdoc/>
    public string? AllocateForCharacter(string name, string gender)
    {
        if (string.Equals(name, "Self", StringComparison.OrdinalIgnoreCase))
        {
            var selfPersona = _personas.FirstOrDefault(kvp => kvp.Value.IsSelf);
            if (selfPersona.Key != null)
            {
                _logger.LogInformation("Allocated self persona '{PersonaKey}' (voice={Voice}) to character 'Self'",
                    selfPersona.Key, selfPersona.Value.Voice);
                _characterToPersona[name] = selfPersona.Key;
                return selfPersona.Key;
            }
            _logger.LogWarning("No self persona found (IsSelf=true) — falling through to normal allocation for 'Self'");
        }

        if (_characterToPersona.TryGetValue(name, out var existing))
        {
            if (_personas.TryGetValue(existing, out var mapped) && mapped.Enabled)
            {
                _logger.LogDebug("Character '{Name}' already mapped to persona '{PersonaKey}'", name, existing);
                return existing;
            }
            _logger.LogInformation("Character '{Name}' was mapped to disabled persona '{PersonaKey}' — reassigning", name, existing);
            _characterToPersona.TryRemove(name, out _);
        }

        EvictStale();

        var assignedKeys = new HashSet<string>(_characterToPersona.Values);
        var available = _personas
            .Where(kvp =>
                kvp.Value.Enabled &&
                (kvp.Value.Model == _currentModel || kvp.Value.IsDefault || string.IsNullOrEmpty(kvp.Value.Model)) &&
                (kvp.Value.Gender == gender || string.IsNullOrEmpty(kvp.Value.Gender)) &&
                !assignedKeys.Contains(kvp.Key))
            .Select(kvp => kvp.Key)
            .ToList();

        string? selectedKey;

        if (available.Count == 0)
        {
            _logger.LogWarning("No available persona for character '{Name}' (gender={Gender}, model={Model})", name, gender, _currentModel);

            var defaultPersona = _personas.FirstOrDefault(p => p.Value.IsDefault && p.Value.Enabled);
            if (defaultPersona.Key != null)
            {
                selectedKey = defaultPersona.Key;
            }
            else
            {
                var fallback = _personas.FirstOrDefault(p =>
                    p.Value.Enabled && p.Value.Model == _currentModel && (p.Value.Gender == gender || string.IsNullOrEmpty(p.Value.Gender)));
                selectedKey = fallback.Key;
            }

            if (selectedKey == null)
            {
                _logger.LogWarning("No fallback persona for character '{Name}'", name);
                return null;
            }
        }
        else
        {
            selectedKey = available[Random.Shared.Next(available.Count)];
        }

        _logger.LogInformation("Allocated persona '{PersonaKey}' (voice={Voice}, temp={Temp}) to character '{Name}' (gender={Gender})",
            selectedKey, _personas[selectedKey].Voice, _personas[selectedKey].Temperature, name, gender);

        _characterToPersona[name] = selectedKey;
        _usage[selectedKey] = new PersonaUsageEntry { LastCharacter = name, LastUsedUtc = DateTime.UtcNow };
        PersistPersonaUsage();

        return selectedKey;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _characterToPersona.Clear();
        _usage.Clear();
        PersistPersonaUsage();
        _logger.LogInformation("All character assignments and usage history cleared");
    }

    /// <inheritdoc/>
    public VoicePersona? GetPersona(string personaKey)
    {
        return _personas.TryGetValue(personaKey, out var persona) ? persona : null;
    }

    private void EvictStale()
    {
        var threshold = TimeSpan.FromHours(48);
        var stale = _characterToPersona
            .Where(kvp => _usage.TryGetValue(kvp.Value, out var entry) &&
                          DateTime.UtcNow - entry.LastUsedUtc > threshold)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in stale)
            _characterToPersona.TryRemove(key, out _);
    }

    private void PersistPersonaUsage()
    {
        if (_configPath == null) return;

        _fileLock.Wait();
        try
        {
            if (!File.Exists(_configPath)) return;

            var json = File.ReadAllText(_configPath);
            var node = JsonNode.Parse(json);
            if (node == null) return;

            node["PersonaUsage"] = JsonSerializer.SerializeToNode(_usage);
            node["CharacterAssignments"] = JsonSerializer.SerializeToNode(_characterToPersona);
            var output = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, output);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist persona usage to {Path}", _configPath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static string? FindConfigPath()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "appsettings.json");
            if (File.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir)
                break;
            dir = parent;
        }

        return null;
    }
}
