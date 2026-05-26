# Stage 5: Speech-to-Text Front-End

## Goal

Add an `--stt` mode to benow-conversation that captures audio from the microphone, transcribes it via Groq Whisper Large V3, cleans up the transcript via OpenRouter, and pastes the result into whatever application currently has focus.

## Pipeline

```
[Keystroke] → [Record audio] → [Transcribe (Groq Whisper)] → [Raw transcript]
                                                                   ↓
                                          [Transform (OpenRouter LLM cleanup)]
                                                                   ↓
                                                          [Cleaned text]
                                                                   ↓
                                          [Copy to clipboard] → [Paste + Enter]
                                                                   ↓
                                          [Target application receives text]
                                                                   ↓
                                          [Submit to local proxy for TTS response]
```

The cleaned text is also submitted to the OpenAI-compatible proxy (`localhost:8080`) if the daemon is running, so the response gets spoken back via TTS.

## System Dependencies

See [External Tools](../external-tools.md) for installation, setup, and verification of all external utilities.

### Required

| Tool | Package | Purpose |
|---|---|---|
| `ffmpeg` | `ffmpeg` | WAV header repair, audio format conversion |
| `pw-record` | `pipewire-bin` | Microphone audio capture (Linux/Wayland) |
| `wl-copy` | `wl-clipboard` | Clipboard write (Wayland) |
| `ydotool` | `ydotool` | Keyboard simulation (Wayland) |

### Setup

`ydotool` requires `ydotoold` running as root and `YDOTOOL_SOCKET=/tmp/.ydotool_socket` set in the environment. See [External Tools](../external-tools.md) for systemd unit file and socket configuration.

`ffmpeg` is used to repair WAV headers when `pw-record` is terminated mid-stream (SIGTERM may not finalize the RIFF size). If the recorded WAV has an incorrect header, ffmpeg rewrites it:

```
ffmpeg -y -i <recording.wav> -c:a pcm_s16le -ar 16000 -ac 1 <repaired.wav>
```

## Abstraction Layer

All platform-specific operations are behind interfaces so the STT pipeline is environment-agnostic. The initial implementation targets Ubuntu/Wayland. Future implementations can add macOS (CoreAudio, pbcopy, AppleScript) or Windows (NAudio, Win32 clipboard, SendInput) by implementing these interfaces.

### Interfaces

```csharp
public interface IAudioRecorder
{
    bool IsAvailable { get; }
    Task<string> RecordToFileAsync(string outputPath, CancellationToken ct);
    // Returns path to recorded WAV file. The caller is responsible for cleanup.
    // Implementations must handle graceful termination (SIGTERM) to finalize output.
}

public interface ITranscriptionService
{
    Task<string> TranscribeAsync(string audioFilePath, CancellationToken ct = default);
    // Returns raw transcript text.
}

public interface ITextTransformer
{
    Task<string> TransformAsync(string input, CancellationToken ct = default);
    // Returns transformed text. For cleanup: removes fillers, fixes punctuation.
    // Can be skipped by caller or replaced with a no-op implementation.
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
```

### Linux/Wayland Implementations

| Interface | Implementation | System Tool |
|---|---|---|
| `IAudioRecorder` | `PipeWireRecorder` | `pw-record`, `ffmpeg` |
| `ITranscriptionService` | `GroqWhisperTranscriber` | HTTP to Groq API |
| `ITextTransformer` | `LlmTextTransformer` | HTTP to OpenRouter |
| `IClipboardService` | `WaylandClipboardService` | `wl-copy` |
| `IKeyboardSimulator` | `YdotoolKeyboardSimulator` | `ydotool` |

### DI Registration

```csharp
services.AddSingleton<IAudioRecorder, PipeWireRecorder>();
services.AddSingleton<ITranscriptionService, GroqWhisperTranscriber>();
services.AddSingleton<ITextTransformer, LlmTextTransformer>();
services.AddSingleton<IClipboardService, WaylandClipboardService>();
services.AddSingleton<IKeyboardSimulator, YdotoolKeyboardSimulator>();
services.AddSingleton<ISttRunner, SttRunner>();
```

Future: select implementations via config or runtime platform detection (e.g., `IKeyboardSimulator` → `XdotoolKeyboardSimulator` on X11, `AppleScriptKeyboardSimulator` on macOS).

## Configuration

New sections in `appsettings.json`:

```json
{
  "Stt": {
    "Recorder": "pipewire",
    "RecorderCommand": "pw-record",
    "RecorderFormat": "wav",
    "RecorderSampleRate": 16000,
    "RecorderChannels": 1,
    "TriggerKey": "F9",
    "AutoSubmit": true,
    "FfmpegPath": "ffmpeg",
    "CleanupSkip": false
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
    "SystemPrompt": "You are a professional editor..."
  }
}
```

- `TranscriptCleanup.BaseUrl`: empty means use `OpenRouter.BaseUrl` (the default). Set explicitly to use a different endpoint.
- `Stt.Recorder`: selector for which `IAudioRecorder` implementation to use (`pipewire`, future: `alsa`, `coreaudio`, etc.).
- `Stt.CleanupSkip`: equivalent of `--no-cleanup` in config form.
- `Stt.FfmpegPath`: path to ffmpeg binary. Empty/absent = use system PATH.
- `Groq.TranscriptionTimeout`: timeout for transcription API calls (default 2 minutes for long recordings).

API keys in `appsettings.Development.json`:

```json
{
  "Groq": {
    "ApiKey": "gsk_..."
  }
}
```

### Settings Classes

```csharp
public class SttSettings
{
    public string Recorder { get; set; } = "pipewire";
    public string RecorderCommand { get; set; } = "pw-record";
    public string RecorderFormat { get; set; } = "wav";
    public int RecorderSampleRate { get; set; } = 16000;
    public int RecorderChannels { get; set; } = 1;
    public string TriggerKey { get; set; } = "F9";
    public bool AutoSubmit { get; set; } = true;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public bool CleanupSkip { get; set; }
}

public class GroqSettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public string Model { get; set; } = "whisper-large-v3";
    public TimeSpan TranscriptionTimeout { get; set; } = TimeSpan.FromMinutes(2);
}

public class TranscriptCleanupSettings
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "meta-llama/llama-3.1-8b-instruct";
    public string SystemPrompt { get; set; } = "You are a professional editor...";
}
```

Extend `AppSettings`:

```csharp
public class AppSettings
{
    // ... existing properties ...
    public SttSettings Stt { get; set; } = new();
    public GroqSettings Groq { get; set; } = new();
    public TranscriptCleanupSettings TranscriptCleanup { get; set; } = new();
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
    Stt/
      ISttRunner.cs                 STT loop orchestrator interface
      SttRunner.cs                  Main STT loop (record → transcribe → transform → paste → submit)
      PipeWireRecorder.cs           IAudioRecorder via pw-record + ffmpeg repair
      GroqWhisperTranscriber.cs     ITranscriptionService via Groq API
      LlmTextTransformer.cs         ITextTransformer via OpenRouter chat/completions
      WaylandClipboardService.cs    IClipboardService via wl-copy
      YdotoolKeyboardSimulator.cs   IKeyboardSimulator via ydotool
  Configuration/
    AppSettings.cs                  (extend with SttSettings, GroqSettings, TranscriptCleanupSettings)
```

Separating interfaces into `Abstractions/` and implementations into `Stt/` keeps the existing `Services/` structure clean and makes it obvious where to add new platform implementations (e.g., `Services/MacOs/` or `Services/Windows/`).

## Component Design

### 1. SttRunner (`ISttRunner`)

Orchestrates the full STT pipeline. Extracted from `Program.cs` to avoid bloating the entry point (already 800+ lines). Follows the same pattern as `ProxyService.RunAsync()`.

```csharp
public interface ISttRunner
{
    Task RunAsync(CancellationToken cancellationToken);
}
```

**Loop flow:**

```
1. Wait for trigger (Enter key in console)
2. Start recording via IAudioRecorder
   Console.WriteLine("Recording... press Enter to stop.")
3. Wait for trigger again (Enter key to stop)
4. Stop recording (cancellation)
5. Transcribe via ITranscriptionService
   Console.WriteLine("Transcribing...")
6. If !Stt.CleanupSkip: transform via ITextTransformer
   Console.WriteLine("Cleaning up...")
7. Copy to clipboard via IClipboardService
8. Paste via IKeyboardSimulator.PasteAsync()
   Brief delay (100ms) to ensure clipboard is settled
9. If Stt.AutoSubmit: simulate Enter via IKeyboardSimulator.PressEnterAsync()
10. If proxy is reachable: submit cleaned text for TTS response
11. Wait for TTS completion (if submitted) before re-enabling recording
    This prevents the next recording from capturing TTS output
12. Loop to step 1
```

**Error recovery:** Each step is wrapped in try/catch. On failure:
- Log the error with context (which step failed)
- Show error in console
- Clean up temp files
- Return to step 1 (waiting for next trigger)
- Never crash the loop on transient errors

**Temp file cleanup:** Recorded WAV files are created in `Path.GetTempPath()` and deleted in a `finally` block after transcription completes.

**Concurrent daemon mode (`--stt --daemon`):** `SttRunner.RunAsync()` runs as a separate `Task` alongside `ProxyService.RunAsync()`. The proxy submission in step 10 POSTs to `localhost:{Proxy.Port}`, which is the same-process HTTP server. On startup, `SttRunner` polls `http://localhost:{Proxy.Port}/v1/models` with a 10-second timeout to wait for the daemon to be ready before entering the loop.

### 2. PipeWireRecorder (`IAudioRecorder`)

Records microphone audio via `pw-record`. Handles graceful process termination and WAV header repair.

**Recording:**
- Spawns `pw-record --format s16 --rate 16000 --channels 1 <tempfile>.wav`
- Returns the temp file path
- The caller passes a `CancellationToken` — when cancelled, the recorder stops the process

**Process termination (critical — avoids truncated WAV):**
1. Send SIGTERM via `Process.Kill(false)` is not available in .NET; use `kill -TERM <pid>` via `Process.Start("kill", $"-TERM {process.Id}")`
2. Wait up to 2 seconds for exit via `process.WaitForExit(2000)`
3. If still running, send SIGKILL via `process.Kill(true)` (entire process tree)
4. Check if the output file exists and has content
5. Validate WAV header: read first 4 bytes for "RIFF" and check file size matches the RIFF size field
6. If header is invalid, repair with ffmpeg: `ffmpeg -y -i <broken.wav> -c:a pcm_s16le -ar 16000 -ac 1 <repaired.wav>`
7. Return repaired file path (original deleted)

**ffmpeg dependency:** ffmpeg must be available. `IsAvailable` checks for ffmpeg at startup. If not available and the WAV header needs repair, throws with a clear error message.

### 3. GroqWhisperTranscriber (`ITranscriptionService`)

Transcribes audio via Groq Whisper Large V3 API.

**Implementation:**
- Uses a named `HttpClient` registered as `"Groq"` in DI
- POSTs the WAV file as multipart/form-data to `POST {Groq.BaseUrl}/audio/transcriptions`
- Headers: `Authorization: Bearer {Groq.ApiKey}`
- Form fields: `model=<Groq.Model>`, `file=<audio>`, `response_format=json`, `language=en`
- Timeout: `Groq.TranscriptionTimeout` (default 2 minutes)
- Returns `text` field from JSON response

**Groq Whisper constraints (verified 2026-05):**
- File size: 25 MB (free tier) / 100 MB (dev tier). At 16kHz s16 mono WAV (~32 KB/sec), that's ~13 min / ~52 min.
- Minimum billed: 10 seconds per request (short utterances still incur 10s billing).
- Rate limits (whisper-large-v3): 20 RPM, 2K RPD, 7,200 audio-seconds/hour, 28,800 audio-seconds/day.
- Supported formats: `flac, mp3, mp4, mpeg, mpga, m4a, ogg, wav, webm`.
- Available models: `whisper-large-v3` ($0.111/hr) and `whisper-large-v3-turbo` ($0.04/hr, slightly higher WER).
- The `prompt` parameter (max 224 tokens) can provide context/vocabulary hints for better accuracy.
- Audio is downsampled to 16kHz mono internally regardless of input format.

**Groq Whisper request format:**
```
POST https://api.groq.com/openai/v1/audio/transcriptions
Content-Type: multipart/form-data
Authorization: Bearer gsk_...

--boundary
Content-Disposition: form-data; name="file"; filename="recording.wav"
Content-Type: audio/wav
<audio bytes>
--boundary
Content-Disposition: form-data; name="model"
whisper-large-v3
--boundary
Content-Disposition: form-data; name="response_format"
json
--boundary
Content-Disposition: form-data; name="language"
en
--boundary--
```

### 4. LlmTextTransformer (`ITextTransformer`)

Cleans up raw STT output using an LLM.

**Implementation:**
- Resolves endpoint: `TranscriptCleanup.BaseUrl` if non-empty, otherwise `OpenRouter.BaseUrl`
- Uses existing `"OpenRouter"` named `HttpClient`
- POST to `{baseUrl}/chat/completions`
- Authorization: reuses `OpenRouter.ApiKey` (or could be extended for separate key)
- Model: `TranscriptCleanup.Model`
- System prompt: `TranscriptCleanup.SystemPrompt`
- User message: the raw transcript
- Returns `choices[0].message.content`
- Timeout: 30 seconds (cleanup should be fast)

**Default system prompt:**
```
You are a professional editor. Your task is to clean up the raw transcript provided
below, making it highly readable while preserving the exact meaning, tone, and
authenticity of the speaker.

Guidelines for editing:
- Remove fillers & stutters (uh, um, like, you know, I mean)
- Remove repeated words caused by stutters or false starts
- Fix transcription errors
- Add proper capitalization and punctuation
- DO NOT paraphrase, summarize, or alter the subject matter
- Keep editorial changes strictly limited to formatting and clarity
- Output only the cleaned transcript, no preamble
```

### 5. WaylandClipboardService (`IClipboardService`)

Clipboard operations via `wl-copy`.

**Implementation:**
- Uses `Process` with `RedirectStandardInput = true` (NOT shell string construction)
- Writes text to process stdin to avoid shell injection
- `wl-copy` reads from stdin when no text arguments are given
- **Must use `--foreground` (`-f`) flag** — by default, `wl-copy` forks to background and serves clipboard data asynchronously. Without `--foreground`, the paste may occur before the clipboard is populated.
- No trailing newline (text is written as-is via stdin)

```csharp
var psi = new ProcessStartInfo
{
    FileName = "wl-copy",
    Arguments = "--foreground",
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true
};
using var process = Process.Start(psi);
await process.StandardInput.WriteAsync(text);
process.StandardInput.Close();
await process.WaitForExitAsync(ct);
```

**`IsAvailable`:** Checks `which wl-copy` at startup.

### 6. YdotoolKeyboardSimulator (`IKeyboardSimulator`)

Keyboard simulation via `ydotool` on Wayland.

**Implementation:**
- Sets `YDOTOOL_SOCKET` env var on the process if configured in `Stt.YdotoolSocketPath` (default: `/tmp/.ydotool_socket`)
- Does NOT rely on the shell environment — sets it explicitly on each `ProcessStartInfo`
- Linux input event key codes (from `<linux/input-event-codes.h>`):
  - `KEY_ENTER` = 28
  - `KEY_LEFTCTRL` = 29
  - `KEY_V` = 47

**Paste (Ctrl+V):**
```
YDOTOOL_SOCKET=/tmp/.ydotool_socket ydotool key 29:1 47:1 47:0 29:0
```

**Enter:**
```
YDOTOOL_SOCKET=/tmp/.ydotool_socket ydotool key 28:1 28:0
```

**`IsAvailable`:** Checks `which ydotool` AND tests socket connectivity at startup. Logs warning if `ydotoold` is not running.

## Integration in Program.cs

### New CLI Flags

| Flag | Description |
|---|---|
| `--stt` | Start STT mode (console toggle) |
| `--stt --daemon` | STT + daemon mode (both run concurrently) |
| `--cleanup-model <model>` | Override cleanup LLM model |
| `--no-cleanup` | Skip transcript cleanup step |

### DI Changes

Add to the existing `ConfigureServices` block:

```csharp
services.AddSingleton<IAudioRecorder, PipeWireRecorder>();
services.AddSingleton<ITranscriptionService, GroqWhisperTranscriber>();
services.AddSingleton<ITextTransformer, LlmTextTransformer>();
services.AddSingleton<IClipboardService, WaylandClipboardService>();
services.AddSingleton<IKeyboardSimulator, YdotoolKeyboardSimulator>();
services.AddSingleton<ISttRunner, SttRunner>();

services.AddHttpClient("Groq", client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});
```

### Mode Dispatch

Add before the existing `daemonMode` branch in `Program.cs`:

```
if (sttMode && daemonMode)
{
    // Run both concurrently
    var proxyService = host.Services.GetRequiredService<IProxyService>();
    var speechQueue = host.Services.GetRequiredService<ISpeechQueue>();
    var sttRunner = host.Services.GetRequiredService<ISttRunner>();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    await speechQueue.StartAsync(cts.Token);

    var proxyTask = proxyService.RunAsync(cts.Token);
    var sttTask = sttRunner.RunAsync(cts.Token);

    await Task.WhenAny(proxyTask, sttTask);
    cts.Cancel();
    await Task.WhenAll(proxyTask, sttTask);

    await speechQueue.StopAsync(CancellationToken.None);
}
else if (sttMode)
{
    var sttRunner = host.Services.GetRequiredService<ISttRunner>();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await sttRunner.RunAsync(cts.Token);
}
```

## Proxy Submission

After pasting, the cleaned text is also submitted to the local proxy daemon:

```
POST http://localhost:{Proxy.Port}/v1/chat/completions
{
  "model": "<Proxy.BackendModel>",
  "messages": [{"role": "user", "content": "<cleaned text>"}],
  "stream": false
}
```

This triggers the proxy's TTS pipeline: the response is spoken aloud. `stream: false` is used because we don't need to relay the response back — we just want the side effect of TTS playback via SpeechQueue.

**Important:** `SttRunner` waits for this HTTP response before looping back to step 1. This ensures TTS playback completes (or at least begins with a reasonable delay) before the microphone is re-enabled, preventing the next recording from capturing TTS audio through the speakers.

## CLI Usage

```sh
# Start STT mode (console toggle)
dotnet run --project src/benow-conversation -- --stt

# STT + daemon mode (runs both STT listener and proxy)
dotnet run --project src/benow-conversation -- --stt --daemon

# Skip transcript cleanup
dotnet run --project src/benow-conversation -- --stt --no-cleanup

# Override cleanup model
dotnet run --project src/benow-conversation -- --stt --cleanup-model meta-llama/llama-3.1-8b-instruct
```

## Dependencies to Install

See [External Tools](../external-tools.md) for full installation instructions.

```sh
# Audio recording + WAV repair
sudo apt install ffmpeg pipewire-bin

# Wayland clipboard
sudo apt install wl-clipboard

# Wayland keyboard simulation
sudo apt install ydotool
sudo systemctl enable --now ydotoold
```

No additional NuGet packages needed — all operations are handled via process invocation and standard `HttpClient`.

## Trigger Key Detection

On Wayland, global hotkey detection is restricted. Options:

1. **evdev device monitoring** — read directly from `/dev/input/eventX` (requires `input` group). This is what ydotool uses internally.
2. **D-Bus / portal** — `org.freedesktop.portal.GlobalShortcuts` via XDG desktop portal. More correct but complex.
3. **Console toggle** — Since `--stt` runs in a terminal, use Enter key in that terminal to start/stop recording. Simplest, no permissions.

**Initial implementation: option 3** (console-based toggle). The user presses Enter in the terminal to start recording, presses Enter again to stop. This avoids all Wayland permission complexities.

Console UX during the loop:
```
[STT] Press Enter to start recording...
[STT] Recording... press Enter to stop.
[STT] Transcribing...
[STT] Cleaning up...
[STT] Pasted. Waiting for TTS response...
[STT] Done. Press Enter to start recording...
```

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Wayland restricts global hotkeys | Use console-based toggle (Enter key in terminal) |
| ydotool needs daemon running | Check availability at startup (`IsAvailable`), warn if not running |
| wl-copy may not work in all compositors | `IsAvailable` check; log clear error; future: `xclip` fallback for XWayland |
| Groq rate limits on Whisper | 20 RPM, 7.2K audio-sec/hr; catch 429 errors, show message, wait and retry or skip to next trigger |
| Cleanup model may alter meaning | System prompt forbids paraphrasing; `--no-cleanup` flag skips step entirely |
| Long recordings may hit Groq file size limit | 25 MB free / 100 MB dev ≈ 13/52 min at 16kHz s16 mono; warn if recording exceeds tier limit |
| pw-record SIGKILL truncates WAV | SIGTERM first, then ffmpeg header repair as fallback |
| Shell injection via clipboard text | Pipe text to `wl-copy` stdin, never construct shell strings |
| TTS output captured by next recording | Wait for proxy response before re-enabling recording |
| Concurrent `--stt --daemon` startup race | `SttRunner` polls proxy health endpoint with 10s timeout |
| Transient API errors crash loop | Each step wrapped in try/catch, errors logged, loop continues |
| `YDOTOOL_SOCKET` not in environment | Set explicitly on each `ProcessStartInfo.Environment` |
| wl-copy forks to background by default | Use `--foreground` flag to ensure synchronous clipboard write before paste |
| Short utterances billed as 10 sec minimum | Acceptable for STT use case; document in config for awareness |

## Future Enhancements

- **Push-to-talk**: Hold a key to record, release to stop (requires evdev or portal access)
- **whisper-large-v3-turbo**: Configurable via `Groq.Model` — cheaper ($0.04/hr vs $0.111/hr) and faster but slightly higher WER (12% vs 10.3%)
- **Streaming transcription**: Use Groq's streaming endpoint for real-time partial results
- **Visual indicator**: Show recording state (e.g. tray icon, OSD notification via `notify-send`)
- **Voice activity detection**: Auto-stop recording when silence is detected (avoid manual stop)
- **Multiple language support**: Configure Whisper language parameter
- **macOS support**: `CoreAudioRecorder`, `PbcopyClipboardService`, `AppleScriptKeyboardSimulator`
- **X11 support**: `XdotoolKeyboardSimulator`, `XclipClipboardService`
- **Windows support**: `NAudioRecorder`, `Win32ClipboardService`, `SendInputKeyboardSimulator`
