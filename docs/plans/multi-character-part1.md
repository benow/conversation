# Multi-Character TTS — Part 1: Input Format & Persona Allocation

## Input Format

The LLM response contains character blocks delimited by `[CharacterName]` markers:

```
[Sofia]Mami, I think I see someone...[thought]inner thought[/thought]

[Marina]Ah, Sofia, you're growing up...
```

### Parsing Rules

1. **Character marker regex**: `\[([^\]:]+)(?::([FM]))?\]` — captures name group 1 and optional gender group 2.
2. `[CharacterName]` — starts a new character block. Everything until the next `[Name]` or end-of-text belongs to that character.
3. `[CharacterName:G]` — optional gender suffix: `:F` (female, default) or `:M` (male). Used for persona allocation.
4. **Text before the first `[Name]` marker** is assigned to the default persona (the persona with `IsDefault: true` in config, currently `female-1`). This handles LLM responses that begin with narration or preamble before character dialogue.
5. `[thought]...[/thought]` — inner monologue tags within a character block are **spoken** in a distinct inner-monologue style (lower register, reflective tone), not stripped. Thought tags **must close before a new `[Name]` marker**. If a `[Name]` marker appears before `[/thought]` is found, the thought is auto-closed at that boundary — the thought text and its `[/thought]` tag are discarded, and the new character block begins.
6. `(modifier)` — optional inline speech modifier placed **only immediately after the character marker** (before the first word of dialogue). Alters the `instructions` for that segment (e.g. `[Sofia](whisper)text...`). Mid-text `(modifier)` tags are treated as literal text.
7. Each block is one character's spoken text after modifier extraction, thought tag stripping, and trimming.

### Example Parse

Input:
```
[Sofia]Mami, I think I see someone... a boy, he's looking at me.[thought]What if he's the one?[/thought]

[Marina]Ah, Sofia, you're growing up, and it's beautiful to see. As we dance...
```

Parsed segments (in order):
| # | Character | Gender | Spoken Text | Persona |
|---|-----------|--------|-------------|---------|
| 0 | Sofia     | F      | "Mami, I think I see someone... a boy, he's looking at me." | *(random)* |
| 1 | Marina    | F      | "Ah, Sofia, you're growing up, and it's beautiful to see. As we dance..." | *(random)* |

## Persona Allocation

Personas are allocated **randomly** from the pool of matching-gender personas for the current TTS model:

- At startup (or on `/reset`), all personas are returned to the available pool.
- When a new character is first encountered, a persona is picked at random from the available pool of matching gender.
- Characters reappearing later get the **same** persona they were first assigned.
- If more characters than available personas, personas are reused (pick random from full pool, log a warning).

### Persona Pool

The pool is built from:
1. All personas in the `Personas` dictionary whose `Model` matches the current TTS model (`OpenRouter.TtsModel`).
2. Filtered by gender (`:F` or `:M` on the character marker, default `:F`).
3. Gender is determined by the `Gender` property on `VoicePersona`. If `Gender` is not set, it defaults to the requested gender (assume configured correctly).
4. Some TTS models use prefixed voice IDs (e.g. `af_`, `bf_` = female; `am_`, `bm_` = male). For those models without a `Gender` field, gender can be inferred from the prefix. The current TTS model (`gpt-4o-mini-tts`) uses named voices without prefixes, so the `Gender` field should always be set.

### Reset (`/reset`)

Clears the entire character→persona allocation table. All personas return to the available pool. Next multi-character response randomizes fresh allocations. State is persisted immediately.

### Persona Reuse (48-hour expiry)

Personas are safe to reuse after 48 hours of inactivity. The allocator tracks **last-used timestamps** per persona, persisted to the config file:

```json
"PersonaUsage": {
  "female-1": {
    "LastCharacter": "Sofia",
    "LastUsedUtc": "2026-05-29T03:00:00Z"
  },
  "female-2": {
    "LastCharacter": "Marina",
    "LastUsedUtc": "2026-05-29T03:00:00Z"
  }
}
```

- On allocation, personas whose `LastUsedUtc` is older than 48 hours are treated as **available** (their previous character mapping is forgotten, and the persona can be assigned to a new character).
- This allows personas to cycle back naturally over time without needing a `/reset`.
- The `LastUsedUtc` is updated each time the persona is assigned (not each time it is used to speak — only on new allocation).
- Persistence to `appsettings.json` happens after each allocation change (the `PersonaUsage` section is written back to the file).

### Default Persona

The persona with `IsDefault: true` (currently `female-1` — The Temptress) is used for:
- Text before the first `[Name]` marker in a multi-character response
- Single-character (non-multi-character) responses (existing behavior via `ProxySettings.TtsPersona`)
- Fallback when a character has no available persona in the pool

The default persona **is included in the random allocation pool** and can be assigned to characters like any other persona. It also serves as the fallback for unmarked text and single-character mode.

### Allocation Algorithm

```
1. For each unique character in parsed text (first-encounter order):
   a. If character already has a persona → keep it
   b. Get available pool: personas matching model + gender, minus those assigned to active characters (< 48h)
   c. Pick random from available pool
   d. Record character→persona mapping, update LastUsedUtc, persist
```

## Persona Roster

All 13 voices from `openai/gpt-4o-mini-tts-2025-12-15`, configured as female personas with randomized characteristics. The `Gender` field defaults to `F` for all.

### Summary Table

| Key | Voice | Archetype | Role |
|-----|-------|-----------|------|
| `female-1` | marin | The Temptress | **Default** — unmarked text, fallback |
| `female-2` | nova | The Confidante | Warm, empathetic, Australian |
| `female-3` | coral | The Dreamer | Ethereal, poetic, distant |
| `female-4` | shimmer | The Intellectual | Composed, dry wit, precise |
| `female-5` | fable | The Storyteller | Theatrical, dramatic, expressive |
| `female-6` | verse | The Free Spirit | Casual, irreverent, laid-back |
| `female-7` | sage | The Mentor | Calm, authoritative, patient |
| `female-8` | ash | The Innocent | Youthful, eager, sweet |
| `female-9` | ballad | The Romantic | Passionate, sensual, lush |
| `female-10` | echo | The Mysterious | Low, enigmatic, magnetic |
| `female-11` | alloy | The Chameleon | Neutral, adaptable, balanced |
| `female-12` | onyx | The Intense | Sharp, piercing, commanding |
| `female-13` | cedar | The Earth Mother | Grounded, nurturing, ancient |

### female-1 — The Temptress (Default)

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "marin",
  "OpenAiInstructions": "young, british, sexy, flirty, continually interested",
  "Gender": "F",
  "ThoughtInstructions": "speak in a lower, deeper register, reflective and intimate, as if thinking to yourself",
  "Temperature": 0.7,
  "IsDefault": true
}
```

A young, flirtatious British voice with an ever-present undercurrent of desire. Her speech carries a teasing quality, always engaged and curious.

### female-2 — The Confidante

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "nova",
  "OpenAiInstructions": "warm australian, empathetic, curious, animated, breathless when excited",
  "Gender": "F",
  "ThoughtInstructions": "speak in a softer, more contemplative register, as if reflecting quietly to yourself",
  "Temperature": 0.7
}
```

Warm, empathetic, animated. An Australian accent gives her an approachable, sunny quality. She leans in when she speaks, genuinely invested.

### female-3 — The Dreamer

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "coral",
  "OpenAiInstructions": "soft-spoken, dreamy, slightly distant, poetic, ethereal quality",
  "Gender": "F",
  "ThoughtInstructions": "speak barely above a whisper, floating and detached, lost in imagination",
  "Temperature": 0.8
}
```

Ethereal and slightly detached, as if she exists half in this world and half in another. Her speech flows like a slow stream.

### female-4 — The Intellectual

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "shimmer",
  "OpenAiInstructions": "composed, articulate, measured, dry wit, understated warmth",
  "Gender": "F",
  "ThoughtInstructions": "speak in a lower, more analytical register, precise and methodical",
  "Temperature": 0.5
}
```

Precise and composed, she chooses words carefully. Her wit is dry, delivered with a slight smile audible in her tone.

### female-5 — The Storyteller

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "fable",
  "OpenAiInstructions": "rich, theatrical, expressive, playful, dramatic flair, loves words",
  "Gender": "F",
  "ThoughtInstructions": "speak in a hushed, reverent tone, as if reading from a beloved book",
  "Temperature": 0.75
}
```

Theatrical and expressive, she treats every sentence as a performance. Her voice has rich variation in pitch and pacing.

### female-6 — The Free Spirit

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "verse",
  "OpenAiInstructions": "casual, laid-back, witty, irreverent, dry humor, easy laughter",
  "Gender": "F",
  "ThoughtInstructions": "speak in a musing, lazy cadence, daydreaming out loud",
  "Temperature": 0.8
}
```

Casual and irreverent with a laid-back delivery. She doesn't take anything too seriously and her humor is dry and effortless.

### female-7 — The Mentor

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "sage",
  "OpenAiInstructions": "calm, authoritative, patient, reassuring, gentle gravitas, speaks from experience",
  "Gender": "F",
  "ThoughtInstructions": "speak in a quiet, measured tone, drawing on deep wisdom, contemplative",
  "Temperature": 0.4
}
```

Calm and grounded, her voice carries the weight of experience without being heavy. She speaks with patience and gentle authority.

### female-8 — The Innocent

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "ash",
  "OpenAiInstructions": "youthful, wide-eyed, eager, sweet, earnest, slightly nervous energy",
  "Gender": "F",
  "ThoughtInstructions": "speak in a tiny, wondering voice, discovering something for the first time",
  "Temperature": 0.65
}
```

Young and earnest with a sweet quality. Her eagerness is infectious, always slightly on the edge of excitement.

### female-9 — The Romantic

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "ballad",
  "OpenAiInstructions": "lush, passionate, sensual, flowing cadence, dramatic, romantic intensity",
  "Gender": "F",
  "ThoughtInstructions": "speak in a breathy, longing register, intimate and vulnerable",
  "Temperature": 0.75
}
```

Passionate and sensual, her voice has a lush, flowing quality. She speaks with dramatic romantic intensity.

### female-10 — The Mysterious

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "echo",
  "OpenAiInstructions": "low, measured, enigmatic, controlled, magnetic undertone, speaks little",
  "Gender": "F",
  "ThoughtInstructions": "speak in a barely audible murmur, dark and private, guarding secrets",
  "Temperature": 0.5
}
```

Low and enigmatic with a magnetic quality. She speaks sparingly and every word carries weight. Hard to read, impossible to forget.

### female-11 — The Chameleon

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "alloy",
  "OpenAiInstructions": "neutral, adaptable, clear, balanced, versatile, mirrors the conversation",
  "Gender": "F",
  "ThoughtInstructions": "speak in a quiet internal voice, processing and adapting",
  "Temperature": 0.6
}
```

Neutral and versatile, she adapts to the tone around her. Clear and balanced, making her a good default or foil character.

### female-12 — The Intense

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "onyx",
  "OpenAiInstructions": "sharp, piercing, commanding, fierce, magnetic, does not waste words",
  "Gender": "F",
  "ThoughtInstructions": "speak in a tightly controlled, simmering intensity, restrained fury",
  "Temperature": 0.55
}
```

Commanding and fierce with a sharp, piercing quality. She does not waste words and her silences are as powerful as her speech.

### female-13 — The Earth Mother

```json
{
  "Model": "openai/gpt-4o-mini-tts-2025-12-15",
  "Voice": "cedar",
  "OpenAiInstructions": "grounded, warm, nurturing, slow cadence, deeply soothing, ancient wisdom",
  "Gender": "F",
  "ThoughtInstructions": "speak in a deep, slow rumination, connected to something ancient",
  "Temperature": 0.45
}
```

Warm and nurturing with a slow, grounded cadence. Her voice feels ancient and deeply connected to something fundamental.
