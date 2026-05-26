# Stage 2: Voice Profiles, Model Discovery & Batch Generation

## Goal

Add voice profile selection, TTS model discovery, and batch voice sampling to the CLI. All features are query/generation oriented — no UI yet.

## Tasks

### 1. Voice Profile Support

**`--voice <profile>`** flag (optional, defaults to current model default):

- If profile is a valid voice ID for the model (e.g., `nova`, `af_bella`, `en_paul_happy`), use it directly
- If profile is `all`, generate one file per supported voice, using the same input text
  - Output files named `<prefix>_<voice>.mp3` (e.g., `output_20260523-140300_nova.mp3`, `output_20260523-140300_alloy.mp3`)
  - Or if `--output` is specified as `greeting.mp3`, files become `greeting_nova.mp3`, `greeting_alloy.mp3`, etc.
- If profile doesn't match any supported voice, error with: `"Voice '{voice}' is not supported by model '{model}'. Use --list-voices to see available voices."`

**Changes to `TtsService`:**
- `SynthesizeToFileAsync` accepts an optional `voice` parameter
- When voice is null/empty, use model's default (first in `supported_voices` list, or "alloy" as fallback)
- New method: `SynthesizeAllVoicesAsync(string text, string? outputFileName = null)` → returns list of all output paths
  - Fetches supported voices for the configured model
  - Calls `SynthesizeToFileAsync` for each voice with appropriate filename

**Changes to `Models/TtsRequest.cs`:**
- Voice property already exists, no structural change needed

### 2. Model Discovery

**`--list-models`** flag — queries OpenRouter API and displays available TTS models:

```
Available TTS models:
  openai/gpt-4o-mini-tts-2025-12-15     $0.02/1M chars   13 voices
  google/gemini-3.1-flash-tts-preview   $0.01/1M chars   30 voices
  x-ai/grok-voice-tts-1.0               $0.15/1M chars    5 voices
  mistralai/voxtral-mini-tts-2603       $16.00/1M chars  30 voices
  hexgrad/kokoro-82m                    $0.01/1M chars   54 voices
  canopylabs/orpheus-3b-0.1-ft          $0.50/1M chars    7 voices
  sesame/csm-1b                         $0.01/1M chars    7 voices
  zyphra/zonos-v0.1-transformer         $0.50/1M chars    5 voices
  zyphra/zonos-v0.1-hybrid              $0.50/1M chars    5 voices
```

**New service method:**
- `IModelService.GetTtsModelsAsync()` → queries `GET /api/v1/models?output_modalities=speech`
- Returns model ID, name, pricing, supported voice count, provider status

### 3. Voice Listing

**`--list-voices`** flag — lists available voices for the configured model (or `--model` override):

```
Voices for openai/gpt-4o-mini-tts-2025-12-15:
  alloy     ash       ballad    coral
  echo      fable     onyx      nova
  sage      shimmer   verse     marin
  cedar
```

For models with descriptive naming patterns, infer and display metadata:

```
Voices for mistralai/voxtral-mini-tts-2603:
  en_paul_sad         en_paul_neutral      en_paul_happy
  en_paul_frustrated  en_paul_excited      en_paul_confident
  en_paul_cheerful    en_paul_angry        gb_oliver_neutral
  ...

Voices for hexgrad/kokoro-82m:
  af_alloy (American female)    af_bella (American female)    am_adam (American male)
  bf_alice (British female)     bm_daniel (British male)      ...
```

**New service method:**
- `IModelService.GetVoicesForModelAsync(string modelId)` → returns list of voice IDs
- Local voice description inference from naming patterns:
  - `af_` → American female, `am_` → American male
  - `bf_` → British female, `bm_` → British male
  - `en_paul_` → English Paul + emotion, `gb_jane_` → British Jane + emotion
  - etc.

### 4. Model Override Flag

**`--model <model-slug>`** flag (optional, defaults to config `OpenRouter:TtsModel`):

- Overrides the TTS model for the current invocation
- Applies to `--list-voices`, `--voice all`, and synthesis

### 5. Service Refactoring

**New `IModelService` / `ModelService`:**
- `GetTtsModelsAsync()` → list all TTS models from OpenRouter
- `GetVoicesForModelAsync(string? modelId = null)` → list voices for a model
- Uses same `IHttpClientFactory` + API key auth as `TtsService`

**New models:**
- `Models/TtsModelInfo.cs` — model ID, name, pricing, voice count, status
- `Models/VoiceInfo.cs` — voice ID, inferred description (language, gender, emotion)

### 6. CLI Summary

Updated CLI surface:

```
Usage: benow-conversation <text-or-file> [options]

Arguments:
  <text-or-file>          Text to synthesize, or path to a text file

Options:
  --output <file>         Output filename (default: timestamped)
  --text-file <path>      Read text from file
  --voice <profile>       Voice profile name, or "all" for all voices
  --model <model-slug>    Override TTS model
  --list-models           List available TTS models
  --list-voices           List voices for current/specified model
  --help                  Show this help message
```

### 7. VS Code Launcher Updates

Add launch configurations:
- **"TTS all voices"** — runs with `--voice all` to generate samples for every profile
- **"List TTS models"** — runs with `--list-models`
- **"List voices"** — runs with `--list-voices`

### 8. Tests

- `ModelServiceTests`:
  - `GetTtsModelsAsync_ReturnsModels` — mock API response, verify parsing
  - `GetVoicesForModelAsync_ReturnsVoices` — mock model endpoints response
- `VoiceDescriptionTests`:
  - `InfersGenderFromKokoroPrefix` — `af_bella` → American female
  - `InfersEmotionFromVoxtralName` — `en_paul_happy` → English Paul, happy
  - `ReturnsRawNameWhenNoPattern` — `alloy` → no inference
- `TtsServiceTests` (additions):
  - `SynthesizeAllVoicesAsync_GeneratesAllFiles` — mock returns 3 voices, verify 3 files
  - `SynthesizeToFileAsync_UsesSpecifiedVoice` — verify voice param in request
  - `SynthesizeToFileAsync_InvalidVoice_ThrowsWithGuidance`

## Acceptance Criteria

- [ ] `dotnet build` succeeds with no warnings
- [ ] `dotnet test` passes
- [ ] `--list-models` displays all available TTS models with pricing
- [ ] `--list-voices` displays voices for the default model with inferred descriptions
- [ ] `--voice nova "Hello"` produces output with the nova voice
- [ ] `--voice all "Hello"` produces one MP3 per voice profile
- [ ] `--model hexgrad/kokoro-82m --list-voices` lists kokoro voices
- [ ] Invalid voice produces actionable error message
- [ ] All new flags reflected in `--help`

## Out of Scope

- Voice cloning (reference audio submission)
- Frontend / UI
- Audio playback
- Streaming
- Stdin pipe support
