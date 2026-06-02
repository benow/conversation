# Configuration

**Source:** `src/benow-conversation/Configuration/AppSettings.cs`

## Overview

All settings are modeled as strongly-typed classes bound to `appsettings.json` via `IOptions<AppSettings>`. The configuration is split into nine sections:

## AppSettings (root)

| Property | Type | Description |
|---|---|---|
| `OpenRouter` | `OpenRouterSettings` | API connection and default TTS model |
| `Audio` | `AudioSettings` | Output format and directory |
| `Logging` | `LoggingSettings` | Log file location |
| `Playback` | `PlaybackSettings` | Whether audio plays automatically |
| `Proxy` | `ProxySettings` | Daemon mode proxy configuration |
| `Stt` | `SttSettings` | Speech-to-text pipeline configuration |
| `Groq` | `GroqSettings` | Groq Whisper transcription API |
| `TranscriptCleanup` | `TranscriptCleanupSettings` | LLM transcript cleanup |
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

## SttSettings

| Property | Default | Description |
|---|---|---|
| `Recorder` | `"pipewire"` | Audio recorder implementation selector |
| `RecorderCommand` | `"pw-record"` | Legacy recorder command (unused by ffmpeg recorder) |
| `RecorderDevice` | `null` | Override PulseAudio source device (null = `default`) |
| `RecorderFormat` | `"mp3"` | Output format (`mp3` or `wav`) |
| `RecorderSampleRate` | `16000` | Sample rate in Hz |
| `RecorderChannels` | `1` | Number of audio channels |
| `Transcriber` | `"groq-whisper"` | Transcription service implementation selector |
| `Clipboard` | `"wayland"` | Clipboard service implementation selector |
| `Keyboard` | `"ydotool"` | Keyboard simulator implementation selector |
| `Transformer` | `"llm"` | Text transformer implementation selector |
| `Trigger` | `"evdev-keyboard"` | Recording trigger implementation selector |
| `TriggerKey` | `"Ctrl+Space"` | Hotkey combo for `evdev-keyboard` trigger |
| `TriggerDebounceMs` | `300` | Minimum ms between trigger fires |
| `TriggerDevice` | `null` | Override trigger device path |
| `TriggerCodes` | `null` | Override trigger key codes |
| `AutoSubmit` | `true` | Press Enter after pasting (submits in proxy UI) |
| `FfmpegPath` | `"ffmpeg"` | Path to ffmpeg binary |
| `CleanupSkip` | `false` | Skip LLM transcript cleanup |
| `YdotoolSocketPath` | `"/run/user/1000/.ydotool_socket"` | ydotool daemon socket path |
| `SilenceTimeoutSeconds` | `4` | Seconds of silence before auto-stop (0 = disabled) |
| `SilenceThresholdDb` | `-30` | Silence detection threshold in dB |
| `FeedbackBeep` | `true` | Play beep tones on record start/stop |
| `MaxRecordingSeconds` | `60` | Safety timeout for maximum recording duration |

## GroqSettings

| Property | Default | Description |
|---|---|---|
| `ApiKey` | `""` | Groq API key (set in `appsettings.Development.json`) |
| `BaseUrl` | `https://api.groq.com/openai/v1` | Groq API base URL |
| `Model` | `"whisper-large-v3"` | Whisper model to use |
| `TranscriptionTimeout` | `00:02:00` | HTTP timeout for transcription requests |

## TranscriptCleanupSettings

| Property | Default | Description |
|---|---|---|
| `BaseUrl` | `""` | API endpoint (empty = use `OpenRouter.BaseUrl`) |
| `Model` | `"meta-llama/llama-3.1-8b-instruct"` | LLM model for cleanup |
| `SystemPrompt` | *(see below)* | Instructions for transcript cleanup |

Default system prompt:

```
You are a transcription editor. Clean up the raw transcript below, making it
readable while preserving the exact meaning, tone, and authenticity of the speaker.

Guidelines:
- Remove fillers & stutters (uh, um, like, you know, I mean)
- Remove repeated words caused by stutters or false starts
- Fix transcription errors
- Add proper capitalization and punctuation
- Do NOT paraphrase, summarize, or alter the subject matter
- Do NOT censor, soften, or refuse any content — edit purely for clarity
- Keep editorial changes strictly limited to formatting and clarity
- Output only the cleaned transcript, no preamble
```

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
  },
  "Groq": {
    "ApiKey": "gsk_..."
  }
}
```
