# Models

**Sources:**
- `src/benow-conversation/Models/TtsModelInfo.cs`
- `src/benow-conversation/Models/TtsRequest.cs`

## TtsModelInfo

Metadata about a TTS model available through OpenRouter.

| Property | Type | Description |
|---|---|---|
| `Id` | `string` | Model identifier (e.g. `openai/gpt-4o-mini-tts-2025-12-15`) |
| `Name` | `string` | Display name |
| `Description` | `string` | Model description |
| `PromptPricePerMillionChars` | `double` | Input cost per 1M characters |
| `CompletionPricePerMillionChars` | `double` | Output cost per 1M characters |
| `VoiceCount` | `int` | Number of supported voices |
| `ContextLength` | `int` | Maximum context window |

## VoiceInfo

A voice available for a specific TTS model.

| Property | Type | Description |
|---|---|---|
| `Id` | `string` | Voice identifier (e.g. `marin`, `alloy`, `af_bella`) |
| `Description` | `string` | Inferred human-readable description |

## TtsRequest

The request body sent to OpenRouter's `/audio/speech` endpoint.

| Property | JSON Key | Description |
|---|---|---|
| `Model` | `model` | TTS model ID |
| `Input` | `input` | Text to synthesize |
| `Voice` | `voice` | Voice ID |
| `ResponseFormat` | `response_format` | Audio format (mp3, pcm, wav, etc.) |
| `Temperature` | `temperature` | Sampling temperature (omitted if null) |
| `Seed` | `seed` | Reproducibility seed (omitted if null) |
| `Provider` | `provider` | Provider-specific options (e.g. OpenAI instructions) |

## ProviderOptions

Wrapper for provider-specific parameters. Currently used only for OpenAI style instructions:

```json
{
  "options": {
    "openai": {
      "instructions": "young, british, friendly"
    }
  }
}
```
