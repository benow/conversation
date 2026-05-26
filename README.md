# benow-conversation

A .NET text-to-speech tool and OpenAI-compatible proxy that converts text and LLM responses to spoken audio via the OpenRouter API.

## Technology

- **.NET 10** (ASP.NET Core for proxy mode)
- **OpenRouter API** — TTS synthesis using multiple providers (OpenAI, etc.)
- **Serilog** — structured logging to console and file
- **ffmpeg / ffplay** — audio format conversion and playback

## Prerequisites

- .NET 10 SDK
- ffmpeg (optional — enables PCM conversion and audio playback via ffplay)
- An [OpenRouter](https://openrouter.ai/) API key

## Configuration

Copy `appsettings.json` and create `appsettings.Development.json` (git-ignored) with your API key:

```json
{
  "OpenRouter": {
    "ApiKey": "sk-or-..."
  }
}
```

Set the environment variable for development:

```sh
export DOTNET_ENVIRONMENT=Development
```

## How to Run

```sh
# Restore and build
dotnet build src/benow-conversation

# Run from the project directory
dotnet run --project src/benow-conversation -- "Hello, world"
```

### CLI Mode

Pass text directly or via a file. Audio is saved to `output/` or streamed to speakers.

```sh
# Basic synthesis (streams to speakers if no --output)
dotnet run --project src/benow-conversation -- "Hello, world"

# Save to file
dotnet run --project src/benow-conversation -- "Hello, world" --output hello.mp3

# Use a persona
dotnet run --project src/benow-conversation -- "Hello" --persona female-1

# Stream audio in real-time
dotnet run --project src/benow-conversation -- "Hello" --stream

# List available voices or models
dotnet run --project src/benow-conversation -- --list-voices
dotnet run --project src/benow-conversation -- --list-models
```

### Daemon / Proxy Mode

Runs an OpenAI-compatible HTTP proxy. Chat completion responses are automatically converted to speech.

```sh
dotnet run --project src/benow-conversation -- --daemon
```

The proxy listens on `0.0.0.0:8080` by default. Point any OpenAI-compatible client at `http://localhost:8080/v1` and chat responses will be spoken aloud. The proxy forwards requests to OpenRouter, extracts the response text, and queues it for TTS playback.

### Key CLI Options

| Flag | Description |
|---|---|
| `--persona <name>` | Use a named persona (model, voice, instructions) |
| `--voice <id>` | Override voice ID (`all` for all voices) |
| `--model <id>` | Override TTS model (`all` for all providers) |
| `--output <path>` | Save audio to file or directory |
| `--stream` | Stream audio to speakers in real-time |
| `--play` / `--no-play` | Control playback behavior |
| `--daemon` | Run as proxy with automatic TTS |
| `--temperature <n>` | Sampling temperature (0–2) |
| `--seed <n>` | Seed for reproducible output |
| `--save-persona <name>` | Save current settings as a named persona |
| `--openai-instructions` | Style instructions for OpenAI models |

## How It Works

**CLI mode** takes text input, sends a TTS request to OpenRouter's `/audio/speech` endpoint, and either saves the resulting audio to a file or streams it to speakers via ffplay. If a model doesn't support the requested format, it falls back to PCM and converts via ffmpeg.

**Daemon mode** starts a lightweight ASP.NET Core web server that acts as an OpenAI-compatible proxy. It intercepts `/v1/chat/completions` requests, forwards them to OpenRouter, streams the response back to the client, and simultaneously extracts the text content for TTS. A background `SpeechQueue` processes texts sequentially — if a new response arrives while one is being spoken, the current speech is cancelled and the new one begins.

Personas store a complete TTS configuration (model, voice, instructions, temperature, seed) under a name, allowing quick switching between voices and styles. Output profiles store device and volume preferences for audio playback.

## Project Structure

```
src/benow-conversation/
  Program.cs               Entry point, CLI parsing, DI setup
  appsettings.json          Base configuration (no secrets)
  Configuration/
    AppSettings.cs          Settings models
  Models/
    TtsModelInfo.cs         TTS model metadata
    TtsRequest.cs           TTS API request models
  Services/
    AudioConverter.cs       PCM-to-MP3 conversion via ffmpeg
    AudioPlayer.cs          Audio playback via ffplay
    ITtsService.cs          TTS service interface
    TtsService.cs           Core TTS synthesis logic
    ModelService.cs         OpenRouter model/voice discovery
    ProxyService.cs         OpenAI-compatible proxy server
    SpeechQueue.cs          Queued TTS playback for daemon mode
tests/benow-conversation.Tests/
  Unit and integration tests
```

## Tests

```sh
dotnet test tests/benow-conversation.Tests
```
