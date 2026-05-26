# benow-conversation

A .NET text-to-speech tool and OpenAI-compatible proxy that converts text and LLM chat completion responses to spoken audio via the OpenRouter API.

## Technology Stack

| Layer | Technology | Purpose |
|---|---|---|
| Runtime | **.NET 10** | Target framework |
| Web framework | **ASP.NET Core** (Minimal APIs) | Proxy server in daemon mode |
| TTS backend | **OpenRouter API** (`/audio/speech`) | Multi-provider text-to-speech synthesis |
| Audio playback | **ffplay** (ffmpeg) | Real-time audio streaming and file playback |
| Audio conversion | **ffmpeg** | PCM-to-MP3 transcoding when models don't support the requested format |
| Audio devices | **aplay** (ALSA) | Enumeration of system audio output devices on Linux |
| Logging | **Serilog** | Structured logging to console and rotating log files |
| Configuration | **Microsoft.Extensions.Configuration** | JSON-based settings with environment-specific overrides |
| DI | **Microsoft.Extensions.DependencyInjection** | Constructor-injected services via `IHost` |
| Testing | **xUnit** + **Moq** | Unit and integration tests |

## Architectural Approach

The application has two operating modes sharing a common service layer:

1. **CLI mode** -- accepts text input, synthesizes speech, saves to file or streams to speakers. Supports batch operations across all voices and/or all TTS models.
2. **Daemon/Proxy mode** -- runs a lightweight ASP.NET Core web server implementing the OpenAI Chat Completions API. Incoming requests are forwarded to OpenRouter, responses are streamed back to the client, and the text content is extracted and queued for TTS playback.

Both modes share the same core services: `TtsService` for synthesis, `AudioPlayer` for playback, `AudioConverter` for format conversion, and `ModelService` for model/voice discovery.

Configuration is centralized in `appsettings.json` with secrets in a git-ignored `appsettings.Development.json`. Named **personas** store complete TTS configurations (model, voice, instructions, temperature, seed), and **output profiles** store device and volume preferences.

## Project Structure

```
benow-conversation/
  src/benow-conversation/
    Program.cs                    Entry point, CLI parsing, DI container setup
    appsettings.json              Base configuration (no secrets)
    Configuration/
      AppSettings.cs              Settings model classes
    Models/
      TtsModelInfo.cs             TTS model and voice metadata
      TtsRequest.cs               TTS API request structure
    Services/
      ITtsService.cs              TTS service interface
      TtsService.cs               Core synthesis logic
      AudioConverter.cs           PCM-to-MP3 conversion via ffmpeg
      AudioPlayer.cs              Audio playback via ffplay, device listing via aplay
      ModelService.cs             OpenRouter model/voice discovery
      ProxyService.cs             OpenAI-compatible proxy server
      SpeechQueue.cs              Queued TTS playback for daemon mode
  tests/benow-conversation.Tests/  Unit and integration tests
  docs/                            Documentation
```

## Component Documentation

| Component | File | Description |
|---|---|---|
| [Program / Entry Point](program.md) | `Program.cs` | CLI argument parsing, DI setup, mode dispatch |
| [Configuration](configuration.md) | `Configuration/AppSettings.cs` | Settings models for all subsystems |
| [TTS Service](tts-service.md) | `Services/TtsService.cs` | Core text-to-speech synthesis via OpenRouter |
| [Audio Player](audio-player.md) | `Services/AudioPlayer.cs` | Playback via ffplay, streaming, device enumeration |
| [Audio Converter](audio-converter.md) | `Services/AudioConverter.cs` | PCM-to-MP3 transcoding via ffmpeg |
| [Model Service](model-service.md) | `Services/ModelService.cs` | TTS model and voice discovery from OpenRouter |
| [Proxy Service](proxy-service.md) | `Services/ProxyService.cs` | OpenAI-compatible HTTP proxy with TTS |
| [Speech Queue](speech-queue.md) | `Services/SpeechQueue.cs` | Background queued TTS playback for daemon mode |
| [Models](models.md) | `Models/` | Request/response data structures |
| [Tests](tests.md) | `tests/` | Test suite overview |
| [External Tools](external-tools.md) | System utilities | Installation and setup of ffmpeg, ffplay, aplay, pw-record, wl-copy, ydotool |

## Quick Start

```sh
# Build
dotnet build src/benow-conversation

# CLI mode -- synthesize and play
dotnet run --project src/benow-conversation -- "Hello, world"

# Daemon mode -- OpenAI-compatible proxy with automatic TTS
dotnet run --project src/benow-conversation -- --daemon
```

The proxy listens on `http://0.0.0.0:8080` by default. Point any OpenAI-compatible client at `http://localhost:8080/v1`.
