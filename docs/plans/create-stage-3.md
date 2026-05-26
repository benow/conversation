# Stage 3: Audio Playback & Streaming

## Goal

Add audio playback to speakers via `ffplay` (part of the existing ffmpeg dependency). After TTS generation, optionally play the audio through the system's audio output device. Support both file playback and streaming playback for lower latency.

## Context & Decisions

### Why ffplay (not ManagedBass or NAudio)

The high-level plan originally specified **ManagedBass** (wraps un4seen BASS library) for cross-platform audio playback. After evaluation:

- **ffplay** is part of the ffmpeg suite, which is already a required dependency for PCM→MP3 conversion
- **ManagedBass** requires separate native `.so`/`.dll`/`.dylib` binaries per platform, adding packaging complexity
- **NAudio** (11M downloads) is primarily Windows-focused; Linux ALSA support is experimental
- ffplay gives us playback, volume control, seeking, and format support for free
- ffplay supports streaming from stdin, enabling real-time playback before the full TTS response completes
- Zero additional NuGet dependencies

### Trade-off

- ffplay spawns an external process (not in-process audio)
- No fine-grained audio control (waveform analysis, mixing, etc.)
- For our use case (TTS playback), this is perfectly adequate

## Tasks

### 1. Audio Player Service

**`IAudioPlayer` / `AudioPlayer`** — wraps ffplay for audio playback:

```csharp
public interface IAudioPlayer
{
    Task PlayAsync(string filePath, double? volume = null);
    Task PlayStreamAsync(Stream audioStream, string format = "mp3", double? volume = null);
    bool IsAvailable { get; }
}
```

- `PlayAsync` — plays a file by path using `ffplay -nodisp -autoexit -loglevel quiet -i <path>`
- `PlayStreamAsync` — pipes audio data to ffplay via stdin for streaming playback
- `IsAvailable` — checks if ffplay is on PATH (reuse `AudioConverter.IsFfmpegAvailable` pattern)
- Volume control via `-volume` flag (0-100 scale)
- Process lifecycle management: ensure ffplay process is killed on cancellation

### 2. CLI Flag: `--play`

**`--play`** flag (optional, boolean):

- When present, plays the generated audio through speakers after saving
- Works with single voice, `--voice all`, `--model all`, and `--model all --voice all`
- For batch modes, plays each file sequentially (or optionally just the first file)

### 3. Streaming Playback (Low Latency)

**`--stream`** flag (optional, boolean):

- Instead of waiting for full TTS response, stream audio to ffplay as chunks arrive
- Uses the OpenRouter streaming endpoint (`stream: true` in the TTS request)
- PCM format for lowest latency (~0.8s time-to-first-audio)
- ffplay receives raw PCM via stdin: `ffplay -nodisp -autoexit -f s16le -ar 24000 -ac 1 -i pipe:0`
- On completion, optionally save the full audio to file as well

**New `TtsService` method:**

```csharp
Task StreamAndPlayAsync(string text, string? voice = null, string? instructions = null,
    double? temperature = null, int? seed = null, string? model = null);
```

- Sends TTS request with `stream: true` and `response_format: "pcm"`
- Reads SSE chunks and pipes to ffplay stdin
- Real-time playback begins before the API finishes generating

### 4. Configuration Additions

**`appsettings.json`:**

```json
{
  "Playback": {
    "EnabledByDefault": false
  },
  "OutputProfiles": {
    "default": {
      "Device": "",
      "Volume": 80,
      "IsDefault": true
    }
  }
}
```

- `Playback.EnabledByDefault` — if true, `--play` is implied without needing the flag
- `OutputProfiles` — dictionary of named profiles (same pattern as `Personas`)
- Each profile has `Device` (ALSA device name, empty = system default), `Volume` (0-100), optional `FfplayPath`, and `IsDefault`
- At least one profile should have `IsDefault: true`

**New config classes:**

```csharp
public class PlaybackSettings
{
    public bool EnabledByDefault { get; set; }
}

public class OutputProfile
{
    public string Device { get; set; } = "";
    public int Volume { get; set; } = 80;
    public string? FfplayPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDefault { get; set; }
}
```

### 5. Output Profiles (mirrors persona system)

Output profiles are named configurations for audio playback — they store the output device, volume, and other playback settings. This mirrors the persona system (named config bundles, `IsDefault`, save/list/set-default).

**`OutputProfile` config class:**

```csharp
public class OutputProfile
{
    public string Device { get; set; } = "";
    public int Volume { get; set; } = 80;
    public string? FfplayPath { get; set; }
    public bool IsDefault { get; set; }
}
```

**`appsettings.json`:**

```json
{
  "OutputProfiles": {
    "speakers": {
      "Device": "sysdefault",
      "Volume": 80,
      "IsDefault": true
    },
    "headphones": {
      "Device": "pipewire",
      "Volume": 60
    },
    "hdmi": {
      "Device": "hdmi:CARD=Generic,DEV=0",
      "Volume": 90
    }
  }
}
```

**CLI flags (mirrors persona flags):**

- `--output-profile <name>` — use a named output profile (resolves device, volume, ffplay path)
- `--save-output-profile <name>` — save current output params as a named profile
- `--set-output-default` — mark an output profile as default
- `--list-output-profiles` — list all saved output profiles
- `--list-devices` — discover available audio output devices on this system

**Device discovery (`--list-devices`):**

Uses `aplay -L` on Linux to list named ALSA PCM devices:

```
Available audio output devices:
  sysdefault          Default Audio Device
  pipewire            PipeWire Sound Server
  pulse               PulseAudio Sound Server
  jack                JACK Audio Connection Kit
  null                Discard all samples
  hdmi:CARD=Generic,DEV=0   HD-Audio Generic HDMI 0
  hdmi:CARD=Generic,DEV=1   HD-Audio Generic HDMI 1
  hw:CARD=Generic_1,DEV=0   ALC257 Analog
```

Also uses `aplay -l` to show hardware cards for reference.

**Output profile resolution order:** `--device`/`--volume` CLI overrides → `--output-profile <name>` → output profile with `IsDefault=true` → system defaults (empty device = default audio, volume 80).

**Unity enforcement:** `--set-output-default` clears `IsDefault` from all other output profiles before setting it on the target (same pattern as persona `IsDefault`).

### 6. Playback State Management

- Track playback state (idle, playing, completed)
- Support cancellation: `--play` with Ctrl+C should cleanly stop ffplay
- For batch modes: short delay between files, log which file is playing
- Playback progress logging (optional, controlled by log level)

### 7. VS Code Launch Configurations

Add launch configs:

- **"TTS and play"** — runs with `--play` flag
- **"TTS stream"** — runs with `--stream` flag
- **"TTS all voices and play"** — runs with `--voice all --play`

### 8. Tests

- `AudioPlayerTests`:
  - `PlayAsync_CallsFfplay_WithCorrectArgs` — verify process invocation
  - `PlayAsync_Throws_WhenFfplayNotAvailable` — graceful error
  - `IsAvailable_ReturnsTrue_WhenFfplayOnPath`
  - `PlayStreamAsync_PipesAudioToFfplay` — verify stdin piping
- `PlaybackIntegrationTests`:
  - `SynthesizeAndPlay_WorksEndToEnd` — generates audio, plays it (skip on CI)
- `OutputProfileTests`:
  - `OutputProfile_ResolutionOrder` — CLI overrides → profile → default
  - `SaveOutputProfile_CreatesNewProfile` — writes to appsettings.json
  - `SaveOutputProfile_UpdatesExistingProfile` — overwrites values
  - `SetOutputDefault_EnforcesUnity` — clears IsDefault from others
  - `ListDevices_DisplaysAlsaDevices` — mock aplay output
- Update existing tests for `--play` and `--output-profile` flag parsing

## CLI Summary (after Stage 3)

```
Usage: benow-conversation <text-or-file> [options]

Arguments:
  <text-or-file>                  Text to synthesize, or path to a text file

Options:
  --persona <name>                Use a named persona from config
  --voice <id>                    Override voice ID, or "all" for all voices
  --model <model>                 Override TTS model, or "all" for all providers
  --openai-instructions <text>    Style instructions (warns and skips for non-OpenAI)
  --temperature <0-2>             Sampling temperature
  --seed <int>                    Seed for reproducible output
  --output <file>                 Output filename (default: timestamped)
  --text-file <path>              Read text from file
  --play                          Play audio through speakers after generation
  --stream                        Stream and play audio in real-time (lowest latency)
  --output-profile <name>         Use a named output profile (device + volume)
  --device <device>               Override audio output device (ALSA device name)
  --volume <0-100>                Override playback volume
  --save-output-profile <name>    Save current output params as a named profile
  --set-output-default            Mark output profile as default
  --list-models                   List available TTS models
  --list-voices                   List voices for current/specified model
  --list-personas                 List named personas
  --list-output-profiles          List named output profiles
  --list-devices                  List available audio output devices
  --save-persona <name>           Save current params as a named persona
  --set-default                   Mark persona as default
  --help                          Show this help message
```

## Acceptance Criteria

- [ ] `dotnet build` succeeds with no warnings
- [ ] `dotnet test` passes
- [ ] `--play` flag plays generated audio through speakers
- [ ] `--play` works with `--voice all` (plays all generated files sequentially)
- [ ] `--play` works with `--model all` (plays all generated files)
- [ ] ffplay unavailability produces clear error message
- [ ] Ctrl+C during playback cleanly stops ffplay
- [ ] `--stream` plays audio with ~1s latency
- [ ] `--list-devices` shows available ALSA audio output devices
- [ ] `--output-profile <name>` resolves device + volume from named profile
- [ ] `--save-output-profile <name>` persists to appsettings.json
- [ ] `--set-output-default` enforces IsDefault unity across output profiles
- [ ] `--list-output-profiles` shows all profiles with default marker
- [ ] Output profile resolution: CLI overrides → named profile → IsDefault → system defaults
- [ ] All new flags reflected in `--help`

## Out of Scope

- Voice cloning (reference audio)
- Frontend / UI
- WebSocket-based real-time streaming
- Multiple concurrent playback streams
- Audio recording / microphone input
