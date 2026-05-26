# TTS Service

**Source:** `src/benow-conversation/Services/TtsService.cs`
**Interface:** `src/benow-conversation/Services/ITtsService.cs`

## Overview

The core synthesis engine. Sends text to OpenRouter's `/audio/speech` endpoint and returns audio data as either files or in-memory streams.

## Interface

```csharp
public interface ITtsService
{
    Task<string> SynthesizeToFileAsync(...);
    Task<(Stream AudioStream, string Format)> SynthesizeToStreamAsync(...);
    Task<List<string>> SynthesizeAllVoicesAsync(...);
    Task<List<string>> SynthesizeAllModelsAsync(...);
    Task<List<string>> SynthesizeAllProvidersAsync(...);
}
```

## Key Behaviors

### Format Fallback

When a model doesn't support the requested audio format (e.g. mp3), the service detects the error response, caches the fact that the model only supports PCM, and automatically retries with `response_format=pcm`. The PCM data is then converted to the target format via `AudioConverter`. This caching is per-model for the lifetime of the session.

### OpenAI Instructions

When using OpenAI models (those with IDs starting `openai/`), style instructions are passed through the `provider.options.openai.instructions` field in the TTS request. Non-OpenAI models silently skip instructions.

### Synthesis Methods

| Method | Purpose |
|---|---|
| `SynthesizeToFileAsync` | Single voice/model, saves audio to disk, returns file path |
| `SynthesizeToStreamAsync` | Single voice/model, returns in-memory stream + format name |
| `SynthesizeAllVoicesAsync` | Iterates all voices for the configured model |
| `SynthesizeAllModelsAsync` | Iterates all available TTS models with a single voice |
| `SynthesizeAllProvidersAsync` | Iterates all models x all voices (comprehensive batch) |

### Error Handling

- **401/403** -- API key invalid or unauthorized
- **429** -- Rate limit exceeded
- **JSON response body** -- TTS API error (parsed for message)
- **Empty response** -- Service issue
- **Connection failure** -- Network/unreachable host

### Batch Operations

Batch methods (`SynthesizeAll*Async`) iterate sequentially through each voice/model combination, catching per-item failures without aborting the entire batch. Each generated file is named with a pattern like `{base}_{modelSlug}_{voiceId}.{ext}`.

### Internal Helpers

| Helper | Purpose |
|---|---|
| `CreateModelService()` | Creates an ad-hoc `ModelService` for voice discovery during batch ops |
| `ValidateApiKey()` | Throws if API key is empty |
| `SendRequestAsync()` | POSTs the TTS request to OpenRouter |
| `ReadAudioResponseAsync()` | Reads the response, handling HTTP errors and JSON error bodies |
| `ResolveOutputPath()` | Resolves relative output paths against the project root |
