# Program / Entry Point

**Source:** `src/benow-conversation/Program.cs`

## Overview

The entry point is a top-level-statements `Program.cs` that handles:

1. **Logging setup** -- Configures Serilog with console and file sinks. Log files are written to a `logs/` directory relative to the project root (determined by walking up from `AppContext.BaseDirectory` to find the `.csproj` file).

2. **DI container** -- Builds an `IHost` with all services registered:
   - `IAudioConverter` → `AudioConverter`
   - `ITtsService` → `TtsService`
   - `IModelService` → `ModelService`
   - `IAudioPlayer` → `AudioPlayer`
   - `ISpeechQueue` → `SpeechQueue`
   - `IProxyService` → `ProxyService`
   - Two named `HttpClient` factories: `"OpenRouter"` (60s timeout) and `"ProxyBackend"` (5min timeout)

3. **CLI argument parsing** -- A manual loop over `args` that recognizes flags and positional text input. Supports:
   - Direct text or file path as the first positional argument
   - `--text-file` for explicit file input
   - `--daemon` to switch to proxy mode
   - `--persona`, `--voice`, `--model`, `--openai-instructions`, `--temperature`, `--seed` for TTS configuration
   - `--output`, `--stream`, `--play`, `--no-play` for output control
   - `--output-profile`, `--device`, `--volume` for audio device settings
   - `--list-models`, `--list-voices`, `--list-personas`, `--list-devices`, `--list-output-profiles` for discovery
   - `--save-persona`, `--set-default`, `--save-output-profile`, `--set-output-default` for configuration management

4. **Mode dispatch** -- Routes to either daemon mode (`ProxyService.RunAsync` + `SpeechQueue`) or CLI synthesis mode with persona resolution, output handling, and optional playback.

5. **Persona and output profile management** -- Functions to resolve, save, and list personas and output profiles by reading/writing `appsettings.json` directly.

## Key Functions

| Function | Purpose |
|---|---|
| `FindProjectRoot()` | Walks up from the build output directory to find the `.csproj` file location |
| `ResolvePersona()` | Looks up a named persona from config, throws if not found |
| `ResolveOutputSettings()` | Merges output profile, device override, and volume override |
| `SavePersona()` / `SaveOutputProfile()` | Persists settings back to `appsettings.json` |
| `HandleStream()` | Synthesizes to a stream and pipes to `AudioPlayer.PlayStreamAsync` |
| `HandleListModels()` / `HandleListVoices()` | Discovers and displays available TTS models and voices |
| `ParseOutputPath()` | Determines whether `--output` is a file path (with format from extension) or a directory |
