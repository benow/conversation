# Audio Format Negotiation & Dynamic Conversion

## Problem

TTS providers return audio in different formats: Replicate returns WAV, OpenRouter returns MP3, Kokoro returns raw PCM. The ffplay pipeline is configured with a fixed format at startup. Two issues arise:

1. **Format mismatch**: ffplay told to expect MP3 but receiving WAV causes silent crashes or playback failures.
2. **WAV is not streamable**: `ffplay -f wav` reads one WAV file from the pipe and stops. Subsequent segments piped to the same ffplay instance are silently ignored. Multi-character playback (16+ segments) only works if ffplay is restarted between segments, which currently only happens on gender change via `InterruptAsync`.

The user hears only the first segment(s) and then silence until a gender-change triggers an ffplay restart.

## Target Architecture

```
ITtsProvider.SynthesizeAsync()
    → returns Stream audio           (format declared via ITtsProvider.OutputFormat property)
         ↓
    AudioFormatConverter.Convert(audio, provider.OutputFormat, target: Pcm24000Mono)
         ↓
    PersistentAudioPipeline (always ffplay -f s16le -ar 24000 -ac 1 -i pipe:0)
```

The pipeline is a **dumb PCM sink** — never changes format, never restarts for format reasons. Adaptation happens per chunk, before the pipe. Zero encoding overhead for PCM→PCM and WAV→PCM (header strip). MP3→PCM uses an ffmpeg subprocess.

The `OutputFormat` property is the canonical way providers declare their format. The return type of `SynthesizeAsync` stays `Task<Stream>` — the format is known statically from the provider type, not per-call. This avoids changing the call signature everywhere and means callers can resolve the converter path before synthesis even completes.

### Why PCM as the universal format

| Format | Streamable? | Header overhead | Gap-tolerant? |
|--------|------------|----------------|---------------|
| WAV    | No         | 44 bytes       | No — one file then stops |
| MP3    | Yes        | Frame headers   | Yes, but demuxer state-dependent |
| PCM    | Yes        | None            | Yes — ffplay reads bytes forever |

Raw PCM (16-bit signed little-endian, mono) is the only format where ffplay has no parser state, no EOF concept, no headers to close on. Bytes in → audio out. Gaps of any duration between writes don't matter.

## Components

### 1. `AudioFormat` struct

```csharp
public readonly record struct AudioFormat(
    string Codec,       // "pcm", "wav", "mp3"
    int SampleRate,     // e.g. 24000
    int Channels,       // 1 = mono, 2 = stereo
    int BitsPerSample   // 16
)
{
    public static AudioFormat Pcm24000Mono => new("pcm", 24000, 1, 16);
    public int BytesPerSecond => SampleRate * Channels * (BitsPerSample / 8);

    public bool IsCompatibleWith(AudioFormat target) =>
        Codec == target.Codec &&
        SampleRate == target.SampleRate &&
        Channels == target.Channels &&
        BitsPerSample == target.BitsPerSample;
}
```

### 2. `ITtsProvider` extension

```csharp
public interface ITtsProvider
{
    AudioFormat OutputFormat { get; }
    Task<Stream> SynthesizeAsync(...);
}
```

Each provider declares its output:
- `ReplicateTtsProvider` → `AudioFormat("wav", 24000, 1, 16)` (from WAV header after first call; hardcoded until detection)
- `OpenRouterTtsProvider` → `AudioFormat("mp3", 24000, 1, 16)` (from `ProviderFormats` config or TtsService cache)
- `KokoroTtsProvider` → `AudioFormat("pcm", 24000, 1, 16)` (hardcoded — known PCM backend)

Note on `OpenRouterTtsProvider`: currently its `SynthesizeAsync` calls `TtsService.SynthesizeToStreamAsync()` which returns `(Stream, string format)` but the format string is discarded. After this change, `OpenRouterTtsProvider.OutputFormat` derives from `TtsService`'s existing `_modelFormats` cache or the new `ProviderFormats` config. The `TtsService` format cache is promoted to a shared concern.

The pipeline's target format is always `AudioFormat.Pcm24000Mono`.

### 3. `AudioFormatConverter`

```csharp
public class AudioFormatConverter
{
    public Stream Convert(Stream source, AudioFormat sourceFormat, AudioFormat targetFormat);
}
```

Registry of converters:

| Source | Target | Converter | Overhead |
|--------|--------|-----------|----------|
| PCM | PCM | Passthrough | Zero |
| WAV | PCM | Strip 44-byte RIFF header, validate sample rate | Near-zero |
| MP3 | PCM | `ffmpeg -f mp3 -i pipe:0 -f s16le -ar 24000 -ac 1 pipe:1` | Subprocess overhead (one reusable instance) |

WAV header stripping:
```csharp
static byte[] StripWavHeader(byte[] wav)
{
    // Parse RIFF header: find "data" chunk, return everything after 8-byte chunk header
    var dataOffset = FindWavDataOffset(wav);
    return wav[dataOffset..];
}
```

MP3 decoder (reusable):
```csharp
// Persistent ffmpeg subprocess: mp3 → pcm
// Started once, reused for all MP3 conversions in the session
var psi = new ProcessStartInfo
{
    FileName = "ffmpeg",
    Arguments = "-f mp3 -i pipe:0 -f s16le -ar 24000 -ac 1 pipe:1",
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    UseShellExecute = false
};
```

### 4. Pipeline always uses PCM

In `Program.cs`, the pipeline format is no longer conditional on TTS backend:

```csharp
// Before (current):
format: settings.TtsBackend switch { "replicate" => "wav", ... }

// After:
format: "pcm"  // always — AudioFormatConverter handles provider differences
```

ffplay args: `-f s16le -ar 24000 -ac 1 -probesize 32 -analyzeduration 0 -i pipe:0`

The sample rate is configurable (`Audio.PcmSampleRate`, default 24000). Changing it restarts the pipeline naturally.

### 5. Integration points

**ParallelTtsPlayer** (multi-character segments):
```csharp
var audio = await _ttsProvider.SynthesizeAsync(...);
var converted = _converter.Convert(audio, _ttsProvider.OutputFormat, AudioFormat.Pcm24000Mono);
// Sequential playback loop — only one ConvertAsync in flight at any time.
if (_pipeline != null)
    await _pipeline.PipeAsync(converted, ct);
else
    await _audioPlayer.PlayStreamAsync(converted, "pcm", cancellationToken: ct);
```

**SpeechQueue** (single-character TTS):
```csharp
var audio = await _ttsProvider.SynthesizeAsync(...);
var converted = _converter.Convert(audio, _ttsProvider.OutputFormat, AudioFormat.Pcm24000Mono);
// Single-consumer ProcessQueueAsync — no concurrent ConvertAsync calls.
if (_pipeline != null)
    await _pipeline.PipeAsync(converted, ct);
else
    await _audioPlayer.PlayStreamAsync(converted, "pcm", cancellationToken: ct);
```

The `IAudioPlayer.PlayStreamAsync` fallback path passes `"pcm"` (not `"wav"` as currently hardcoded) because conversion already happened. `AudioPlayer.BuildStreamArgs` reads the PCM sample rate from config (`Audio.PcmSampleRate`) instead of the current hardcoded `-ar 24000`.

**ReplayLastAsync** (retained for Phase 3): stored segments are already converted PCM bytes, so `_audioPlayer.PlayStreamAsync(ms, "pcm", ...)`.

Conversion happens right before `PipeAsync` or `PlayStreamAsync` in both paths. One place, all providers covered.

Note on fallback: `_pipeline` is null when ffplay is unavailable. The fallback through `IAudioPlayer.PlayStreamAsync` spawns a one-shot ffplay per segment, using the same PCM format parameters as the persistent pipeline. Both paths are format-consistent.

### 6. WAV header parser & full format detection

Proper RIFF parsing — not hardcoded 44 bytes. Some WAVs embed metadata chunks (LIST, fact, etc.) before the "data" chunk.

```
RIFF header (12 bytes):
  "RIFF" (4) + fileSize (4) + "WAVE" (4)
Chunks:
  "fmt " (4) + chunkSize (4) + formatData
  "data" (4) + chunkSize (4) + audioData...
  (other chunks: "fact", "LIST", etc.)
```

The `fmt ` chunk contains:
- Audio format tag (1 = PCM)
- Number of channels (1 = mono, 2 = stereo)
- Sample rate (e.g. 22050, 24000, 44100, 48000)
- Byte rate
- Block align
- Bits per sample (8, 16, 24, 32)

Parse the `fmt ` chunk to populate `AudioFormat`. Find the `data` chunk for the raw PCM offset.

### 7. Conversion decision matrix

For each audio chunk, compare source `AudioFormat` (from provider or WAV header) against pipeline target:

| Source format | Same rate? | Same channels? | Same bits? | Action |
|---|---|---|---|---|
| WAV 24000/mono/16 | Yes | Yes | Yes | **Header strip** — remove RIFF wrapper, pipe raw PCM |
| WAV 44100/mono/16 | No | Yes | Yes | **ffmpeg resample** — `ffmpeg -f s16le -ar 44100 -ac 1 -i pipe:0 -f s16le -ar 24000 -ac 1 pipe:1` |
| WAV 24000/stereo/16 | Yes | No | Yes | **ffmpeg downmix** — `-ac 2` in, `-ac 1` out |
| WAV 22050/mono/24 | No | Yes | No | **ffmpeg resample + convert** — change rate and bit depth |
| PCM 24000/mono/16 | Yes | Yes | Yes | **Passthrough** — zero overhead |
| MP3 (any internal params) | N/A | N/A | N/A | **ffmpeg decode** — `-f mp3 -i pipe:0 -f s16le -ar 24000 -ac 1 pipe:1` |

The converter selects the cheapest path:
1. If source codec is PCM and all params match → **passthrough**
2. If source is WAV and all params match → **header strip** (cheapest conversion possible)
3. All other cases → **ffmpeg subprocess** (reusable, one per session)

### 8. Reusable ffmpeg subprocess design (Phase 4+)

Rather than spawning ffmpeg per chunk (Phase 1 approach), a persistent instance avoids cold-start latency on every chunk. Thread-safety is provided by the calling pattern: the converter is invoked from the sequential playback loop in `ParallelTtsPlayer` (line 116) and the single-consumer `ProcessQueueAsync` in `SpeechQueue`. Only one `ConvertAsync` call is in-flight at any moment, so a single shared subprocess is safe.

```csharp
public class FfmpegConverter : IDisposable
{
    private Process? _process;
    private Stream? _stdin;
    private Stream? _stdout;

    public void Configure(AudioFormat source, AudioFormat target)
    {
        var args = BuildArgs(source, target);
        // e.g. "-f s16le -ar 44100 -ac 2 -i pipe:0 -f s16le -ar 24000 -ac 1 pipe:1"
        StartProcess(args);
    }

    public async Task<byte[]> ConvertAsync(byte[] audio, CancellationToken ct)
    {
        await _stdin.WriteAsync(audio, ct);
        await _stdin.FlushAsync(ct);
        // ffmpeg writes out same number of samples; read until stdout stream ends for this chunk.
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await _stdout.ReadAsync(buffer, ct)) > 0)
            await ms.WriteAsync(buffer.AsMemory(0, read), ct);
        await _stdout.FlushAsync(ct);
        return ms.ToArray();
    }
}
```

When the source format changes (different provider returns different audio), reconfigure the ffmpeg subprocess. In practice this is rare — a single TTS provider typically uses consistent output throughout a session.

### 9. Pipeline PCM parameters

The pipeline target is fixed at startup from config:

```json
{
  "Audio": {
    "OutputFormat": "mp3",       // legacy — for file output, not pipeline
    "PcmSampleRate": 24000,      // new
    "PcmChannels": 1,            // new (mono)
    "PcmBitsPerSample": 16       // new
  }
}
```

ffplay args: `-f s16le -ar {PcmSampleRate} -ac {PcmChannels} -probesize 32 -analyzeduration 0 -i pipe:0`

Changing these values restarts the pipeline naturally (format string rebuilds on next `EnsureProcessAsync`).

### 10. `ProviderFormatCache`

Persists detected audio formats per model/provider to `appsettings.json` for survival across restarts, and maintains an in-memory `ConcurrentDictionary` for instant lookup during a session.

```csharp
public class ProviderFormatCache
{
    private readonly ConcurrentDictionary<string, AudioFormat> _cache = new();
    private readonly string _configPath;
    private readonly ILogger<ProviderFormatCache> _logger;
    private readonly object _writeLock = new();

    /// <summary>Look up format for a model/provider key. Returns null if not yet known.</summary>
    public AudioFormat? Get(string key);

    /// <summary>Store format in memory and persist to appsettings.json.</summary>
    public void Set(string key, AudioFormat format);

    /// <summary>Load all entries from appsettings.json on startup.</summary>
    public void Load();

    /// <summary>Clear all entries (called by /reset).</summary>
    public void Clear();
}
```

Key format: `"replicate/lucataco-xtts-v2"`, `"openrouter/openai-gpt-4o-mini-tts"`. This supersedes `TtsService`'s existing `ConcurrentDictionary<string, string> _modelFormats` (which only stores a format codec string like `"mp3"` or `"pcm"`, not full `AudioFormat`). During migration, populate `ProviderFormatCache` from the existing `_modelFormats` entries on first load, then remove `_modelFormats` from `TtsService`.

Config persistence: uses the same full-file read-modify-write approach as `SavePersona` and `SaveOutputProfile` in `Program.cs`. A new `SaveProviderFormats` method reads `appsettings.json`, updates the `ProviderFormats` dictionary, serializes, and writes back. Alternatively, accept a new `Dictionary<string, AudioFormat>` field on `AppSettings` so standard `IOptions<T>` serialization handles it — this is simpler and avoids a second JSON parse step. `AppSettings.ProviderFormats` is typed as `Dictionary<string, AudioFormat>` with `AudioFormat` having JSON-serializable properties.

## Format Discovery & Caching

Rather than requiring users to configure audio format parameters per provider/model, the system auto-detects format on first use and persists it to config. Subsequent requests skip detection entirely.

This replaces `TtsService`'s existing `ConcurrentDictionary<string, string> _modelFormats` (which only caches a format codec like `"mp3"` or `"pcm"` for the session). The new `ProviderFormatCache` stores full `AudioFormat` structs (codec + rate + channels + bits) and survives restarts via `appsettings.json`.

### How it works

```
First TTS request for model X on provider Y
    → synthesize, receive audio
    → inspect response: parse WAV header / check content-type / probe MP3 headers
    → determine AudioFormat{codec, rate, channels, bits}
    → write to appsettings.json: ProviderFormats["replicate/lucataco-xtts-v2"] = {codec:"wav", rate:24000, ...}
    → cache in memory

Subsequent requests for same model+provider
    → read ProviderFormats from config (loaded into in-memory cache on startup)
    → use cached AudioFormat — no inspection
```

### Config schema

```json
{
  "ProviderFormats": {
    "replicate/lucataco-xtts-v2": {
      "Codec": "wav",
      "SampleRate": 24000,
      "Channels": 1,
      "BitsPerSample": 16
    },
    "openrouter/openai-gpt-4o-mini-tts": {
      "Codec": "mp3",
      "SampleRate": 24000,
      "Channels": 1,
      "BitsPerSample": 16
    }
  }
}
```

### Write behavior

- Config is written immediately after first successful synthesis for a given model/provider pair. Uses a `ProviderFormats` dictionary field on `AppSettings` + standard `IOptions<T>` serialization through a helper method similar to `SavePersona` and `SaveOutputProfile` in `Program.cs`.
- In-memory cache (`ConcurrentDictionary` in `ProviderFormatCache`) provides instant lookup. Config is the backing store — survives restarts.
- If config already has an entry, detection is skipped. The config is the source of truth.
- User can manually add entries for new providers if they know the format ahead of time.
- Migration: on first load, populate `ProviderFormats` from `TtsService`'s existing `_modelFormats` dictionary entries (where available), then remove `_modelFormats` from `TtsService`.

### Detection strategies

| Provider | Detection method |
|---|---|
| Replicate (WAV) | Parse RIFF header: read `fmt ` chunk for rate/chans/bits |
| OpenRouter (MP3) | Check `Content-Type` response header (typically `audio/mpeg`). Probe first MPEG frame header for sample rate. |
| Kokoro (PCM) | Provider is known PCM 24000/mono/16 — no detection needed, hardcoded in `KokoroTtsProvider.OutputFormat` |
| Unknown/future | Fall back to WAV header parse; if that fails, log warning and use a safe default (PCM 24000/mono/16) |

### Clear on `/reset`

When the user issues `/reset` in chat, the `ProviderFormats` section is cleared alongside persona assignments. This forces re-detection on the next TTS request, accommodating model changes.

## Implementation Phases

### Phase 1 — Immediate Fix (all backends → PCM, inline conversion)

**Goal**: Fix the "no sound after first segment" bug for all three backends. Minimal new classes, no interface changes.

1. Pipeline format hardcoded to `"pcm"` in both `Program.cs` and `PersistentAudioPipeline.BuildArgs` (currently hardcodes `-ar 24000` inline; move to config key `Audio.PcmSampleRate` with default 24000).
2. Inline conversion helper methods (static, no DI) in a new `AudioFormatConverter.cs`:
   - `StripWavHeader(byte[])` — simple 44-byte RIFF header strip (known xtts-v2 output format)
   - `ConvertMp3ToPcmAsync(byte[], ct)` — spawns an ffmpeg subprocess per chunk: `ffmpeg -f mp3 -i pipe:0 -f s16le -ar 24000 -ac 1 pipe:1`, writes MP3 bytes to stdin, reads PCM output. Per-chunk subprocess avoids shared-state issues; this is pragmatic for Phase 1 and gets replaced by the persistent subprocess in Phase 4.
   - `Passthrough(byte[])` — identity
3. In `ParallelTtsPlayer` and `SpeechQueue`, before `PipeAsync` or `PlayStreamAsync`, inspect the TTS backend and convert:
   - `"replicate"` → `StripWavHeader` then pipe as PCM
   - `"openrouter"` → `ConvertMp3ToPcmAsync` then pipe as PCM
   - `"kokoro"` → passthrough (already raw PCM)
4. Update `SpeechQueue` fallback path: Kokoro currently sends PCM bytes to `PlayStreamAsync(stream, "wav")` — a pre-existing bug. Fix by passing format based on backend.
5. Update `ParallelTtsPlayer` fallback path: currently hardcodes `PlayStreamAsync(playMs, "wav", ...)` when `_pipeline` is null. After conversion, pass `"pcm"` instead.
6. Update `AudioPlayer.BuildStreamArgs` to read PCM sample rate from config (`Audio.PcmSampleRate`) instead of hardcoding 24000.
7. Verify multi-character playback works end-to-end for all three backends.

**Files changed**: `Program.cs`, `ParallelTtsPlayer.cs`, `SpeechQueue.cs`, `PersistentAudioPipeline.cs`, `AudioPlayer.cs`, `AppSettings.cs` (new `Audio.PcmSampleRate` key), `appsettings.json`, new `AudioFormatConverter.cs`

### Phase 2 — `AudioFormat` + `ITtsProvider.OutputFormat`

**Goal**: Provider self-declares format. No hardcoded assumptions.

1. Add `AudioFormat` record struct
2. Add `OutputFormat` property to `ITtsProvider`
3. Implement in `ReplicateTtsProvider`, `OpenRouterTtsProvider`, `KokoroTtsProvider`
4. For `OpenRouterTtsProvider`: resolve `OutputFormat` from `TtsService`'s existing `_modelFormats` cache (bridging to `ProviderFormatCache` in Phase 3)
5. Update `ParallelTtsPlayer` and `SpeechQueue` to read format from provider instead of hardcoding by backend name

**Files changed**: New `AudioFormat.cs`, `ITtsProvider.cs`, `ReplicateTtsProvider.cs`, `OpenRouterTtsProvider.cs`, `KokoroTtsProvider.cs`, `TtsService.cs` (expose format cache), `ParallelTtsPlayer.cs`, `SpeechQueue.cs`

### Phase 3 — `AudioFormatConverter` + `ProviderFormatCache`

**Goal**: Pluggable converter registry + format persistence. Replaces Phase 1 inline helpers with proper RIFF parsing and caching.

1. Upgrade `AudioFormatConverter` with converter registry (replaces Phase 1 inline helpers)
2. WAV→PCM converter: proper RIFF parsing (not hardcoded 44 bytes), validates rate/channels/bits from `fmt ` chunk
3. PCM→PCM converter: passthrough
4. MP3→PCM converter: promote Phase 1 per-chunk ffmpeg to configurable (handles rate/channel mismatch)
5. `ProviderFormatCache` class: in-memory `ConcurrentDictionary` + `appsettings.json` persistence
6. Add `ProviderFormats` dictionary to `AppSettings` class
7. Wire into `ParallelTtsPlayer` and `SpeechQueue`
8. Integrate with `/reset` command to clear `ProviderFormats`

**Files changed**: `AudioFormatConverter.cs` (upgraded), `ProviderFormatCache.cs` (new), `AppSettings.cs`, `Program.cs`, `appsettings.json`, `PersistentAudioPipeline.cs`, `AudioPlayer.cs`

### Phase 4 — Persistent MP3 Decoder (optimization)

**Goal**: Replace Phase 1 per-chunk ffmpeg spawn with a persistent subprocess that avoids cold-start latency (~200ms per chunk).

1. Upgrade `Mp3ToPcmConverter` to use a persistent ffmpeg subprocess (reusable per session)
2. Subprocess lifecycle management (start on first use, reconfigure on source format change, kill on daemon shutdown)
3. Error handling for ffmpeg crashes: auto-restart, current chunk is lost but `ParallelTtsPlayer` retry logic re-synthesizes

**Files changed**: `AudioFormatConverter.cs`

## Testing

### Unit Tests

| Test | What it validates | Phase |
|---|---|---|
| `WavHeaderParser_ParsesStandardWav` | Extracts rate=24000, chans=1, bits=16 from a valid 44-byte RIFF header | 2 |
| `WavHeaderParser_ParsesWavWithExtraChunks` | Skips LIST/fact chunks, correctly finds "data" chunk offset | 2 |
| `WavHeaderParser_HandlesStereo` | Extracts channels=2 from stereo WAV | 2 |
| `WavHeaderParser_Handles44100Hz` | Extracts sample rate 44100 from WAV header | 2 |
| `WavHeaderParser_RejectsMalformedHeader` | Returns null/false for non-RIFF data, logs warning | 2 |
| `WavHeaderParser_Handles24Bit` | Extracts bitsPerSample=24 | 2 |
| `AudioFormat_IsCompatibleWith_MatchingParams` | Returns true for identical formats | 2 |
| `AudioFormat_IsCompatibleWith_DifferentRate` | Returns false when sample rates differ | 2 |
| `AudioFormat_IsCompatibleWith_DifferentChannels` | Returns false for mono vs stereo | 2 |
| `AudioFormatConverter_StripsWavHeader_Compatible` | WAV 24000/mono/16 → correct PCM byte array (length = original - header offset) | 3 |
| `AudioFormatConverter_Passthrough_Compatible` | PCM 24000/mono/16 → identical byte array | 3 |
| `AudioFormatConverter_UsesFfmpeg_44100To24000` | WAV 44100/mono/16 → ffmpeg resample → PCM 24000/mono/16 | 3 |
| `AudioFormatConverter_UsesFfmpeg_StereoToMono` | WAV 24000/stereo/16 → ffmpeg downmix → PCM 24000/mono/16 | 3 |
| `ProviderFormatCache_ReadsFromConfig` | `ProviderFormats` in appsettings.json is loaded into in-memory cache on startup | 3 |
| `ProviderFormatCache_WritesToConfig` | First synthesis for a new model/provider persists format to appsettings.json | 3 |
| `ProviderFormatCache_SkipsDetectionWhenCached` | Second synthesis for same model/provider uses cached format, no header inspection | 3 |
| `ProviderFormatCache_ClearedOnReset` | `/reset` clears `ProviderFormats` section alongside persona assignments | 3 |

### Integration Tests

| Test | What it validates | Phase |
|---|---|---|
| `FullPipeline_ReplicateWav_PlaysAllSegments` | 8+ segments via ParallelTtsPlayer, all heard (no silent segments), no ffplay restarts | 1 |
| `FullPipeline_OpenRouterMp3_PlaysAllSegments` | 8+ segments with OpenRouter backend, MP3→PCM conversion, all segments audible | 1 |
| `FullPipeline_MixedProviders_SingleConversation` | Two different providers returning different formats, all segments play correctly | 3 |
| `FullPipeline_FirstRequest_DetectsAndCaches` | First TTS request auto-detects format, persists to config, subsequent requests use cache | 3 |
| `FullPipeline_44100HzWav_ResampledTo24000` | Provider returns WAV at 44100Hz, converter resamples, pipeline plays at 24000Hz | 3 |
| `FullPipeline_FfmpegCrash_Recovers` | Kill ffmpeg subprocess mid-conversion, verify retry re-synthesizes and replays | 3 |

### Golden Master / Regression

All existing golden master tests (`CharacterParser`, `ModifierInjector`, `ParagraphSplitter`) must continue to pass unchanged. Format conversion is a playback-layer concern — no parsing or text pipeline behavior should change.

Add golden master fixtures:
- `Wav24000Mono_Parsed.bytes` — sample WAV header (44 bytes) with known values
- `Wav44100Stereo_Parsed.txt` — expected `AudioFormat` from parsing a 44100Hz stereo header

### Manual Verification Checklist

- [ ] Single-character TTS (SpeechQueue path): audio plays, no broken pipe
- [ ] Multi-character TTS (ParallelTtsPlayer): all segments audible, no silent gaps
- [ ] OpenRouter backend → PCM pipeline: verify MP3→PCM conversion via ffmpeg, all segments play
- [ ] Replicate backend → PCM pipeline: verify WAV header strip, all segments play
- [ ] Kokoro backend → PCM pipeline: verify passthrough, all segments play
- [ ] Gender change between segments: ffplay restarts, new voice played
- [ ] `/reset` command: `ProviderFormats` cleared from appsettings.json
- [ ] First run after `/reset`: format re-detected, new entry written to config
- [ ] Config survives daemon restart: `ProviderFormats` persists between runs

## Documentation

### Files to update

| File | Change |
|---|---|
| `docs/overview.md` | Add "Audio Format Negotiation" section describing the pipeline target (PCM), converter layer, and format discovery |
| `docs/proxy-service.md` | Update TTS pipeline diagram to show `AudioFormatConverter` between provider and pipeline |
| `docs/configuration.md` | Document new config keys: `Audio.PcmSampleRate`, `Audio.PcmChannels`, `Audio.PcmBitsPerSample`, and `ProviderFormats` section |
| `AGENTS.md` | Add format negotiation section under Pipeline Notes |
| `appsettings.json` | Add commented example `ProviderFormats` entries so users see the schema |

### API docs (inline)

- `AudioFormat` record struct — XML doc comments on each field
- `ITtsProvider.OutputFormat` — XML doc explaining how format is determined and cached
- `AudioFormatConverter.Convert` — XML doc describing conversion paths and subprocess usage
- `ProviderFormatCache` — XML doc on detection, caching, config persistence, and `/reset` behavior

## Design Decisions

**Why PCM over MP3 as universal format?** MP3 is fine for transmission (smaller), but as a stream format it has state — the demuxer tracks frame boundaries. Gaps between segments can desync it. PCM has zero state. Ffplay reads bytes, plays them. Restart-proof, gap-proof, provider-proof.

**Why not GStreamer?** GStreamer adds a complex plugin ecosystem and runtime dependency. Ffmpeg is already a dependency (ffplay is used for playback), handles all formats we need, and is more portable (Linux/macOS/Windows). For this use case — simple format conversion with no graph processing — ffmpeg is the right tool.

**Why convert before piping, not in a subprocess pipe?** Converting in-code (WAV header strip, PCM passthrough) has zero process overhead. Only MP3→PCM needs a subprocess, and we reuse one instance for the session lifetime. An alternative architecture — `TTS → ffmpeg subprocess → pipeline` — adds process overhead for every format, even when no conversion is needed.

**Why not restart ffplay between segments?** Restarting works (gender-change code proves it) but adds ~200ms silence per segment (kill + spawn + warmup). For 16 segments, that's ~3 seconds of cumulative dead air. PCM streaming avoids this entirely — continuous audio, no gaps.

## Edge Cases

- **WAV header advertises wrong sample rate**: Some TTS models produce a WAV header saying 44100Hz but the actual audio is 24000Hz. The converter can't detect this — it trusts the header. Mitigation: each provider's `OutputFormat` property is the source of truth. If a provider is known to misdeclare, its `OutputFormat` override takes precedence over WAV header parsing.
- **ffmpeg subprocess reconfigure**: If source format changes mid-session (rare), kill and restart the ffmpeg subprocess with new args. Buffer the audio chunk while restarting (~200ms cold start).
- **Provider changes format mid-session**: Each chunk is independently converted. If the conversion subprocess needs reconfiguring, it happens transparently.
- **ffmpeg subprocess crash during conversion**: Auto-restart the subprocess. The current chunk is lost but `ParallelTtsPlayer` and `SpeechQueue` already have retry logic that re-synthesizes failed segments.
- **WAV parsing failure (malformed header)**: Log warning, fall back to treating as raw PCM at the provider's declared format (trust `ITtsProvider.OutputFormat`).

## Open Questions

1. **Pipeline PCM config keys**: `Audio.PcmSampleRate`, `Audio.PcmChannels`, `Audio.PcmBitsPerSample` — new keys or reuse existing `OutputFormat` with a `Pipeline` sub-object? Three new keys at root of `Audio` is simpler.
2. **MP3 streaming without decode**: If OpenRouter becomes the primary provider, would keeping the pipeline as `-f mp3` (native MP3 frames) be simpler than decoding to PCM? Tradeoff: no subprocess overhead, but gaps between segments can desync the MP3 demuxer. The plan's answer to this is already decided: PCM as universal format. MP3 passthrough would break as soon as a non-MP3 provider (Replicate/WAV, Kokoro/PCM) is in the mix. The converter architecture is the right approach. Benchmarking both paths is deferred — the immediate need is correctness, not MP3 passthrough optimization.
3. **Multiple concurrent TTS backends in one conversation**: ParallelTtsPlayer already allocates different personas to different voices. If different voices use different TTS providers simultaneously, the converter handles per-segment conversion naturally. The ffmpeg subprocess might need reconfiguring between chunks if two providers have incompatible source formats — a rare but handled case. The sequential playback loop ensures only one `ConvertAsync` call at a time, so reconfiguration between chunks is safe.
4. **Should the pipeline accept stereo for future surround/ambient audio?** Mono is correct for speech. If we later add music or ambient effects, stereo output would need pipeline reconfig. Config-driven, straightforward.
