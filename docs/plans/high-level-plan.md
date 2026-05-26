# Conversation - High Level Plan

## Goal

Real-time voice conversation between human and AI. Speak in, get a spoken response back. Low-reach, high-value increments that compose into the final system.

## Pipeline Overview

```
[Microphone] -> [STT] -> [Text Cleanup] -> [LLM] -> [TTS] -> [Speaker]
```

### Stage 1: Voice-to-Text (Input) - *Already working via Whispering*

- Whispering app captures microphone audio
- Transcription via API (Whisper, possibly through Groq)
- Raw transcript cleaned up via DeepSeek v3
- Output: clean text
- Status: functional, not the immediate focus

### Stage 2: LLM Processing - *Already working via Kilo*

- Clean text submitted to model (currently GLM-5-Turbo via Kilo)
- Model generates response text
- Status: functional

### Stage 3: Text-to-Speech (Output) - *Primary focus*

- Response text converted to speech via TTS API
- Zero-shot voice cloning from a 3-second audio sample
- Audio playback to user
- Status: not yet implemented

### Stage 4: Integration / Unified Application - *Future*

- Proxy service implementing OpenAI-compatible API spec
- Pass-through and interception of text between stages
- Allows Whispering/Kilo to talk through a single interface
- Replaces ad-hoc toolchain with a cohesive app

## TTS: Mistral Voxtral Mini TTS via OpenRouter

**Model**: `mistralai/voxtral-mini-tts-2603`
**Cost**: $16 per 1M characters
**Endpoint**: `POST https://openrouter.ai/api/v1/audio/speech` (OpenAI-compatible)

### Key Features

- Zero-shot voice cloning from as little as 2-3 seconds of audio
- "Voice-as-instruction" approach: captures emotion, speaking style, and accent from the audio prompt
- Multilingual: English, French, Spanish, Portuguese, Italian, Dutch, German, Hindi, Arabic
- Cross-lingual cloning (e.g., French voice sample with English text = French-accented English)
- Streaming support via SSE (`stream: true`)
- PCM format: ~0.8s time-to-first-audio (lowest latency)
- MP3 format: ~3s due to encoder overhead

### Voice Cloning - Two Methods

**Method 1: Inline (ref_audio)** - Pass base64-encoded audio sample directly in each request. Good for prototyping.

```json
{
  "model": "mistralai/voxtral-mini-tts-2603",
  "input": "Hello! This is a cloned voice speaking.",
  "ref_audio": "<base64-encoded-audio-reference>",
  "response_format": "mp3"
}
```

**Method 2: Saved Voice (voice_id)** - Create a reusable voice profile via Mistral API, then reference by ID. Better for production.

Step 1 - Create voice: `POST https://api.mistral.ai/v1/audio/voices`
```json
{
  "name": "andy",
  "sample_audio": "<base64-encoded-audio-file>",
  "sample_filename": "andy-sample.mp3",
  "languages": ["en"]
}
```

Step 2 - Use voice:
```json
{
  "model": "mistralai/voxtral-mini-tts-2603",
  "input": "Hello! This is Andy's cloned voice.",
  "voice_id": "voice-abc123",
  "response_format": "mp3"
}
```

### Voice ID Hosting & Implications

Voice profiles created via Method 2 are **hosted on Mistral's servers**:

- The `voice_id` references a server-side stored profile on Mistral's infrastructure
- **No additional storage cost** — you only pay per character for TTS generation ($16/1M chars)
- **Retention**: the API includes a `retention_notice` field (default: `30`, likely days). Voices may be purged after the retention period if not used
- **Full CRUD lifecycle**: create (`POST`), list (`GET`), get details (`GET /{id}`), update metadata (`PATCH /{id}`), delete (`DELETE /{id}`), retrieve sample audio (`GET /{id}/sample`)
- **Requires a separate Mistral API key** (not OpenRouter) — the voice management endpoints are on `api.mistral.ai`, not `openrouter.ai`
- **Implication**: for voice cloning via OpenRouter, use Method 1 (inline `ref_audio`). For saved voices via Method 2, you need direct Mistral API access

### Best Practices for Voxtral

- Keep text under 300 words per request for best results
- Voice sample language should match output language for best quality
- Verbalize numbers and symbols: "one thousand two hundred" not "1200"
- Avoid markdown, emojis, special characters in input text
- Spell out abbreviations: "F-B-I" not "FBI"
- Use PCM format for real-time streaming (lowest latency)

## Architecture

```
                    OpenAI-compatible
                    API spec (proxy)
                        |
            +-----------+-----------+
            |                       |
        [STT Service]          [TTS Service]
            |                       |
     Whispering (existing)    Voxtral Mini TTS
            |                   (OpenRouter)
     DeepSeek cleanup              |
            |                  Voice clone
            |                  (ref_audio or
            |                   saved voice_id)
            +------- [LLM] --------+
                 GLM-5-Turbo
                (via Kilo or proxy)
```

## Tech Stack

- **Language**: C# / .NET 8
- **App structure**: .NET console app with `HostBuilder`, dependency injection, `appsettings.json` / `appsettings.Development.json`
- **TTS**: OpenRouter `/api/v1/audio/speech` with `mistralai/voxtral-mini-tts-2603`
- **HTTP client**: `HttpClient` via `IHttpClientFactory` (DI-managed)
- **Audio playback**: ffplay (part of ffmpeg suite, already used for PCM→MP3 conversion)
  - Same ffmpeg dependency already in use — no additional packages
  - `ffplay -nodisp -autoexit -i file.mp3` for file playback
  - Supports streaming from stdin for real-time playback
  - Volume control, device selection via ALSA device names
  - Alternative considered: ManagedBass (rejected — requires separate native libs per platform)
- **Audio extraction from video**: [Xabe.FFmpeg](https://www.nuget.org/packages/Xabe.FFmpeg) v6.0.2
  - Cross-platform (.NET Standard 2.0) managed wrapper around FFmpeg
  - Fluent API for media conversion: extract audio track from video files
  - Requires FFmpeg binaries installed on the system (available via package manager on all platforms)
  - Alternative: shell out to `ffmpeg` CLI directly (simpler, no dependency, FFmpeg is ubiquitous on Linux)
  - Supports: MP4, MKV, AVI, MOV, WebM, and all common video formats
- **Future UI**: Blazor or web application (not in scope yet)

### appsettings.json structure

```json
{
  "OpenRouter": {
    "ApiKey": "",
    "BaseUrl": "https://openrouter.ai/api/v1",
    "TtsModel": "mistralai/voxtral-mini-tts-2603"
  },
  "Mistral": {
    "ApiKey": "",
    "BaseUrl": "https://api.mistral.ai/v1"
  },
  "Voice": {
    "DefaultVoice": "alloy",
    "RefAudioPath": "",
    "SavedVoiceId": ""
  },
  "Audio": {
    "OutputFormat": "mp3",
    "OutputPath": "output"
  },
  "Ffmpeg": {
    "BinaryPath": ""
  }
}
```

API keys go in `appsettings.Development.json` (gitignored). `Ffmpeg.BinaryPath` can be left empty if FFmpeg is on PATH.

## Implementation Plan - Stages

Detailed plans for each stage are in separate files:

| Stage | Description | Plan File | Status |
|-------|-------------|-----------|--------|
| 1 | Project setup + Text to Audio File | [create-stage-1.md](create-stage-1.md) | Done |
| 2 | Voice Profiles, Model Discovery, Batch | [create-stage-2.md](create-stage-2.md) | Done |
| 3 | Audio Playback & Streaming | [create-stage-3.md](create-stage-3.md) | Planning |
| 3 | Voice Cloning | *TBD* | |
| 4 | Voice Management (Saved Voice IDs) | *TBD* | |
| 5 | Pipe Integration | *TBD* | |
| 6 | OpenAI-Compatible Proxy | *TBD* | |
| 7 | Full Conversation App | *TBD* | |

### Stage 1: Project Setup + Text to Audio File *(start here)*
- .NET 8 console app with DI, `HostBuilder`, `appsettings.json`
- `ITtsService` / `TtsService` — calls OpenRouter `/api/v1/audio/speech` with Voxtral Mini TTS
- Text input: direct text argument OR path to text file
- Saves response as MP3 to `output/` directory with timestamped filename
- No voice cloning — use default voice
- Test project with unit tests
- **Deliverable**: `dotnet run -- "Hello world"` produces `output/20260523-131300.mp3`

### Stage 2: Native Audio Playback
- Add ManagedBass dependency
- `IAudioPlayer` / `AudioPlayer` — loads native BASS libs, plays audio through speakers
- After TTS, play the resulting audio through speakers instead of just saving to file
- `--play` flag to toggle playback vs file-only
- Test on Linux first, validate cross-platform BASS lib loading
- **Deliverable**: `dotnet run -- "Hello world" --play` speaks aloud

### Stage 3: Voice Cloning
- Accept a voice sample file (3+ seconds) via `--voice <path>` flag
- Supports audio files (MP3, WAV, FLAC, OGG) and video files (MP4, MKV, AVI, MOV, WebM)
- If video file is provided, extract audio track using system FFmpeg
- Base64-encode the audio and pass as `ref_audio` in TTS requests
- **Deliverable**: `dotnet run -- "Hello world" --voice clip.mp4 --play` extracts audio from video, clones voice, speaks aloud

### Stage 4: Voice Management (Saved Voice IDs)
- `--voice-save <name> <sample-path>` — create a saved voice profile via Mistral API
- `--voice-list` — list saved voices
- `--voice-delete <id>` — delete a saved voice
- `--voice-id <id>` — use a saved voice ID instead of inline ref_audio
- Requires Mistral API key (separate from OpenRouter)
- **Deliverable**: `dotnet run -- "Hello world" --voice-id voice-abc123 --play`

### Stage 5: Pipe Integration
- Accept text via stdin in addition to CLI args
- Integrate with Kilo's output: pipe model response to TTS
- Basic shell pipeline: `kilo respond | dotnet run --project Conversation`
- **Deliverable**: AI responses are spoken aloud automatically

### Stage 6: OpenAI-Compatible Proxy
- HTTP server implementing OpenAI chat completions API
- Intercepts response text and feeds to TTS (Voxtral via OpenRouter)
- Whispering/Kilo configure to use `localhost:PORT` as their API endpoint
- Transparent pass-through with TTS side-effect
- **Deliverable**: existing tools unchanged, responses spoken automatically

### Stage 7: Full Conversation App
- Unified application handling STT + LLM + TTS
- Real-time streaming (partial TTS as LLM generates, using PCM format for ~0.8s first-audio latency)
- WebSocket-based for low latency
- Configurable model, voice, personality
- UI: Blazor or web frontend
- **Deliverable**: standalone conversation application

## CLI Command Reference (target)

```
# Phase 1: basic TTS to file
dotnet run -- "Hello world"
dotnet run -- "Hello world" --output myfile.mp3

# Phase 2: play through speakers
dotnet run -- "Hello world" --play

# Phase 3: voice cloning from audio or video sample
dotnet run -- "Hello world" --voice andy-sample.mp3 --play
dotnet run -- "Hello world" --voice interview-clip.mp4 --play
dotnet run -- "Hello world" --voice andy.wav

# Phase 4: saved voice management
dotnet run -- --voice-save andy andy-sample.mp3
dotnet run -- --voice-save andy interview-clip.mp4
dotnet run -- --voice-list
dotnet run -- --voice-delete voice-abc123
dotnet run -- "Hello world" --voice-id voice-abc123 --play

# Phase 5: pipe mode
echo "Hello world" | dotnet run -- --stdin --play
kilo respond | dotnet run -- --stdin --play
```

## Key Technical Considerations

- **Latency**: PCM format gives ~0.8s time-to-first-audio. Streaming TTS as LLM generates is critical for natural feel
- **Interruption**: user should be able to interrupt AI mid-sentence (voice activity detection)
- **Context**: conversation history management for coherent multi-turn dialogue
- **API costs**: Voxtral at $16/1M chars, LLM per-token pricing - monitor usage
- **Voice management**: saved voice IDs require a separate Mistral API key; inline ref_audio works with OpenRouter only
- **Voice retention**: saved voices on Mistral servers have a `retention_notice` (default 30 days) — may be purged if unused
- **Text preprocessing**: strip markdown/special chars from LLM output before TTS, verbalize numbers
- **ManagedBass native libs**: need BASS .so/.dll/.dylib for each platform. Bundled via NuGet or loaded from known path
- **FFmpeg**: required for video-to-audio extraction. Ubiquitous on Linux. Xabe.FFmpeg provides managed wrapper, or shell out to CLI
- **appsettings.Development.json**: gitignored, holds API keys. `appsettings.json` has defaults with empty keys

## Project Structure

```
conversation/
├── src/
│   └── benow-conversation/
│       ├── benow-conversation.csproj
│       ├── Program.cs
│       ├── appsettings.json
│       ├── appsettings.Development.json
│       ├── Services/
│       │   ├── ITtsService.cs
│       │   ├── TtsService.cs
│       │   ├── IAudioPlayer.cs
│       │   ├── AudioPlayer.cs
│       │   ├── IMediaExtractor.cs
│       │   └── MediaExtractor.cs
│       ├── Models/
│       │   ├── TtsRequest.cs
│       │   └── VoiceProfile.cs
│       └── Configuration/
│           └── AppSettings.cs
├── tests/
│   └── benow-conversation.Tests/
│       └── benow-conversation.Tests.csproj
├── benow-conversation.sln
├── .gitignore
└── docs/
    └── plans/
        ├── high-level-plan.md
        └── create-stage-1.md
```

Naming convention: `benow-conversation` for the main project, `benow-conversation-{x}` for additional projects (e.g. `benow-conversation-shared`, `benow-conversation-models`), `benow-conversation.Tests` and `benow-conversation-{x}.Tests` for test projects. Lowercase `{x}` by convention.

## Next Steps

1. `dotnet new console` with proper DI setup
2. Configure `appsettings.json` / `appsettings.Development.json` with OpenRouter key
3. Implement `TtsService` — call OpenRouter TTS endpoint
4. Wire up `Program.cs` to accept text arg and invoke service
5. Test: `dotnet run -- "Hello world"` produces audio file
