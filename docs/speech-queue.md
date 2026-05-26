# Speech Queue

**Source:** `src/benow-conversation/Services/SpeechQueue.cs`

## Overview

A background service that processes TTS requests sequentially for daemon mode. Uses `System.Threading.Channels.Channel<string>` as an unbounded single-reader queue.

## Interface

```csharp
public interface ISpeechQueue
{
    void Enqueue(string text);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
```

## How It Works

### Enqueuing

`Enqueue()` writes text to an unbounded `Channel<string>`. The text is trimmed; empty strings are discarded.

### Processing Loop

`ProcessQueueAsync()` runs as a background task, reading from the channel via `ReadAllAsync()`:

1. **Cancel previous speech** -- When new text arrives, any currently playing speech is cancelled via a linked `CancellationTokenSource`. This ensures the latest response is spoken immediately rather than queuing up.
2. **Synthesize** -- Calls `TtsService.SynthesizeToStreamAsync()` with the persona's model, voice, instructions, temperature, and seed
3. **Play** -- Streams the audio to `AudioPlayer.PlayStreamAsync()`
4. **Cancellation handling**:
   - If cancelled because a new speech arrived (`cts.IsCancellationRequested` but not the global token) → logs "Speech cancelled" and continues
   - If cancelled because the daemon is shutting down → breaks out of the loop
   - Other exceptions → logged as errors, processing continues

### Persona Resolution

At construction time, the queue resolves the TTS configuration from (in priority order):
1. The named persona specified in `Proxy.TtsPersona`
2. Direct `Proxy.TtsModel` / `Proxy.TtsVoice` overrides
3. Defaults from `OpenRouter.TtsModel` and `"alloy"` voice

### Lifecycle

- `StartAsync()` -- Launches the processing loop as a background `Task`
- `StopAsync()` -- Completes the channel writer and awaits the processing task to finish

## Cancellation Design

The key design decision is **newest-wins**: if multiple LLM responses arrive in quick succession, only the most recent one is spoken. Each new enqueue cancels the previous playback via a linked `CancellationTokenSource`, providing responsive interruption behavior.
