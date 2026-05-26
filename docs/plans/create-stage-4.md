# Stage 4: OpenAI Proxy with TTS

## Goal

Run as a daemon that acts as an OpenAI-compatible API proxy. KiloCode (or any client) connects to `localhost:8080/v1` instead of directly to OpenRouter. The proxy forwards requests transparently, intercepts response text, and speaks it through the TTS pipeline.

## Architecture

```
KiloCode → POST localhost:8080/v1/chat/completions → Proxy → OpenRouter
                                                        ↓
                                                  Response text
                                                  ↙            ↘
                                    TTS → ffplay         Return to KiloCode
```

## Design Decisions

### Why an OpenAI-compatible proxy
- KiloCode supports custom `baseURL` — just point it at localhost
- No KiloCode plugins or extensions needed
- Works with any OpenAI-compatible client
- Transparent interception — client doesn't know it's proxied

### Non-blocking TTS
- Response streams back to client in real-time (no added latency)
- TTS synthesis + playback happens asynchronously after stream completes
- If a new response arrives while TTS is playing, the previous speech is cancelled

### Streaming SSE passthrough
- Client sends `stream: true` (standard for chat completions)
- Proxy forwards SSE chunks to client immediately
- Accumulates `delta.content` chunks into full text
- On `[DONE]`, queues full text for TTS

## Configuration

### `appsettings.json`

```json
{
  "Proxy": {
    "Port": 8080,
    "BackendUrl": "https://openrouter.ai/api/v1",
    "TtsPersona": "",
    "TtsModel": "openai/gpt-4o-mini-tts-2025-12-15",
    "TtsVoice": "marin"
  }
}
```

- `Port` — localhost port to listen on
- `BackendUrl` — upstream OpenRouter (or any OpenAI-compatible) base URL
- `TtsPersona` — persona name to use for TTS (overrides TtsModel/TtsVoice if set)
- `TtsModel` — TTS model to use (if no persona)
- `TtsVoice` — TTS voice to use (if no persona)

### New config class

```csharp
public class ProxySettings
{
    public int Port { get; set; } = 8080;
    public string BackendUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string TtsPersona { get; set; } = "";
    public string TtsModel { get; set; } = "";
    public string TtsVoice { get; set; } = "";
}
```

## Endpoints

### `POST /v1/chat/completions`
- Forward request body to `{BackendUrl}/chat/completions`
- If `stream: true`:
  - Forward SSE chunks to client as they arrive
  - Accumulate `delta.content` from each chunk
  - On `[DONE]`, queue accumulated text for TTS
- If `stream: false`:
  - Forward response to client
  - Extract `choices[0].message.content`
  - Queue for TTS

### `GET /v1/models`
- Pass-through to `{BackendUrl}/models`
- No TTS

### `POST /v1/audio/speech`
- Pass-through to `{BackendUrl}/audio/speech`
- No TTS (this IS the TTS endpoint, no need to re-TTS it)

### All other routes
- Pass-through to backend

## Implementation

### Services

**`IProxyService` / `ProxyService`** — handles HTTP proxying:
- Creates an `HttpClient` for backend requests
- Forwards request headers (Authorization, Content-Type, etc.)
- Handles streaming and non-streaming responses
- Extracts assistant text from response

**`ISpeechQueue` / `SpeechQueue`** — manages TTS playback:
- Background `Channel<string>` that queues text for TTS
- Processes queue sequentially (one TTS at a time)
- Cancels current playback if new text arrives
- Uses `ITtsService.SynthesizeToStreamAsync` + `IAudioPlayer.PlayStreamAsync`

### CLI

**`--daemon`** flag:
- Starts the proxy HTTP server
- Blocks until Ctrl+C
- Uses `Microsoft.AspNetCore.Builder.WebApplication` for minimal API server
- Or uses raw `HttpListener` for zero additional dependencies

### Technology choice: HttpListener vs ASP.NET Core

**HttpListener** (chosen):
- Zero additional NuGet packages
- Simpler for a single-purpose proxy
- Lower overhead
- Sufficient for localhost-only traffic

**ASP.NET Core Minimal APIs** (not chosen):
- Would add framework dependencies
- Overkill for a simple proxy
- Better if we later need middleware, DI in request pipeline, etc.

## Tasks

### 1. ProxySettings config

Add `ProxySettings` to `AppSettings` and `appsettings.json`.

### 2. SpeechQueue service

Background service that processes a `Channel<string>`:
- Enqueue text from proxy responses
- Dequeue and run through TTS pipeline
- Cancel previous speech when new text arrives
- Handle lifecycle (start/stop)

### 3. ProxyService

HTTP proxy using `HttpListener`:
- Listen on configured port
- Route requests to appropriate handlers
- Forward headers and body
- Handle SSE streaming
- Extract assistant text
- Enqueue to SpeechQueue

### 4. CLI integration

`--daemon` flag in Program.cs:
- Start proxy
- Wire up services
- Block until shutdown

### 5. VS Code launch config

```
"Name": "TTS Proxy daemon"
```

### 6. Tests

- `ProxyServiceTests`:
  - `HandleChatCompletions_NonStreaming_ExtractsText` — verify text extraction
  - `HandleChatCompletions_Streaming_AccumulatesDeltas` — verify SSE parsing
  - `HandleModels_PassesThrough` — verify pass-through
- `SpeechQueueTests`:
  - `Enqueue_ProcessesText` — text goes through TTS
  - `Enqueue_CancelsPrevious` — new text cancels old speech
- Integration test with mock backend

## Acceptance Criteria

- [ ] `--daemon` starts proxy on configured port
- [ ] KiloCode can connect via `http://localhost:8080/v1`
- [ ] Chat completions work (streaming and non-streaming)
- [ ] Response text is spoken through speakers via TTS
- [ ] Streaming responses have no added latency
- [ ] Previous speech cancelled when new response arrives
- [ ] `/v1/models` passes through correctly
- [ ] `dotnet build` 0 warnings
- [ ] All tests pass

## KiloCode Configuration

Set in KiloCode's kilo.json or MCP config:

```json
{
  "provider": {
    "baseURL": "http://localhost:8080/v1",
    "apiKey": "<same openrouter key>"
  }
}
```

## Out of Scope

- Speech-to-text / transcription (Stage 5+)
- WebSocket support
- Request/response logging to file
- Rate limiting or auth on the proxy itself
