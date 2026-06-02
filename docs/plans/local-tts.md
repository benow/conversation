# Local TTS Integration — CosyVoice Backend

## Summary

Replace cloud-based TTS (OpenRouter OpenAI/gemini) with local CosyVoice-300M-Instruct. Zero-shot voice cloning per persona, native emotion/instruct support, bi-directional streaming, Apache 2.0 license, zero censorship, no API costs.

## Architecture

```
ParallelTtsPlayer
  ├── ITtsProvider.SynthesizeAsync(text, voice, instructions, ct)
  │     ├── OpenRouterTtsProvider (existing, via OpenRouter API)
  │     └── CosyVoiceTtsProvider  (new, via local HTTP server)
  └── PersistentAudioPipeline (unchanged — receives PCM stream)
```

### TTS Backend Selection

Config key `TtsBackend` determines which provider is active:
- `"openrouter"` — existing cloud TTS (default, current behavior)
- `"cosyvoice"` — local CosyVoice server

Both providers share the same `ITtsProvider` interface. The selection is done at boot via DI factory.

### Voice Mapping

| Backend | Persona `Voice` field | Provider sends |
|---|---|---|
| OpenRouter | `"nova"`, `"verse"`, etc. | OpenRouter voice name |
| CosyVoice | `"voices/female-2-nova.wav"` | Reference audio file path |

Bootstrapping: use OpenRouter to synthesize one sentence per persona voice, save as `.wav` files in `voices/`. These become permanent reference clips for CosyVoice.

### Emotion / Instructions

Both backends receive `instructions` string from `ComposeInstructions()`. 

- **OpenRouter**: sends via `provider.options.openai.instructions` in request body
- **CosyVoice**: sends via instruct text parameter — `"speak in a whisper"`, `"sound excited and breathless"` — native emotion control, no modifier LLM needed

The modifier injection pipeline (`ModifierInjector`, modifier mappings in `ParallelTtsPlayer`) becomes optional when running CosyVoice. The `ComposeInstructions` method still merges persona base + modifier + thought instructions, but CosyVoice interprets them as direct style directions rather than requiring separate LLM annotation.

## Implementation Phases

### Phase 1 — CosyVoice Installation

**Target**: CosyVoice server running locally, verified with one test clip.

**Installation script** `scripts/install-cosyvoice.sh`:
```bash
#!/bin/bash
set -e
# 1. Create Python venv
python3 -m venv /opt/cosyvoice-venv
source /opt/cosyvoice-venv/bin/activate

# 2. Install dependencies
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cpu
pip install cosyvoice soundfile fastapi uvicorn python-multipart

# 3. Download model
python3 -c "
from modelscope import snapshot_download
snapshot_download('iic/CosyVoice-300M-Instruct', local_dir='pretrained_models/CosyVoice-300M-Instruct')
"

# 4. Verify
python3 -c "from cosyvoice.cli.cosyvoice import CosyVoice; print('OK')"
```

**Server wrapper** `scripts/cosyvoice-server.py`:
```python
"""FastAPI server wrapping CosyVoice-300M-Instruct for TTS requests."""
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
import io, base64

app = FastAPI()
model = None  # lazy init

class TtsRequest(BaseModel):
    text: str
    voice_ref: str  # path to reference audio file
    instructions: str | None = None  # style/emotion direction

@app.post("/v1/tts")
async def synthesize(req: TtsRequest):
    # Load reference voice
    # Synthesize with instruct
    # Return PCM audio stream
    ...
```

### Phase 2 — ITtsProvider Abstraction

**Interface** `Services/ITtsProvider.cs`:
```csharp
public interface ITtsProvider
{
    /// <summary>Synthesize speech to a PCM audio stream.</summary>
    Task<Stream> SynthesizeAsync(
        string text,
        string voice,
        string? instructions,
        double? temperature,
        int? seed,
        CancellationToken ct);

    /// <summary>Whether instructions modify delivery style directly (vs requiring LLM annotation).</summary>
    bool SupportsNativeInstructions { get; }
}
```

**OpenRouterTtsProvider** — wraps existing `ITtsService`, adapts to `ITtsProvider`. Uses `SynthesizeLiveStreamAsync` internally. Passes `instructions` via `provider.options.openai.instructions`.

**CosyVoiceTtsProvider** — HTTP client to local server. Sends `text`, `voice_ref` (path to reference audio), `instructions`. Receives PCM audio stream.

**DI factory** in `Program.cs`:
```csharp
services.AddSingleton<ITtsProvider>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    return settings.TtsBackend switch
    {
        "cosyvoice" => ActivatorUtilities.CreateInstance<CosyVoiceTtsProvider>(sp),
        _ => ActivatorUtilities.CreateInstance<OpenRouterTtsProvider>(sp)
    };
});
```

### Phase 3 — Voice Reference Bootstrapping

**Script** `scripts/bootstrap-voices.sh`:
```bash
#!/bin/bash
# Uses the existing OpenRouter TTS to generate one reference clip per persona.
# Each clip is 2-3 seconds, saved to voices/ directory.
# After bootstrapping, switch TtsBackend to "cosyvoice".

PERSONAS=(female-1 female-2 ... female-13 male-1)
for p in "${PERSONAS[@]}"; do
    DOTNET_ENVIRONMENT=Development dotnet run --project src/benow-conversation \
        --persona "$p" \
        --output "voices/$p.wav" \
        "Hello, this is my voice reference."
done
```

### Phase 4 — ParallelTtsPlayer Integration

**Current flow**:
```
ComposeInstructions(segment, persona)
  → _ttsService.SynthesizeLiveStreamAsync(text, voice, instructions, temp, seed, model)
    → OpenRouter API
```

**New flow**:
```
ComposeInstructions(segment, persona)
  → _ttsProvider.SynthesizeAsync(text, voice, instructions, temp, seed, ct)
    → OpenRouter API  OR  CosyVoice local server
```

Changes to `ParallelTtsPlayer`:
- Replace `ITtsService` dependency with `ITtsProvider`
- Remove `_currentModel` tracking (provider handles model selection internally)
- When `ITtsProvider.SupportsNativeInstructions` is true, skip modifier LLM injection — send instructions directly
- Otherwise, keep existing modifier injection pipeline (for OpenRouter backend)

### Phase 5 — Modifier Pipeline Optimization (CosyVoice mode)

When `TtsBackend == "cosyvoice"`:
- `ModifierInjector` is NOT called (native instruction support)
- `AutoInjectModifiers` config is ignored
- The existing modifier list in `CharacterParser.KnownModifiers` is still used for parsing, but `ComposeInstructions` produces instructions directly for CosyVoice's native understanding

When `TtsBackend == "openrouter"`:
- Full existing pipeline: modifier injection → character parsing → instructions via provider options
- `AutoNormalize` runs as before

### Phase 6 — Config

```json
{
  "TtsBackend": "openrouter",
  "CosyVoice": {
    "ServerUrl": "http://localhost:50000",
    "ReferenceAudioDir": "voices/",
    "TimeoutSeconds": 10,
    "DefaultVoice": "female-1" // fallback if persona Voice field is missing
  }
}
```

Add to `AppSettings.cs`:
```csharp
public class CosyVoiceSettings
{
    public string ServerUrl { get; set; } = "http://localhost:50000";
    public string ReferenceAudioDir { get; set; } = "voices/";
    public int TimeoutSeconds { get; set; } = 10;
    public string DefaultVoice { get; set; } = "female-1";
}
```

## Test Plan

| Test | What it validates |
|---|---|
| OpenRouter provider still works | No regression on existing backend |
| CosyVoice install script completes | Dependencies install, model downloads, server starts |
| Single-segment synthesis via CLI | `dotnet run -- --persona female-1 "test"` uses local TTS |
| Multi-character via proxy | Full pipeline: prose → normalizer → parser → allocator → CosyVoice → ffplay |
| Voice consistency | Same character always uses same reference clip → consistent voice |
| Instruction delivery | (whisper), (excited) etc. produce audible style changes |
| Self persona | male-1 reference clip used for [Self] segments |
| Fallback behavior | If CosyVoice server is down, raise clear error (do not silently fail) |

## Rollback Plan

- `TtsBackend: "openrouter"` restores full cloud TTS
- CosyVoice installation is additive — no existing files modified
- Persona config unchanged — Voice field works for both backends

## Open Questions

1. **Reference audio quality**: Do the bootstrapped OpenRouter clips produce good clones, or do we need to record fresh reference audio per persona?
2. **Emotion granularity**: How well does CosyVoice-300M-Instruct handle subtle instructions like "teasing" vs "flirtatious" vs "thirsty"?
3. **CPU load**: With 16 threads on Z1 Extreme, what's the RTF (real-time factor) for 300M model? Can it keep up with 10+ concurrent segments from ParallelTtsPlayer?
4. **Python server lifecycle**: Should the .NET proxy start/stop the Python server? Or run separately?
5. **PCM format**: What exact PCM format does CosyVoice output vs what ffplay expects? 24kHz vs 24kHz mono vs 48kHz?
