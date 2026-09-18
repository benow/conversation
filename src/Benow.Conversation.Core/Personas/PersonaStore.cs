using System.Text.Json;
using System.Text.Json.Serialization;
using Benow.Conversation.Config;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Personas;

/// <summary>
/// A persona is the BUNDLE (plan §4.9): system prompt + interaction class + speaker LLM +
/// TTS engine + voice. Switching persona switches all of them together.
/// Only what differs from the engine defaults is stored — unspecified fields pass through.
/// The system prompt is REQUIRED for a real persona ("if it's not different from the default,
/// it's not a persona"); the built-in Default pseudo-persona is pure pass-through and has none.
/// </summary>
public sealed class Persona
{
    public string Name { get; set; } = "";

    /// <summary>Required for user personas; empty for the built-in Default.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>roleplay | assistant | acquaintance — how the interaction is framed.</summary>
    public string InteractionClass { get; set; } = "assistant";

    // Optional overrides (null/empty = use the engine default).
    public string? SpeakerModel { get; set; }
    public string? TtsProvider { get; set; }
    public string? TtsModel { get; set; }
    public string? TtsVoice { get; set; }

    /// <summary>Built-in personas (Default + stock) can be edited but not deleted.</summary>
    public bool IsBuiltIn { get; set; }

    [JsonIgnore] public bool IsDefault => Name == PersonaStore.DefaultName;
}

/// <summary>
/// Persona library: the Default pseudo-persona, stock starting personas, CRUD, active selection,
/// and V1 `characters/*.md` import. Persisted as JSON next to the config.
/// </summary>
public sealed class PersonaStore
{
    public const string DefaultName = "Default";

    private readonly string _path;
    private readonly List<Persona> _personas = new();
    private string _active = DefaultName;

    public PersonaStore(string? path = null)
    {
        _path = path ?? Path.Combine(Path.GetDirectoryName(new ConfigStore().FilePath)!, "personas.json");
        Load();
    }

    public IReadOnlyList<Persona> All => _personas;
    public string ActiveName => _active;
    public Persona Active => Find(_active) ?? _personas[0];

    public Persona? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _personas.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Selects the active persona (unknown name → Default).</summary>
    public Persona SetActive(string? name)
    {
        var p = Find(name) ?? _personas[0];
        _active = p.Name;
        Save();
        return p;
    }

    /// <summary>
    /// Adds or replaces a persona. Throws for invalid input (a real persona needs a prompt) —
    /// the UI surfaces the message.
    /// </summary>
    public Persona Upsert(Persona persona)
    {
        if (string.IsNullOrWhiteSpace(persona.Name))
            throw new ArgumentException("Persona name is required.");
        if (persona.Name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("'Default' is reserved for the built-in pass-through persona.");
        if (string.IsNullOrWhiteSpace(persona.SystemPrompt))
            throw new ArgumentException("A persona needs a system prompt — a persona that changes nothing is not a persona.");

        var existing = Find(persona.Name);
        if (existing != null)
        {
            existing.SystemPrompt = persona.SystemPrompt;
            existing.InteractionClass = persona.InteractionClass;
            existing.SpeakerModel = persona.SpeakerModel;
            existing.TtsProvider = persona.TtsProvider;
            existing.TtsModel = persona.TtsModel;
            existing.TtsVoice = persona.TtsVoice;
        }
        else
        {
            _personas.Add(persona);
        }
        Save();
        return Find(persona.Name)!;
    }

    /// <summary>Removes a user persona (built-ins and the active Default are protected).</summary>
    public bool Delete(string name)
    {
        var p = Find(name);
        if (p == null || p.IsBuiltIn) return false;
        _personas.Remove(p);
        if (_active.Equals(name, StringComparison.OrdinalIgnoreCase)) _active = DefaultName;
        Save();
        return true;
    }

    /// <summary>
    /// Imports every `*.md` character file in a directory (V1's `characters/` folder). Existing
    /// personas with the same name are refreshed; failures are logged and skipped, not fatal.
    /// </summary>
    public int ImportCharactersDirectory(string directory, ILogger? logger = null)
    {
        if (!Directory.Exists(directory)) return 0;
        var imported = 0;
        foreach (var file in Directory.GetFiles(directory, "*.md").OrderBy(f => f))
        {
            try
            {
                ImportCharacterFile(file);
                imported++;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[personas] could not import {File} — {Reason}. Fix: check the " +
                    "front-matter block (--- key: value ---) in that file.", Path.GetFileName(file), ex.Message);
            }
        }
        if (imported > 0) Save();
        return imported;
    }

    /// <summary>
    /// Imports V1 `characters/*.md` personas: front-matter (--- key: value ---) → overrides
    /// (ReferenceAudio → voice, Model → speaker model), body → system prompt.
    /// </summary>
    public Persona ImportCharacterFile(string path)
    {
        var text = File.ReadAllText(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var prompt = text;
        string? voice = null;
        string? model = null;

        if (text.StartsWith("---"))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                var frontMatter = text[3..end];
                prompt = text[(end + 4)..].TrimStart();
                foreach (var line in frontMatter.Split('\n'))
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var key = line[..idx].Trim().ToLowerInvariant();
                    var value = line[(idx + 1)..].Trim().Trim('"');
                    if (key is "referenceaudio" or "reference_audio") voice = Path.GetFileName(value);
                    if (key == "model") model = value;
                }
            }
        }

        var persona = new Persona
        {
            Name = name,
            SystemPrompt = prompt.Trim(),
            InteractionClass = "roleplay",
            TtsVoice = voice,
            SpeakerModel = model
        };
        Upsert(persona);
        return persona;
    }

    private void Load()
    {
        _personas.Clear();
        _personas.Add(new Persona
        {
            Name = DefaultName,
            SystemPrompt = "",
            IsBuiltIn = true,
            InteractionClass = "assistant"
        });

        if (File.Exists(_path))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), ConversationJson.Options);
                if (doc != null)
                {
                    foreach (var p in doc.Personas.Where(p => !p.Name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase)))
                        _personas.Add(p);
                    _active = string.IsNullOrWhiteSpace(doc.Active) ? DefaultName : doc.Active;
                    if (Find(_active) == null) _active = DefaultName;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[personas] could not parse {_path}: {ex.Message} — starting from stock personas (file left untouched)");
            }
        }

        if (_personas.Count == 1) SeedStock();
    }

    /// <summary>Stock personas: editable starting points demonstrating the interaction classes.</summary>
    private void SeedStock()
    {
        _personas.Add(new Persona
        {
            Name = "Assistant",
            IsBuiltIn = true,
            InteractionClass = "assistant",
            SystemPrompt = "You are a helpful, precise assistant. Answer directly and concisely. " +
                           "When you are unsure, say so instead of guessing."
        });
        _personas.Add(new Persona
        {
            Name = "Acquaintance",
            IsBuiltIn = true,
            InteractionClass = "acquaintance",
            SystemPrompt = "You are a friendly acquaintance chatting casually. Keep replies short, warm and " +
                           "conversational, with the occasional question back. Avoid formal phrasing."
        });
        _personas.Add(new Persona
        {
            Name = "Storyteller",
            IsBuiltIn = true,
            InteractionClass = "roleplay",
            SystemPrompt = "You are a vivid storyteller. Respond in an engaging narrative voice, using sensory " +
                           "detail and natural pacing suited to being read aloud."
        });
        Save();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var file = new StoreFile { Active = _active, Personas = _personas.Where(p => !p.IsDefault).ToList() };
        File.WriteAllText(_path, JsonSerializer.Serialize(file, new JsonSerializerOptions(ConversationJson.Options) { WriteIndented = true }));
    }

    private sealed class StoreFile
    {
        public string Active { get; set; } = DefaultName;
        public List<Persona> Personas { get; set; } = new();
    }
}
