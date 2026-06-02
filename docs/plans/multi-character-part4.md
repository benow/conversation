# Multi-Character TTS — Part 4: Configuration & Implementation

## New Files to Create

| File | Purpose |
|---|---|
| `Services/CharacterParser.cs` | Regex parsing: `[Name]`, `[Name:G]`, `[thought]`, `(modifier)` → `List<CharacterSegment>` |
| `Services/PersonaAllocator.cs` | Character → persona random mapping, 48h expiry, `/reset`, state persistence |
| `Services/IPersonaAllocator.cs` | Interface for PersonaAllocator |
| `Services/ModifierInjector.cs` | LLM modifier injection with fallback chain |
| `Services/IModifierInjector.cs` | Interface for ModifierInjector |
| `Services/ParallelTtsPlayer.cs` | Parallel TTS synthesis + producer-consumer sequential playback |

## Existing Files to Modify

| File | Changes |
|---|---|
| `Configuration/AppSettings.cs` | Add `PersonaUsage`, `MultiCharacter` properties; add `Gender`, `ThoughtInstructions` to `VoicePersona` |
| `appsettings.json` | Add 12 new personas (`female-2` through `female-13`), add `MultiCharacter` section |
| `Services/ProxyService.cs` | Multi-character detection in `StreamWithTtsAsync` and `NonStreamWithTtsAsync`; `/reset` interception |
| `Program.cs` | DI registration for new services; named HttpClient for ModifierInjector |

## Configuration Schema

### New: MultiCharacterSettings

```json
{
  "MultiCharacter": {
    "ModifierModels": ["z-ai/glm-4.5-air:free", "deepseek/deepseek-v4-flash"],
    "ModifierTimeoutMs": 5000,
    "AutoInjectModifiers": true,
    "ModifierSystemPrompt": "You are a voice director annotating..."
  }
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `ModifierModels` | `List<string>` | `["z-ai/glm-4.5-air:free", "deepseek/deepseek-v4-flash"]` | Ordered fallback chain of OpenRouter model IDs for modifier injection. First model tried; on failure, next in list. |
| `ModifierTimeoutMs` | int | `5000` | Per-model timeout in ms. Each model in the fallback chain gets this timeout independently. |
| `AutoInjectModifiers` | bool | `true` | Enable LLM-driven modifier injection. If false, raw text passes through to parser without annotation. |
| `ModifierSystemPrompt` | string | *(see Part 3)* | System prompt for the modifier injection LLM. Override to tune modifier behavior without code changes. |

### New: PersonaUsage (runtime state, auto-created)

```json
{
  "PersonaUsage": {
    "female-1": {
      "LastCharacter": "Sofia",
      "LastUsedUtc": "2026-05-29T03:00:00Z"
    }
  }
}
```

This section is managed at runtime by `PersonaAllocator`. It does not need to exist in the initial config file — it is created on first allocation. Persisted to `appsettings.json` after each allocation change (same file I/O pattern as `SavePersona` in `Program.cs:608-665`).

### Modified: VoicePersona

New fields added to existing `VoicePersona` class:

| Field | Type | Default | Description |
|---|---|---|---|
| `Gender` | string? | `null` | Explicit gender hint: `"F"` or `"M"`. If null, defaults to the requested gender (assumed configured correctly). Named voice IDs (marin, nova, etc.) have no gender prefix — the field should always be set for the current TTS model. |
| `ThoughtInstructions` | string? | `null` | Instruction suffix appended to `OpenAiInstructions` for `[thought]` segments, joined by `. ` separator. Each persona defines its own thought style (see Part 1 roster). |

### Full appsettings.json (multi-character sections only)

Only the sections that change are shown. All existing sections (`OpenRouter`, `Audio`, `Logging`, `Playback`, `Proxy`, `Stt`, `Groq`, `TranscriptCleanupOff`, `OutputProfiles`) remain unchanged.

```json
{
  "Personas": {
    "female-1": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "marin",
      "OpenAiInstructions": "young, british, sexy, flirty, continually interested",
      "Gender": "F",
      "ThoughtInstructions": "speak in a lower, deeper register, reflective and intimate, as if thinking to yourself",
      "Temperature": 0.7,
      "IsDefault": true
    },
    "female-2": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "nova",
      "OpenAiInstructions": "warm australian, empathetic, curious, animated, breathless when excited",
      "Gender": "F",
      "ThoughtInstructions": "speak in a softer, more contemplative register, as if reflecting quietly to yourself",
      "Temperature": 0.7
    },
    "female-3": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "coral",
      "OpenAiInstructions": "soft-spoken, dreamy, slightly distant, poetic, ethereal quality",
      "Gender": "F",
      "ThoughtInstructions": "speak barely above a whisper, floating and detached, lost in imagination",
      "Temperature": 0.8
    },
    "female-4": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "shimmer",
      "OpenAiInstructions": "composed, articulate, measured, dry wit, understated warmth",
      "Gender": "F",
      "ThoughtInstructions": "speak in a lower, more analytical register, precise and methodical",
      "Temperature": 0.5
    },
    "female-5": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "fable",
      "OpenAiInstructions": "rich, theatrical, expressive, playful, dramatic flair, loves words",
      "Gender": "F",
      "ThoughtInstructions": "speak in a hushed, reverent tone, as if reading from a beloved book",
      "Temperature": 0.75
    },
    "female-6": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "verse",
      "OpenAiInstructions": "casual, laid-back, witty, irreverent, dry humor, easy laughter",
      "Gender": "F",
      "ThoughtInstructions": "speak in a musing, lazy cadence, daydreaming out loud",
      "Temperature": 0.8
    },
    "female-7": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "sage",
      "OpenAiInstructions": "calm, authoritative, patient, reassuring, gentle gravitas, speaks from experience",
      "Gender": "F",
      "ThoughtInstructions": "speak in a quiet, measured tone, drawing on deep wisdom, contemplative",
      "Temperature": 0.4
    },
    "female-8": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "ash",
      "OpenAiInstructions": "youthful, wide-eyed, eager, sweet, earnest, slightly nervous energy",
      "Gender": "F",
      "ThoughtInstructions": "speak in a tiny, wondering voice, discovering something for the first time",
      "Temperature": 0.65
    },
    "female-9": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "ballad",
      "OpenAiInstructions": "lush, passionate, sensual, flowing cadence, dramatic, romantic intensity",
      "Gender": "F",
      "ThoughtInstructions": "speak in a breathy, longing register, intimate and vulnerable",
      "Temperature": 0.75
    },
    "female-10": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "echo",
      "OpenAiInstructions": "low, measured, enigmatic, controlled, magnetic undertone, speaks little",
      "Gender": "F",
      "ThoughtInstructions": "speak in a barely audible murmur, dark and private, guarding secrets",
      "Temperature": 0.5
    },
    "female-11": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "alloy",
      "OpenAiInstructions": "neutral, adaptable, clear, balanced, versatile, mirrors the conversation",
      "Gender": "F",
      "ThoughtInstructions": "speak in a quiet internal voice, processing and adapting",
      "Temperature": 0.6
    },
    "female-12": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "onyx",
      "OpenAiInstructions": "sharp, piercing, commanding, fierce, magnetic, does not waste words",
      "Gender": "F",
      "ThoughtInstructions": "speak in a tightly controlled, simmering intensity, restrained fury",
      "Temperature": 0.55
    },
    "female-13": {
      "Model": "openai/gpt-4o-mini-tts-2025-12-15",
      "Voice": "cedar",
      "OpenAiInstructions": "grounded, warm, nurturing, slow cadence, deeply soothing, ancient wisdom",
      "Gender": "F",
      "ThoughtInstructions": "speak in a deep, slow rumination, connected to something ancient",
      "Temperature": 0.45
    }
  },
  "MultiCharacter": {
    "ModifierModels": ["z-ai/glm-4.5-air:free", "deepseek/deepseek-v4-flash"],
    "ModifierTimeoutMs": 5000,
    "AutoInjectModifiers": true,
    "ModifierSystemPrompt": "You are a voice director annotating a multi-character script for text-to-speech synthesis.\n\nYour job: read the script and insert exactly ONE speech modifier in parentheses before lines that would benefit from expressive delivery guidance. Leave lines that need no special treatment without a modifier.\n\nAvailable modifiers — use these and ONLY these:\n  (whisper)       — very soft, hushed, barely audible\n  (laughing)      — speaking while amused, warm chuckle in voice\n  (thoughtful)    — slower, contemplative, measured\n  (angry)         — frustrated, raised intensity, sharp\n  (sad)           — subdued, melancholic, quiet heaviness\n  (excited)       — energetic, breathless, animated\n  (sigh)          — let out a soft sigh, then speak\n  (quiet)         — intimate, soft-spoken, close-mic\n  (narrate)       — detached, story-telling voice\n  (flirtatious)   — playful, teasing, seductive undertone\n  (teasing)       — mock-serious, playful ribbing\n  (sudden)        — startled, quick intake of breath then speak\n\nRules:\n- Preserve ALL character markers like [Name] and [Name:G] exactly as-is.\n- Preserve ALL [thought]...[/thought] tags exactly as-is — do NOT add modifiers inside thought tags.\n- Insert the modifier between the character marker and the first word of dialogue: [Name](modifier)text...\n- Do NOT add a modifier to every line — only where it genuinely enhances delivery. A natural script has 30-50% of lines modified.\n- If a line already has a modifier, leave it untouched.\n- Consider the emotional arc: early lines may be neutral, building tension leads to (excited)/(angry), resolution leads to (quiet)/(thoughtful).\n- Output ONLY the annotated script text, preserving all line breaks. No preamble, no explanation."
  }
}
```

## C# Model Changes

### AppSettings.cs

Add to `AppSettings` class:

```csharp
public Dictionary<string, PersonaUsageEntry> PersonaUsage { get; set; } = new();
public MultiCharacterSettings MultiCharacter { get; set; } = new();
```

### VoicePersona

Add to existing `VoicePersona` class:

```csharp
public string? Gender { get; set; }
public string? ThoughtInstructions { get; set; }
```

Note: `ThoughtTemperature` is intentionally not added. The temperature bump for thought segments is a fixed +0.1 applied uniformly (see Part 3 temperature formula). Per-persona thought temperature overrides would add config complexity without clear benefit.

### New: PersonaUsageEntry

```csharp
public class PersonaUsageEntry
{
    public string? LastCharacter { get; set; }
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}
```

### New: MultiCharacterSettings

```csharp
public class MultiCharacterSettings
{
    public List<string> ModifierModels { get; set; } = new()
    {
        "z-ai/glm-4.5-air:free",
        "deepseek/deepseek-v4-flash"
    };
    public int ModifierTimeoutMs { get; set; } = 5000;
    public bool AutoInjectModifiers { get; set; } = true;
    public string ModifierSystemPrompt { get; set; } = ""; // populated from config or default
}
```

## Implementation Steps

### Phase 1: Configuration & Models

1. Add `Gender`, `ThoughtInstructions` to `VoicePersona` in `Configuration/AppSettings.cs`
2. Add `PersonaUsageEntry` class to `Configuration/AppSettings.cs`
3. Add `MultiCharacterSettings` class to `Configuration/AppSettings.cs`
4. Add `PersonaUsage` and `MultiCharacter` properties to `AppSettings` class
5. Add `female-2` through `female-13` personas to `appsettings.json` with all fields from Part 1 roster (voice, instructions, gender, thought instructions, temperature)
6. Add `MultiCharacter` section to `appsettings.json` with default system prompt

### Phase 2: Core Services

7. Create `Models/CharacterSegment.cs` — data model (see Part 2)
8. Create `Services/CharacterParser.cs` — stateless parser:
   - `Parse(string text) → List<CharacterSegment>`
   - Regex `\[([^\]:]+)(?::([FM]))?\]` for character markers
   - Text before first marker → segment with character name `""` (assigned to default persona by caller)
   - Extract `[thought]...[/thought]` → `IsThought = true`, text inside tags becomes `SpokenText`
   - Auto-close thought on `[Name]` boundary
   - Extract `(modifier)` immediately after `[Name]` only
   - Skip empty segments
  9. Create `Services/IPersonaAllocator.cs` + `Services/PersonaAllocator.cs`:
    - Constructor: `IOptions<AppSettings>` (consistent with existing codebase — snapshot at construction)
    - `AllocateForCharacter(string name, string gender) → string?`
    - `Reset()` — clears mappings, persists to `appsettings.json`
    - `GetPersona(string key) → VoicePersona?`
    - State persistence: direct file I/O to `appsettings.json` (same pattern as `Program.cs:SavePersona`)
    - Default persona IS in the allocation pool
    - 48h expiry: stale `LastUsedUtc` returns persona to available pool
10. Create `Services/IModifierInjector.cs` + `Services/ModifierInjector.cs`:
    - Constructor: `IHttpClientFactory`, `IOptions<AppSettings>`, `ILogger<ModifierInjector>`
    - Uses named HttpClient `"ModifierInjector"` (timeout = `ModifierTimeoutMs`)
    - Iterates `ModifierModels` list with fallback on failure/timeout/429
    - Returns original text if all models fail or `AutoInjectModifiers` is false
  11. Create `Services/ParallelTtsPlayer.cs`:
     - Constructor: `ITtsService`, `IPersistentAudioPipeline?`, `IAudioPlayer`, `IOptions<AppSettings>`, `ILogger`
     - `PlaySegmentsAsync(List<CharacterSegment> segments, CancellationToken ct) → Task`
     - Expects `PersonaKey` to already be set on each segment by the caller (ProxyService allocates personas before calling this method)
     - Producer: launch all `SynthesizeToStreamAsync` calls concurrently via `Task.WhenAll`
     - Consumer thread: read from thread-safe ordered buffer at index 0, pipe to `IPersistentAudioPipeline`
     - Instruction composition: `base + ". " + modifier + ". " + thought` (see Part 3 rules)
     - Temperature: `persona.Temperature ?? 0.65 + jitter ± 0.05 + 0.1 if thought`, clamped [0,1]
     - Cancellation: finish current segment, stop before next

### Phase 2.5: Unit Tests

12. Unit tests — `CharacterParser`:
    - Single character with `[Name]` markers
    - Multiple characters with different genders `[Name:F]`, `[Name:M]`
    - `[thought]...[/thought]` → `IsThought = true`
    - Thought auto-close on `[Name]` boundary
    - `(modifier)` immediately after `[Name]`
    - Mid-text `(modifier)` treated as literal text
    - Text before first `[Name]` → segment with empty character name
    - Empty segments skipped
    - Mixed thought + modifier on same block
    - No markers → single default segment
13. Unit tests — `PersonaAllocator`:
    - Random selection from matching gender pool
    - Same character gets same persona on second call
    - 48-hour expiry: stale persona returns to pool
    - `Reset()` clears all mappings
    - More characters than personas → reuse with warning
    - Empty pool → returns null
    - Default persona included in allocation pool
14. Unit tests — `ModifierInjector`:
    - Returns annotated text on success
    - Returns original text when `AutoInjectModifiers = false`
    - Returns original text on timeout/failure/429
    - Tries fallback model when first fails

### Phase 3: Integration

15. Register new services in `Program.cs` DI container (see Part 2 for registration code)
16. Add named HttpClient `"ModifierInjector"` in `Program.cs` with timeout from config
17. Modify `ProxyService.cs` — inject `IModifierInjector`, `CharacterParser`, `ParallelTtsPlayer`, `IPersonaAllocator` via constructor
  18. Modify `ProxyService.cs` — `StreamWithTtsAsync`:
     - After stream ends, check accumulated text for `[Name]` markers
     - If found: call `_speechQueue.FlushAndCancel()`, then run `ModifierInjector` → `CharacterParser` → allocate personas → `ParallelTtsPlayer`
     - Persona allocation bridging (between CharacterParser and ParallelTtsPlayer): loop segments, group by unique character, call `PersonaAllocator.AllocateForCharacter()` for each unique character, set `PersonaKey` on all segments for that character
     - If not found: existing single-character behavior unchanged
  19. Modify `ProxyService.cs` — `NonStreamWithTtsAsync`:
     - After extracting text, check for `[Name]` markers
     - If found: run `ModifierInjector` → `CharacterParser` → allocate personas → `ParallelTtsPlayer`
     - Persona allocation bridging: same as step 18
     - If not found: existing `_speechQueue.Enqueue(text)` unchanged
20. Add `/reset` interception in `ProxyService.HandleChatCompletionsCore`:
    - Parse request body, find last user message
    - If content is exactly `"/reset"`: call `PersonaAllocator.Reset()`, return empty streaming response, return (don't forward to backend)
  21. Modify `ProxyService.HandleChatCompletionsCore` — add `_activePlaybackCts` / `_activePlaybackTask` fields. When `_speechQueue.FlushAndCancel()` is called at the start of a new request (line 109), cancel and await (with 2s timeout) any running `ParallelTtsPlayer` operation. See Part 2 "FlushAndCancel interaction" for concrete mechanism.

### Phase 4: Integration Testing

22. Manual integration test — end-to-end multi-character synthesis:
    - Send a multi-character LLM response through the proxy
    - Verify different voices are used for different characters
    - Verify thoughts are spoken with distinct register
    - Verify modifier injection produces audible emotion differences
    - Verify `/reset` clears persona mappings
    - Verify streaming path flushes chunked audio and re-processes
23. Verify no regressions in single-character (non-multi-character) responses

## Edge Cases

All parsing edge cases are covered in Part 1. Runtime edge cases:

- **No character markers** — passthrough to existing single-character TTS path. Modifier injection is skipped. No multi-character pipeline overhead.
- **Single character** (only one `[Name]` marker, or all segments belong to one character) — runs through the multi-character pipeline but with only one segment. Modifier injection runs. Single persona allocated. No parallel synthesis benefit, but emotion modifiers and persona assignment still apply.
- **Character reappears** — same persona assigned as first encounter (deterministic within session).
- **More characters than personas** — cycle back to first available persona for that gender, log a warning.
- **Empty text** — skip segments with no spoken content.
- **Reset** — clears all character→persona mappings. Next multi-character response starts allocation from scratch. State is persisted immediately to `appsettings.json`.
- **48-hour persona reuse** — a persona whose `LastUsedUtc` is older than 48 hours is treated as available for re-assignment to a different character. The previous character→persona link for that persona is forgotten.
- **Streaming mode** — `SentenceSplitter` may enqueue chunks with raw `[Name]` markers during SSE. After stream ends, if markers detected, `_speechQueue.FlushAndCancel()` discards chunked audio and the full text is re-processed through multi-character pipeline. Default persona handles pre-marker text. No text is ever dropped.
- **Modifier LLM failure** — if all models in `ModifierModels` fail/timeout/429, proceed with raw text. No modifiers = each segment uses only persona base instructions.
- **Modifier LLM corrupts markers** — post-injection validation counts `[Name]` markers in output vs input. If counts differ, the original unmodified text is returned instead. This guards against LLMs that strip or rewrite character markers (e.g., `deepseek-v4-flash` has been observed doing this).
- **Persona pool empty for gender** — if no personas match the requested gender, log a warning and use the default persona as fallback.
- **Cancellation during playback** — current segment finishes, playback stops before next segment. In-flight TTS tasks for remaining segments are cancelled.
- **ParallelTtsPlayer when PersistentAudioPipeline is null** — fall back to `IAudioPlayer.PlayStreamAsync()` (buffered playback instead of streaming pipe).
- **PersonaUsage persistence and config reload** — `appsettings.json` is loaded with `reloadOnChange: true` (default from `Host.CreateDefaultBuilder`). Writing `PersonaUsage` to this file will trigger a config reload. Since all services use `IOptions<AppSettings>` (snapshot, not `IOptionsMonitor`), the reload has no effect on running services. `PersonaAllocator` uses its in-memory `ConcurrentDictionary` as the source of truth and persists to file for crash recovery only. No reload handling is needed.

## Note on Existing Config Section Names

The existing `appsettings.json` uses `"TranscriptCleanupOff"` as the JSON key but `AppSettings.cs` maps it via `TranscriptCleanup` property (with JSON `JsonPropertyName` or case-insensitive deserialization). Multi-character changes do not touch this section. Be aware of this naming discrepancy when modifying the config file.
