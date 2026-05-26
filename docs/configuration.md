# Configuration

**Source:** `src/benow-conversation/Configuration/AppSettings.cs`

## Overview

All settings are modeled as strongly-typed classes bound to `appsettings.json` via `IOptions<AppSettings>`. The configuration is split into six sections:

## AppSettings (root)

| Property | Type | Description |
|---|---|---|
| `OpenRouter` | `OpenRouterSettings` | API connection and default TTS model |
| `Audio` | `AudioSettings` | Output format and directory |
| `Logging` | `LoggingSettings` | Log file location |
| `Playback` | `PlaybackSettings` | Whether audio plays automatically |
| `Proxy` | `ProxySettings` | Daemon mode proxy configuration |
| `Personas` | `Dictionary<string, VoicePersona>` | Named TTS configurations |
| `OutputProfiles` | `Dictionary<string, OutputProfile>` | Named audio device/volume presets |

## OpenRouterSettings

| Property | Default | Description |
|---|---|---|
| `ApiKey` | `""` | OpenRouter API key (set in `appsettings.Development.json`) |
| `BaseUrl` | `https://openrouter.ai/api/v1` | API base URL |
| `TtsModel` | `openai/gpt-4o-mini-tts-2025-12-15` | Default TTS model |

## AudioSettings

| Property | Default | Description |
|---|---|---|
| `OutputFormat` | `"mp3"` | Default audio format for file output |
| `OutputPath` | `"output"` | Directory for generated audio files |

## ProxySettings

| Property | Default | Description |
|---|---|---|
| `BindAddress` | `"0.0.0.0"` | HTTP bind address |
| `Port` | `8080` | HTTP listen port |
| `BackendUrl` | `https://openrouter.ai/api/v1` | Upstream LLM API |
| `BackendModel` | `""` | Default model to inject if client omits one |
| `TtsPersona` | `""` | Named persona to use for TTS in daemon mode |
| `TtsModel` / `TtsVoice` | `""` | Direct model/voice overrides (fallback if no persona) |
| `LogBodies` | `false` | Log full request/response bodies (debugging) |

## VoicePersona

A named preset bundling a complete TTS configuration:

| Property | Description |
|---|---|
| `Model` | TTS model ID (e.g. `openai/gpt-4o-mini-tts-2025-12-15`) |
| `Voice` | Voice ID (e.g. `marin`, `alloy`) |
| `OpenAiInstructions` | Style instructions for OpenAI models only |
| `Temperature` | Sampling temperature (0--2) |
| `Seed` | Reproducibility seed |
| `IsDefault` | Whether this persona is the default selection |

## OutputProfile

A named preset for audio output:

| Property | Description |
|---|---|
| `Device` | ALSA device name (empty = system default) |
| `Volume` | Playback volume (0--100) |
| `FfplayPath` | Custom ffplay binary path |
| `IsDefault` | Whether this profile is the default |

## Secrets

API keys are stored in `appsettings.Development.json` (git-ignored), loaded when `DOTNET_ENVIRONMENT=Development`:

```json
{
  "OpenRouter": {
    "ApiKey": "sk-or-..."
  }
}
```
