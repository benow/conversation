# Multi-Character TTS — Overview

Multi-character dialogue support for the benow-conversation TTS proxy. Parses LLM responses containing `[Name]` markers, assigns distinct voice personas, injects emotion modifiers, splits narration from dialogue, and plays back audio in sequence with parallel TTS synthesis.

## Text Format

The LLM generates responses using these markers:

```
[Name]dialogue text
[Name:F]explicitly female character
[Name:M]explicitly male character
[thought]inner monologue[/thought]
(modifier)speech modifier on a line
*text*narration / stage direction
```

The parser detects `[Name]` markers (excluding `[thought]`/`[/thought]`), extracts optional `:F`/`:M` gender suffixes, splits `[thought]...[/thought]` blocks into separate segments with `IsThought=true`, and splits `*...*` delimited text into narration segments (`IsNarration=true`).

Modifiers `(whisper)`, `(thirsty)`, etc. are extracted from `(modifier)` patterns immediately after a character marker.

### Example LLM Output

```
[Marina:F] *her eyes flutter open, a hint of surprise on her face* Ah, señor... *she says, her voice husky with desire* I think that's a wonderful idea.
[Sofia:F] Mami! [thought] *giggles* He's cute! [/thought] Can we go?
[Mopsie:F] *beeps* Systems nominal. [thought]The question is, what will you do next?[/thought]
```

### Parsed Segments

| # | Character | Text | IsNarration | IsThought |
|---|---|---|---|---|
| 0 | Marina | her eyes flutter open, a hint of surprise on her face | ✓ | |
| 1 | Marina | Ah, señor... | | |
| 2 | Marina | she says, her voice husky with desire | ✓ | |
| 3 | Marina | I think that's a wonderful idea. | | |
| 4 | Sofia | Mami! | | |
| 5 | Sofia | giggles | ✓ | ✓ |
| 6 | Sofia | He's cute! | | ✓ |
| 7 | Sofia | Can we go? | | |
| 8 | Mopsie | beeps | ✓ | |
| 9 | Mopsie | Systems nominal. | | |
| 10 | Mopsie | The question is, what will you do next? | | ✓ |

## Architecture

### Services

| Service | File | Purpose |
|---|---|---|
| `CharacterParser` | `Services/CharacterParser.cs` | Stateless parser: `[Name]` markers, `:F`/`:M` gender, `[thought]`, `(modifier)`, `*narration*` splitting |
| `PersonaAllocator` | `Services/PersonaAllocator.cs` | Assigns voice personas to characters by name+gender with 48h expiry and JSON persistence |
| `ModifierInjector` | `Services/ModifierInjector.cs` | LLM-driven emotion modifier annotation via OpenRouter chat completions |
| `ParallelTtsPlayer` | `Services/ParallelTtsPlayer.cs` | Producer-consumer parallel synthesis with sequential playback through persistent ffplay pipeline |
| `PersistentAudioPipeline` | `Services/PersistentAudioPipeline.cs` | Long-lived ffplay process for gapless segment playback |
| `CharacterSegment` | `Models/CharacterSegment.cs` | Record: SequenceIndex, CharacterName, Gender, SpokenText, PersonaKey, IsThought, IsNarration, Modifier |

### Emotion Pipeline

```
LLM response text
  │
  ▼
① ModifierInjector (parallel race: 3 models, first success wins)
   Inserts (modifier) tags via emotion-aware LLM
   Models: google/gemini-2.0-flash-001, deepseek/deepseek-v4-flash, z-ai/glm-4.5-air
   Timeout: 3000ms per model, raced in parallel
  │
  ▼
② CharacterParser
   Splits by [Name] / [Name:G] markers
   Extracts [thought]...[/thought] → IsThought flag
   Extracts (modifier) tags → Modifier field
   Splits *...* text → IsNarration flag
  │
  ▼
③ Persona Assignment (ProxyService)
   IsNarration → NarratorPersona (female-9, ballad)
   IsThought → ThoughtPersona (female-3, coral)
   Otherwise → AllocateForCharacter(name, gender)
   CharacterAssignments in config: fixed mappings survive restarts
  │
  ▼
④ ParallelTtsPlayer (TaskCompletionSource per segment)
   Per segment: instructions = persona base + modifier suffix + thought suffix
   Temperature = persona temp ± random jitter (±0.05; +0.05 if thought)
   All 6-24 segments synthesize concurrently via OpenRouter TTS API
  │
  ▼
⑤ Sequential playback (ordered by SequenceIndex)
   Buffer completed audio streams
   Play back in original order through PersistentAudioPipeline → ffplay
   Replay stored in memory for /replay command
```

## Persona Roster (13 Voices, All Female)

| Key | Voice | Gender | Description |
|---|---|---|---|
| female-1 | marin | F | young, british, dripping with desire, sexually ravenous, flirty tease *(default)* |
| female-2 | nova | F | warm australian, sexually adventurous, vocal with pleasure, eager lover |
| female-3 | coral | F | dreamy, ethereal, sensually detached, erotic dreaminess *(thought persona)* |
| female-4 | shimmer | F | composed, articulate, repressed desire simmering beneath every word |
| female-5 | fable | F | rich, theatrical, sexually dramatic, moans through sentences |
| female-6 | verse | F | casual, laid-back, sexually confident, pillow-talk tone |
| female-7 | sage | F | experienced lover, confident in her desire, knows exactly what she wants |
| female-8 | ash | F | youthful, sexually curious, breathless with anticipation |
| female-9 | ballad | F | lush, passionate, sexually insatiable, vocal and moaning *(narrator)* |
| female-10 | echo | F | dark sexual energy, commands desire with silence, enigmatic and magnetic |
| female-11 | alloy | F | sexually adaptive, mirrors your lust, becomes whatever you need |
| female-12 | onyx | F | sexually dominant, demands submission, uses desire as a weapon |
| female-13 | cedar | F | earth-mother sexuality, primal hunger, knows every inch of you |

All personas use model `openai/gpt-4o-mini-tts-2025-12-15`.

## Speech Modifiers

13 modifiers map to TTS instruction fragments applied when the modifier is present on a segment:

| Modifier | TTS Instructions |
|---|---|
| whisper | speak in a hushed, breathy tone, close-mic intimacy |
| laughing | laugh while speaking, warm and amused |
| thoughtful | speak thoughtfully, slower, contemplative |
| angry | speak with frustration, raised intensity |
| sad | speak sadly, subdued, melancholic |
| excited | speak with excitement, energetic and breathless |
| sigh | let out a sigh before speaking, then continue |
| quiet | speak with intimate closeness, warm and gentle |
| narrate | speak in a detached, story-telling voice |
| flirtatious | speak playfully, teasing, with a seductive undertone |
| teasing | speak in a mock-serious, playful ribbing tone |
| sudden | speak startled, quick intake of breath then speak |
| thirsty | speak with raw sexual hunger, dripping with desperate need, aching and insatiable |

## Streaming & Hold Response

### Chunked TTS Suppression

When chunked TTS is enabled (SentenceSplitter), each dequeued sentence is checked for `[Name]` markers. If detected, chunked enqueuing is suppressed immediately and the SpeechQueue is flushed — no default-voice audio leaks through before multi-character detection.

### Hold Response for Synchronized Text+Audio

Config option `HoldResponseForAudio` (default: `true`). When a multi-character response is detected mid-stream:

1. Text streaming is paused; SSE heartbeat comments (`:hold`) sent every 2s to prevent client timeout
2. Full LLM response is buffered silently
3. Audio synthesis begins for all segments in parallel
4. As each segment's audio starts playing, its text is sent to the client as an SSE delta event
5. After all segments play, `[DONE]` is sent

Non-multi-character responses are unaffected (stream normally).

## Commands

| Command | Description |
|---|---|
| `/reset` | Clears all character→persona assignments and usage history |
| `/replay` | Replays the last multi-character audio from memory |

## Configuration

### MultiCharacterSettings

| Field | Default | Description |
|---|---|---|
| `ModifierModels` | `["google/gemini-2.0-flash-001", "deepseek/deepseek-v4-flash", "z-ai/glm-4.5-air"]` | LLM models for modifier injection (parallel race) |
| `ModifierTimeoutMs` | `3000` | Timeout per modifier model request |
| `AutoInjectModifiers` | `true` | Whether to run LLM modifier injection |
| `ThoughtPersona` | `"female-3"` | Persona for all `[thought]` segments |
| `NarratorPersona` | `"female-9"` | Persona for all `*...*` narration segments |
| `HoldResponseForAudio` | `true` | Buffer response, relay text in sync with audio |

### CharacterAssignments

Fixed character→persona mappings persisted in `appsettings.json`:

```json
"CharacterAssignments": {
  "Marina": "female-2",
  "Sofia": "female-8",
  "Mopsie": "female-10"
}
```

## Test Coverage (30 tests)

| File | Tests | Coverage |
|---|---|---|
| `CharacterParserTests.cs` | 20 | Parsing, markers, gender, thoughts, modifiers, narration splitting |
| `PersonaAllocatorTests.cs` | 8 | Allocation, fallback, reset, staleness, persistence |
| `ModifierInjectorTests.cs` | 3 | Auto-inject toggle, request format, error handling |
| `MultiCharacterConfigTests.cs` | 10 | Config deserialization, defaults |
| `ParallelTtsPlayerTests.cs` | 15 | Segments, ordering, thoughts, modifiers, persona allocation, replay, cancellation, synth failure |
| `PersistentAudioPipelineTests.cs` | 9 | Startup, pipe, interrupt, dispose, serialization, data integrity, disposal guards, cancellation |
| `MultiCharacterIntegrationTests.cs` | 6 | Full pipeline: parse→allocate→play, assignments, fallback, modifier mapping, config validation |

## Files

| File | Contents |
|---|---|
| `src/benow-conversation/Models/CharacterSegment.cs` | Segment record with IsNarration field |
| `src/benow-conversation/Services/CharacterParser.cs` | Stateless parser with narration splitting |
| `src/benow-conversation/Services/PersonaAllocator.cs` | Persona allocator with persistence |
| `src/benow-conversation/Services/ModifierInjector.cs` | Parallel-race modifier injection |
| `src/benow-conversation/Services/ParallelTtsPlayer.cs` | Parallel synthesis with modifier mapping and replay |
| `src/benow-conversation/Services/PersistentAudioPipeline.cs` | Persistent ffplay with orphan cleanup |
| `src/benow-conversation/Configuration/AppSettings.cs` | VoicePersona (Gender, ThoughtInstructions), MultiCharacterSettings, CharacterAssignments |
| `tests/benow-conversation.Tests/` | 30 multi-character tests across 7 files |
