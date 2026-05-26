# Model Service

**Source:** `src/benow-conversation/Services/ModelService.cs`

## Overview

Discovers available TTS models and voices from the OpenRouter API. Used for listing models/voices and for batch synthesis operations that need to enumerate all voices.

## Interface

```csharp
public interface IModelService
{
    Task<List<TtsModelInfo>> GetTtsModelsAsync();
    Task<List<VoiceInfo>> GetVoicesForModelAsync(string? modelId = null);
}
```

## GetTtsModelsAsync

Queries `GET /models?output_modalities=speech` on the OpenRouter API. Parses the response `data` array into `TtsModelInfo` objects with:

- Model ID, name, description, context length
- Pricing (prompt and completion per million characters, converted from per-char string)
- Voice count from `supported_voices` array length

## GetVoicesForModelAsync

Same API call, but filters for a specific model ID and extracts the `supported_voices` array into `VoiceInfo` objects with inferred descriptions.

## Voice Description Inference

`InferVoiceDescription()` generates human-readable descriptions from voice ID naming conventions:

| Prefix | Description |
|---|---|
| `af_` | American female |
| `am_` | American male |
| `bf_` / `bm_` | British female/male |
| `ef_` / `em_` | English female/male |
| `ff_` / `fm_` | French female/male |
| `jf_` / `jm_` | Japanese female/male |
| `zf_` / `zm_` | Chinese female/male |
| `hf_` / `hm_` | Hindi female/male |
| `if_` / `im_` | Italian female/male |
| `pf_` / `pm_` | Portuguese female/male |

Also handles patterns like `en_paul_*`, `gb_oliver_*`, `gb_jane_*`, `fr_marie_*`, and descriptive prefixes like `american_female`, `british_male`, etc.
