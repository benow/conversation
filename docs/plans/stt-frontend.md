# Stage 5: Speech-to-Text Front-End

## Goal

Add an `--stt` mode to benow-conversation that captures audio from the microphone via ffmpeg, transcribes it via Groq Whisper Large V3, cleans up the transcript via OpenRouter LLM, and pastes the result into whatever application currently has focus. Submission to the proxy happens naturally via paste+Enter.

## Pipeline

```
[Ctrl+Space] → [Record audio (ffmpeg)] → [Transcribe (Groq Whisper)] → [Raw transcript]
                                                                         ↓
                                            [Transform (OpenRouter LLM cleanup)]
                                                                         ↓
                                                                [Cleaned text]
                                                                         ↓
                                            [Copy to clipboard] → [Paste + Enter]
                                                                         ↓
                                            [Focused app submits to proxy → TTS response]
```

The cleaned text is pasted into the focused application and Enter is pressed. If the focused app is the proxy UI, this submits the text to the proxy, which generates an AI response and speaks it via TTS. There is no direct API call from SttRunner to the proxy — paste+Enter IS the submission mechanism.

## System Dependencies

See [External Tools](../external-tools.md) for installation, setup, and verification of all external utilities.

### Required

| Tool | Package | Purpose |
|---|---|---|
| `ffmpeg` | `ffmpeg` | Audio recording (`-f pulse`), MP3 encoding, silence detection, beep generation |
| `ffplay` | `ffmpeg` | Beep tone playback (feedback before/after recording) |
| `wl-copy` | `wl-clipboard` | Clipboard write (Wayland) |
| `ydotool` | `ydotool` | Keyboard simulation (Wayland) |

### Setup

- **ydotool**: Requires `ydotoold` running as root. Socket at `/run/user/1000/.ydotool_socket`. See [External Tools](../external-tools.md) for systemd unit file.
- **ffmpeg**: Records via PulseAudio compat layer (`-f pulse -i default`). System ffmpeg (8.0.1) was compiled without native PipeWire support, so the PulseAudio layer is required.
- **evdev access**: The keyboard trigger reads from `/dev/input/eventX` devices. User must be in the `input` group: `sudo usermod -aG input $USER` (requires re-login).

### Optional

- `--stt-setup` mode uses `pw-cli` and `bluetoothctl` for interactive device discovery.

## Abstraction Layer

All platform-specific operations are behind interfaces so the STT pipeline is environment-agnostic. The initial implementation targets Ubuntu/Wayland. Future implementations can add macOS or Windows by implementing these interfaces.

### Interfaces

```csharp
public interface IAudioRecorder
{
    bool IsAvailable { get; }
    Task<string> RecordToFileAsync(string outputPath, CancellationToken ct);
}

public interface ITranscriptionService
{
    Task<string> TranscribeAsync(string audioFilePath, CancellationToken ct = default);
}

public interface ITextTransformer
{
    Task<string> TransformAsync(string input, CancellationToken ct = default);
}

public interface IClipboardService
{
    bool IsAvailable { get; }
    Task CopyAsync(string text, CancellationToken ct = default);
}

public interface IKeyboardSimulator
{
    bool IsAvailable { get; }
    Task PasteAsync(CancellationToken ct = default);
    Task PressEnterAsync(CancellationToken ct = default);
}

public interface IRecordingTrigger
{
    bool IsAvailable { get; }
    Task WaitForTriggerAsync(CancellationToken ct);
}
```

### Linux/Wayland Implementations

| Interface | Implementation | System Tool |
|---|---|---|
| `IAudioRecorder` | `PipeWireRecorder` | `ffmpeg -f pulse` |
| `ITranscriptionService` | `GroqWhisperTranscriber` | HTTP to Groq API |
| `ITextTransformer` | `LlmTextTransformer` | HTTP to OpenRouter |
| `IClipboardService` | `WaylandClipboardService` | `wl-copy` |
| `IKeyboardSimulator` | `YdotoolKeyboardSimulator` | `ydotool` |
| `IRecordingTrigger` | `EvdevKeyboardTrigger` | `/dev/input/eventX` (raw evdev) |
| `IRecordingTrigger` | `EvdevMediaKeyTrigger` | `/dev/input/eventX` (Bluetooth AVRCP) |
| `IRecordingTrigger` | `ConsoleRecordingTrigger` | Console Enter key |

### DI Registration

Config-driven — one implementation registered based on `appsettings.json` selectors:

```csharp
// In RegisterSttServices():
switch (settings.Stt.Recorder)
{
    case "pipewire": services.AddSingleton<IAudioRecorder, PipeWireRecorder>(); break;
}
switch (settings.Stt.Trigger)
{
    case "console": services.AddSingleton<IRecordingTrigger, ConsoleRecordingTrigger>(); break;
    case "evdev-media": services.AddSingleton<IRecordingTrigger, EvdevMediaKeyTrigger>(); break;
    case "evdev-keyboard": services.AddSingleton<IRecordingTrigger, EvdevKeyboardTrigger>(); break;
}
// etc.
services.AddSingleton<ISttRunner, SttRunner>();
```

See `Program.cs:RegisterSttServices()` for full registration.

## Configuration

See [Configuration](../configuration.md) for full settings reference. New sections in `appsettings.json`:

```json
{
  "Stt": {
    "Recorder": "pipewire",
    "RecorderFormat": "mp3",
    "RecorderSampleRate": 16000,
    "RecorderChannels": 1,
    "Trigger": "evdev-keyboard",
    "TriggerKey": "Ctrl+Space",
    "TriggerDebounceMs": 300,
    "AutoSubmit": true,
    "FfmpegPath": "ffmpeg",
    "CleanupSkip": false,
    "YdotoolSocketPath": "/run/user/1000/.ydotool_socket",
    "SilenceTimeoutSeconds": 0,
    "SilenceThresholdDb": -30,
    "FeedbackBeep": true,
    "MaxRecordingSeconds": 60
  },
  "Groq": {
    "ApiKey": "",
    "BaseUrl": "https://api.groq.com/openai/v1",
    "Model": "whisper-large-v3",
    "TranscriptionTimeout": "00:02:00"
  },
  "TranscriptCleanup": {
    "BaseUrl": "",
    "Model": "meta-llama/llama-3.1-8b-instruct",
    "SystemPrompt": "You are a transcription editor..."
  }
}
```

API keys in `appsettings.Development.json` (git-ignored):

```json
{
  "Groq": {
    "ApiKey": "gsk_..."
  }
}
```

## New Files

```
src/benow-conversation/
  Services/
    Abstractions/
      IAudioRecorder.cs             Microphone audio capture
      ITranscriptionService.cs      Audio-to-text transcription
      ITextTransformer.cs           Text cleanup/transformation
      IClipboardService.cs          Clipboard operations
      IKeyboardSimulator.cs         Keyboard simulation
      IRecordingTrigger.cs          Recording start/stop trigger
      ISttRunner.cs                 STT loop orchestrator interface
    Stt/
      SttRunner.cs                  Main STT loop (trigger → record → transcribe → transform → paste → Enter)
      PipeWireRecorder.cs           IAudioRecorder via ffmpeg -f pulse (MP3 output)
      GroqWhisperTranscriber.cs     ITranscriptionService via Groq API
      LlmTextTransformer.cs         ITextTransformer via OpenRouter chat/completions
      WaylandClipboardService.cs    IClipboardService via wl-copy (no --foreground)
      YdotoolKeyboardSimulator.cs   IKeyboardSimulator via ydotool
      EvdevKeyboardTrigger.cs       IRecordingTrigger via raw evdev (Ctrl+Space)
      EvdevMediaKeyTrigger.cs       IRecordingTrigger via Bluetooth AVRCP media keys
      ConsoleRecordingTrigger.cs    IRecordingTrigger via console Enter key
      LinuxInterop.cs               Shared P/Invoke declarations, device discovery, key code lookup
      SttSetup.cs                   Interactive --stt-setup device discovery
  Configuration/
    AppSettings.cs                  SttSettings, GroqSettings, TranscriptCleanupSettings
```

## Component Design

### 1. SttRunner (`ISttRunner`)

Orchestrates the full STT pipeline. Runs in a loop until cancelled.

**Loop flow:**

```
1. Wait for trigger (Ctrl+Space via EvdevKeyboardTrigger)
2. Start recording via IAudioRecorder
   - Play 880Hz beep (fire-and-forget) if FeedbackBeep=true
   - Console: "[STT] Recording... Ctrl+Space to stop."
3. Wait for trigger again (Ctrl+Space to stop)
4. Stop recording (cancel CancellationToken)
   - Play 440Hz beep (awaited) if FeedbackBeep=true
5. Transcribe via ITranscriptionService
6. If !CleanupSkip: transform via ITextTransformer
7. Copy to clipboard via IClipboardService
8. Wait 100ms for clipboard to populate
9. Paste via IKeyboardSimulator.PasteAsync()
10. Wait 100ms for paste to complete
11. If AutoSubmit: press Enter via IKeyboardSimulator.PressEnterAsync()
    - If focused app is the proxy UI, this submits the text
12. Loop to step 1
```

**Error recovery:** Each step is wrapped in try/catch. On failure:
- Log the error with context
- Show error in console
- Clean up temp files in `finally`
- Return to step 1 (never crash the loop)

**Temp file cleanup:** Recorded MP3 files are created in `Path.GetTempPath()` and deleted in a `finally` block after transcription completes.

**Concurrent daemon mode (`--stt --daemon`):** `SttRunner.RunAsync()` runs as a separate `Task` alongside `ProxyService.RunAsync()`. Both are started, then `Task.WhenAny` waits for either to exit, cancels both, then `Task.WhenAll` drains them.

### 2. PipeWireRecorder (`IAudioRecorder`)

Records microphone audio via `ffmpeg -f pulse`. Handles graceful process termination and file finalization.

**Recording command:**
```
ffmpeg -y -f pulse -i default -ac 1 -ar 16000 [-af silencedetect=...] -acodec libmp3lame -b:a 32k "<output>.mp3"
```

Uses `-f pulse` through PipeWire's PulseAudio compat layer (system ffmpeg lacks native pipewire support).

**Process termination (critical — avoids empty MP3):**
1. `CancellationToken` triggers → SIGTERM via `kill -TERM <pid>`
2. Wait up to 5 seconds for ffmpeg to exit (`process.WaitForExit(5000)`)
3. If still running, force kill (`process.Kill(entireProcessTree: true)`)
4. `finally` block calls `EnsureDead()` as safety net
5. Validate output file exists and is non-zero bytes
6. Throw `InvalidOperationException` if file is missing or empty

**Safety timeout:** `MaxRecordingSeconds` (default 60) via linked CancellationTokenSource. After this duration, recording is automatically stopped.

**Orphan cleanup:** `PipeWireRecorder.KillOrphanedProcesses()` is called at startup before DI resolves SttRunner. Kills leftover ffmpeg/pw-record processes from previous crashes. Prevents stuck processes from keeping Bluetooth earbuds in HFP mode.

**Silence detection:** Compiled into ffmpeg args but disabled when `SilenceTimeoutSeconds=0` (current default). When enabled, monitors stderr for `silence_start` lines and sends SIGTERM.

### 3. EvdevKeyboardTrigger (`IRecordingTrigger`)

Monitors all keyboard devices for a configurable hotkey combo.

**How it works:**
1. Parses `/proc/bus/input/devices` to find all devices with "keyboard" in the name
2. Opens each device via `open()` P/Invoke (requires `input` group)
3. Uses `poll()` + `read()` on each fd to read `struct input_event` (24 bytes)
4. Tracks held modifier keys (Ctrl, Alt, Shift, Super)
5. When the trigger key is pressed AND all required modifiers are held, fires the trigger
6. Debounce via `TriggerDebounceMs` (default 300ms)

**Key parsing:** `ParseKeySpec("Ctrl+Space")` → modifiers={29 (LeftCtrl)}, triggerKey=57 (Space). Supports combos like `Ctrl+Alt+Space`, `F9`, etc. Single keys without modifiers get Ctrl added by default.

**Excluded devices:** "ydotoold" and "Controller" are filtered out to avoid feedback loops and game controller interference.

**Channel-based signaling:** Uses a bounded `Channel<bool>` (capacity 1, DropOldest) so triggers fire reliably even if the consumer isn't waiting.

### 4. GroqWhisperTranscriber (`ITranscriptionService`)

Transcribes audio via Groq Whisper Large V3 API.

- Uses named `HttpClient` registered as `"Groq"` in DI with `Authorization: Bearer` header
- POSTs the MP3 file as multipart/form-data to `POST {Groq.BaseUrl}/audio/transcriptions`
- Content-Type for file part: `audio/mpeg`
- Form fields: `model=whisper-large-v3`, `file=<audio>`, `response_format=json`, `language=en`
- Timeout: `Groq.TranscriptionTimeout` (default 2 minutes)
- Throws `InvalidOperationException` on HTTP 400 or other errors

**Groq Whisper constraints:**
- File size: 25 MB (free tier) / 100 MB (dev tier)
- At 16kHz mono MP3 32kbps (~4 KB/sec), that's ~100 min / ~400 min
- Rate limits: 20 RPM, 2K RPD, 7,200 audio-seconds/hour
- Supported formats: flac, mp3, mp4, mpeg, mpga, m4a, ogg, wav, webm

### 5. LlmTextTransformer (`ITextTransformer`)

Cleans up raw STT output using an LLM.

- Resolves endpoint: `TranscriptCleanup.BaseUrl` if non-empty, otherwise `OpenRouter.BaseUrl`
- Uses existing `"OpenRouter"` named `HttpClient`
- POST to `{baseUrl}/chat/completions`
- Model: `TranscriptCleanup.Model` (default: `meta-llama/llama-3.1-8b-instruct`)
- System prompt instructs: remove fillers, fix punctuation, preserve meaning exactly, do not censor
- Timeout: 30 seconds

### 6. WaylandClipboardService (`IClipboardService`)

Clipboard operations via `wl-copy`.

- Uses `Process` with `RedirectStandardInput = true`
- Writes text to process stdin (avoids shell injection)
- Does NOT use `--foreground` flag — `wl-copy` forks to background and returns immediately
- 100ms delay in SttRunner between copy and paste ensures clipboard is populated
- `IsAvailable`: Checks `which wl-copy` at startup

### 7. YdotoolKeyboardSimulator (`IKeyboardSimulator`)

Keyboard simulation via `ydotool` on Wayland.

- Socket path set explicitly on each `ProcessStartInfo.Environment`: `/run/user/1000/.ydotool_socket`
- Does NOT rely on shell environment variable
- Paste (Ctrl+V): `ydotool key 29:1 47:1 47:0 29:0`
- Enter: `ydotool key 28:1 28:0`
- `IsAvailable`: Checks `which ydotool` at startup

## CLI Usage

```sh
# Start STT mode (keyboard trigger)
dotnet run --project src/benow-conversation -- --stt

# STT + daemon mode (runs both STT listener and proxy)
dotnet run --project src/benow-conversation -- --stt --daemon

# Skip transcript cleanup
dotnet run --project src/benow-conversation -- --stt --no-cleanup

# Override cleanup model
dotnet run --project src/benow-conversation -- --stt --cleanup-model meta-llama/llama-3.1-8b-instruct

# Interactive setup (detect keyboard devices, test key codes)
dotnet run --project src/benow-conversation -- --stt-setup
```

## CLI Flags

| Flag | Description |
|---|---|
| `--stt` | Start STT mode (keyboard trigger) |
| `--stt --daemon` | STT + daemon mode (both run concurrently) |
| `--stt-setup` | Interactive device discovery and configuration |
| `--no-cleanup` | Skip transcript cleanup step |
| `--cleanup-model <model>` | Override cleanup LLM model |

## Trigger Architecture

The current default trigger is `evdev-keyboard` — reads directly from `/dev/input/eventX` devices for global hotkey detection without any Wayland compositor cooperation.

### Available Triggers

| Selector | Implementation | Description |
|---|---|---|
| `evdev-keyboard` | `EvdevKeyboardTrigger` | Monitors all keyboard devices for configurable hotkey (default: Ctrl+Space). Requires `input` group. |
| `evdev-media` | `EvdevMediaKeyTrigger` | Monitors Bluetooth AVRCP media keys (play/pause, next, etc.). Only works when earbuds are in A2DP profile. |
| `console` | `ConsoleRecordingTrigger` | Uses Enter key in terminal. Simplest, no permissions. |

### Why evdev-keyboard

Bluetooth A2DP/HFP profile conflict made earbud pinch trigger fundamentally unreliable:
- A2DP provides AVRCP (media keys) but no mic
- HFP provides mic but no AVRCP
- OnePlus Buds Pro 2 only exposes `a2dp-sink`, `headset-head-unit-cvsd`, `headset-head-unit` profiles

The keyboard trigger works regardless of Bluetooth state, monitors all keyboard devices (AT laptop, i4 BT, Legion), and has zero Bluetooth dependency.

### Why toggle over silence detection

Ctrl+Space starts recording, Ctrl+Space stops it. User controls exactly when recording begins and ends. Silence detection was too finicky with background noise levels.

## Beep Feedback

When `FeedbackBeep=true` (default):
- **Start beep**: 880Hz, 0.15s duration — fire-and-forget (doesn't block recording start)
- **Stop beep**: 440Hz, 0.2s duration — awaited (plays after recording stops)

Beeps are generated by ffmpeg (`sine` lavfi source) and played by `ffplay`. Generated once and cached as MP3 files in `/tmp/stt_beep_*.mp3`.

Known issue: beep playback uses default audio sink, which may be earbuds in HFP mode (inaudible). The start beep is fire-and-forget so this doesn't block.

## Recording Flow

```
1. Ctrl+Space → trigger fires
2. SttRunner starts PipeWireRecorder with CancellationToken
3. ffmpeg spawns: ffmpeg -y -f pulse -i default -ac 1 -ar 16000 -acodec libmp3lame -b:a 32k "/tmp/stt_<guid>.mp3"
4. Safety timeout: MaxRecordingSeconds=60 → linked CancellationTokenSource
5. Ctrl+Space → trigger fires → CancellationToken cancelled
6. SIGTERM sent to ffmpeg
7. Wait up to 5s for ffmpeg to finalize MP3 (flush + close container)
8. Validate: file exists, non-zero bytes
9. Return path to SttRunner
```

## Submission Flow

```
1. SttRunner receives transcribed + cleaned text
2. Copy text to clipboard via wl-copy (no --foreground, returns immediately)
3. Wait 100ms for clipboard to populate
4. Simulate Ctrl+V via ydotool (paste)
5. Wait 100ms for paste to complete
6. Simulate Enter via ydotool (submit)
7. Focused application receives the text submission
```

If the focused app is the proxy UI, this triggers the full AI → TTS pipeline automatically. No direct API call from SttRunner.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Wayland restricts global hotkeys | Use evdev device monitoring (requires `input` group) |
| ydotool needs daemon running | `IsAvailable` check at startup, warn if not running |
| ffmpeg killed before MP3 finalization | Wait up to 5s after SIGTERM, validate non-zero output, throw on empty |
| Orphan ffmpeg processes from crashes | `KillOrphanedProcesses()` at startup kills leftover processes |
| Groq rate limits on Whisper | 20 RPM, 7.2K audio-sec/hr; catch 429 errors, show message |
| Cleanup model may alter meaning | System prompt forbids paraphrasing/censoring; `--no-cleanup` skips step |
| Shell injection via clipboard text | Pipe text to `wl-copy` stdin, never construct shell strings |
| wl-copy forks to background | 100ms delay between copy and paste; no `--foreground` flag (was blocking) |
| `YDOTOOL_SOCKET` not in environment | Set explicitly on each `ProcessStartInfo.Environment` |
| Recording too long (disk/API limits) | `MaxRecordingSeconds=60` safety timeout |
| Transient API errors crash loop | Each step wrapped in try/catch, errors logged, loop continues |
| Beep inaudible through earbuds | Start beep is fire-and-forget; stop beep is informational only |
| Keyboard device permissions | Check `IsAvailable` at startup, log clear error about `input` group |

## Future Enhancements

- **whisper-large-v3-turbo**: Configurable via `Groq.Model` — cheaper and faster but slightly higher WER
- **Streaming transcription**: Use Groq's streaming endpoint for real-time partial results
- **Visual indicator**: Show recording state (tray icon, OSD notification via `notify-send`)
- **Multiple language support**: Configure Whisper language parameter
- **macOS support**: `CoreAudioRecorder`, `PbcopyClipboardService`, `AppleScriptKeyboardSimulator`
- **X11 support**: `XdotoolKeyboardSimulator`, `XclipClipboardService`
- **Windows support**: `NAudioRecorder`, `Win32ClipboardService`, `SendInputKeyboardSimulator`
- **Explicit audio source selection**: Choose between laptop mic and earbud mic
