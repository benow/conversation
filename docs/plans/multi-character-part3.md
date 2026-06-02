# Multi-Character TTS — Part 3: Emotion & Modifier System

All speech variation is handled via the TTS `instructions` parameter. No audio post-processing or re-encoding.

## Available TTS Tuning Knobs

| Parameter | Range | What It Controls |
|---|---|---|
| `instructions` | Free text | Accent, emotion, intonation, impressions, **speed**, tone, **whispering**, **depth/register** |
| `temperature` | 0.0–1.0 | Randomness/variation in prosody between utterances |

## Emotion Pipeline

See Part 2 for the full pipeline diagram. In summary:

```
Raw LLM response → ModifierInjector → CharacterParser → PersonaAllocator → Parallel TTS → Playback
```

## Instruction Composition

All instruction suffixes (modifier and thought) are appended to the persona's base `OpenAiInstructions` using `. ` (period-space) as separator. This produces a natural sentence-like instruction string for the TTS model.

### Composition Rules

1. **Base only** (no modifier, not a thought):
   ```
   Final = persona.OpenAiInstructions
   ```

2. **Base + modifier** (e.g. `(whisper)`):
   ```
   Final = persona.OpenAiInstructions + ". " + ModifierMapping[modifier]
   ```

3. **Base + thought** (segment has `[thought]` tags):
   ```
   Final = persona.OpenAiInstructions + ". " + persona.ThoughtInstructions
   ```

4. **Base + modifier + thought** (segment has both `(modifier)` and `[thought]` tags — combined):
   ```
   Final = persona.OpenAiInstructions + ". " + ModifierMapping[modifier] + ". " + persona.ThoughtInstructions
   ```
   Both the modifier delivery instruction and the thought register instruction are applied. Thought instructions are appended last as the innermost tonal layer.

### Composition Examples

| Segment | Base | Modifier Suffix | Thought Suffix | Final Instruction |
|---|---|---|---|---|
| Sofia spoken, no modifier | `"...flirty, continually interested"` | — | — | `"...flirty, continually interested"` |
| Sofia `(thoughtful)` | `"...flirty, continually interested"` | `. speak thoughtfully, slower, contemplative` | — | `"...flirty, continually interested. speak thoughtfully, slower, contemplative"` |
| Marina thought | `"...curious, animated"` | — | `. speak in a softer, more contemplative register, as if reflecting quietly to yourself` | `"...curious, animated. speak in a softer, more contemplative register, as if reflecting quietly to yourself"` |
| Sofia `(whisper)` + thought | `"...flirty, continually interested"` | `. speak in a whisper, very soft and quiet` | `. speak in a lower, deeper register, reflective and intimate, as if thinking to yourself` | `"...flirty, continually interested. speak in a whisper, very soft and quiet. speak in a lower, deeper register, reflective and intimate, as if thinking to yourself"` |
| Marina `(excited)` | `"...curious, animated"` | `. speak with excitement, energetic and breathless` | — | `"...curious, animated. speak with excitement, energetic and breathless"` |

### Instruction Length Guidance

TTS `instructions` is free-text with no documented hard limit, but excessively long instructions (>300 chars) may degrade quality or be ignored. Typical composed instructions fall in the 80–200 char range. No truncation is applied — the persona configs and modifier suffixes are designed to stay within safe bounds.

## Strategy: `[thought]` → Inner Monologue via Instructions

Speak the thought text with a distinct inner-monologue style by appending the persona's `ThoughtInstructions`:

```
Base:     "young, british, sexy, flirty, continually interested"
Thought:  "young, british, sexy, flirty, continually interested. speak in a lower, deeper register, reflective and intimate, as if thinking to yourself"
```

The TTS model handles the tonal shift natively — no volume changes, no ffmpeg filters, no re-encoding.

Each persona defines its own `ThoughtInstructions` (see Part 1 persona roster) to maintain character consistency.

## Strategy: Emotion/Speech Modifiers

Parse optional inline modifiers placed **only immediately after the character marker** that adjust the `instructions` for a specific segment:

```
[Sofia](whisper)I think I see someone...
[Marina](laughing)Ah, Sofia, you're growing up!
[Sofia](thoughtful)I wonder if he noticed me.
```

### Modifier → Instruction Suffix Mapping

```csharp
// Keys are the modifier name WITHOUT parentheses (extracted from (modifier) by CharacterParser)
// Values are the instruction suffix appended to the persona base instructions via ". " separator
public static readonly Dictionary<string, string> ModifierMapping = new(StringComparer.OrdinalIgnoreCase)
{
    ["whisper"]     = "speak in a whisper, very soft and quiet",
    ["laughing"]    = "laugh softly while speaking, warm and amused",
    ["thoughtful"]  = "speak thoughtfully, slower, contemplative",
    ["angry"]       = "speak with frustration, raised intensity",
    ["sad"]         = "speak sadly, subdued, melancholic",
    ["excited"]     = "speak with excitement, energetic and breathless",
    ["sigh"]        = "let out a soft sigh before speaking",
    ["quiet"]       = "speak very softly, intimate",
    ["narrate"]     = "speak in a detached, story-telling voice",
    ["flirtatious"] = "speak playfully, teasing, with a seductive undertone",
    ["teasing"]     = "speak in a mock-serious, playful ribbing tone",
    ["sudden"]      = "speak startled, quick intake of breath then speak",
};
```

## Temperature Variation for Naturalness

To avoid robotic uniformity across segments from the same persona:

- **Base temperature**: from persona `Temperature` config field (values range 0.4–0.8 across personas — see Part 1)
- **Fallback**: if `Temperature` is null on a persona, use `0.65`
- **Random jitter per segment**: `base + Random.Shared.NextDouble() * 0.1 - 0.05` (uniform distribution ±0.05)
- **Thought segments**: additional fixed +0.1 bump (clamped to max 1.0)
- **Clamp**: final temperature is clamped to `[0.0, 1.0]`

```csharp
var base = persona.Temperature ?? 0.65;
var temp = base + Random.Shared.NextDouble() * 0.1 - 0.05;
if (segment.IsThought)
    temp += 0.1;
temp = Math.Clamp(temp, 0.0, 1.0);
```

## LLM-Driven Modifier Injection

A cheap fast LLM analyzes the **full LLM response text** before any parsing, inserting contextually appropriate speech modifiers.

### Pipeline Position

```
Raw LLM response (full text)
  → ModifierInjector (cheap fast model)
  → Annotated text (with modifiers)
  → CharacterParser (split into segments)
  → PersonaAllocator → Parallel TTS
```

### Model Configuration

Models are configured in `MultiCharacterSettings.ModifierModels` (ordered list). The first model is tried; on failure/timeout/429, fall back to the next in the list. If all fail, proceed without modifiers.

```json
{
  "MultiCharacter": {
    "ModifierModels": ["z-ai/glm-4.5-air:free", "deepseek/deepseek-v4-flash"],
    "ModifierTimeoutMs": 5000,
    "AutoInjectModifiers": true,
    "ModifierSystemPrompt": "You are a voice director..."
  }
}
```

**Default model list**:

| Priority | Model | Cost | Rationale |
|---|---|---|---|
| 1 | `z-ai/glm-4.5-air:free` | Free | GLM-TTS research lineage (emotion-RL trained), best emotion understanding |
| 2 | `deepseek/deepseek-v4-flash` | Free | Fastest inference, sometimes strips `[Name]` tags — acceptable fallback |

### Benchmark Results

| Model | TTFB | Total | Quality |
|---|---|---|---|
| `z-ai/glm-4.5-air` | ~1.8s | ~2s | Correct modifiers, preserved tags |
| `deepseek/deepseek-v4-flash` | ~3.5s | ~5s | Correct modifiers, sometimes strips `[Name]` |

### System Prompt

Configured in `MultiCharacterSettings.ModifierSystemPrompt`. Default:

```
You are a voice director annotating a multi-character script for text-to-speech synthesis.

Your job: read the script and insert exactly ONE speech modifier in parentheses before lines that would benefit from expressive delivery guidance. Leave lines that need no special treatment without a modifier.

Available modifiers — use these and ONLY these:
  (whisper)       — very soft, hushed, barely audible
  (laughing)      — speaking while amused, warm chuckle in voice
  (thoughtful)    — slower, contemplative, measured
  (angry)         — frustrated, raised intensity, sharp
  (sad)           — subdued, melancholic, quiet heaviness
  (excited)       — energetic, breathless, animated
  (sigh)          — let out a soft sigh, then speak
  (quiet)         — intimate, soft-spoken, close-mic
  (narrate)       — detached, story-telling voice
  (flirtatious)   — playful, teasing, seductive undertone
  (teasing)       — mock-serious, playful ribbing
  (sudden)        — startled, quick intake of breath then speak

Rules:
- Preserve ALL character markers like [Name] and [Name:G] exactly as-is.
- Preserve ALL [thought]...[/thought] tags exactly as-is — do NOT add modifiers inside thought tags.
- Insert the modifier between the character marker and the first word of dialogue: [Name](modifier)text...
- Do NOT add a modifier to every line — only where it genuinely enhances delivery. A natural script has 30-50% of lines modified.
- If a line already has a modifier, leave it untouched.
- Consider the emotional arc: early lines may be neutral, building tension leads to (excited)/(angry), resolution leads to (quiet)/(thoughtful).
- Output ONLY the annotated script text, preserving all line breaks. No preamble, no explanation.
```

### Example

Input:
```
[Sofia]Mami, I think I see someone... a boy, he's looking at me, and I feel a flutter in my chest. He's got piercing blue eyes and chiseled features, like a sculpture come to life.[thought]What if he's the one?[/thought]

[Marina]Ah, Sofia, you're growing up, and it's beautiful to see. The way you're embracing your desires... it's a truly magical thing.
```

Output:
```
[Sofia](thoughtful)Mami, I think I see someone... a boy, he's looking at me, and I feel a flutter in my chest. He's got piercing blue eyes and chiseled features, like a sculpture come to life.[thought]What if he's the one?[/thought]

[Marina](thoughtful)Ah, Sofia, you're growing up, and it's beautiful to see. The way you're embracing your desires... it's a truly magical thing.
```

### ModifierInjector Service

```
interface IModifierInjector
  InjectModifiersAsync(string text, CancellationToken ct) → Task<string>

class ModifierInjector : IModifierInjector
  constructor(IHttpClientFactory, IOptions<AppSettings>, ILogger<ModifierInjector>)

  InjectModifiersAsync:
    - If AutoInjectModifiers is false → return original text immediately
    - Iterate ModifierModels list in order:
      - Send full raw text to model with ModifierSystemPrompt
      - If successful response → return annotated text
      - If failure/timeout/429 → log warning, try next model
    - If all models fail → return original text unchanged
    - Uses named HttpClient "ModifierInjector" (timeout = ModifierTimeoutMs)
    - Graceful degradation: no modifiers = each segment uses persona base instructions only
```

## Updated MultiCharacterSettings (cascading from this part)

The config model gains two fields relative to Part 2's definition:

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
    public string ModifierSystemPrompt { get; set; } = @"You are a voice director..."; // default prompt
}
```

Note: Part 2 and Part 4 both define `ModifierModels` as a `List<string>` to support the fallback chain. The definitions are consistent across all parts.
