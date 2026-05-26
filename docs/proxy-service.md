# Proxy Service

**Source:** `src/benow-conversation/Services/ProxyService.cs`

## Overview

An OpenAI-compatible HTTP proxy that intercepts chat completion requests, forwards them to a backend LLM API (OpenRouter), streams responses back to the client, and simultaneously extracts text content for automatic TTS playback via the `SpeechQueue`.

## Architecture

The proxy creates a minimal ASP.NET Core web application at runtime:

```
Client → ProxyService → OpenRouter API
                ↓
         SpeechQueue → TtsService → AudioPlayer → Speakers
```

## Endpoints

| Route | Handler |
|---|---|
| `/v1/chat/completions` | `HandleChatCompletions` -- intercept for TTS |
| `/chat/completions` | `HandleChatCompletions` |
| `/v1/models` | `FullPassThrough` -- transparent proxy |
| `/models` | `FullPassThrough` |
| `/v1` | `HandleChatCompletions` |
| `/*` (fallback) | `FullPassThrough` |

## CORS

All responses include permissive CORS headers (`Access-Control-Allow-Origin: *`). `OPTIONS` requests return `204 No Content`.

## Request Flow (Chat Completions)

1. **Read body** -- The full request body is buffered
2. **Model injection** -- If the request has no `model` field and `Proxy.BackendModel` is configured, the model is injected via JSON manipulation
3. **Header forwarding** -- All request headers (except `content-type`, `content-length`, `host`) are copied to the backend request. If no `Authorization` header is present, the configured API key is injected
4. **Backend call** -- The request is forwarded to `{BackendUrl}/chat/completions`
5. **Response handling**:
   - **Streaming** (`"stream": true`) -- SSE chunks are relayed in real-time. Content deltas are buffered and the complete text is enqueued for TTS when the stream ends
   - **Non-streaming** -- The full JSON response is relayed, then the `choices[0].message.content` is extracted and enqueued for TTS

## Full Pass-Through

For non-chat endpoints (models listing, etc.), requests are proxied transparently with header forwarding and response streaming. This allows the proxy to act as a drop-in replacement for the OpenRouter API base URL.

## Model Injection

`InjectModel()` parses the JSON body, replaces or adds the `model` field, and re-serializes. If parsing fails, the original body is passed through unchanged.

## Header Management

- **Request headers**: Copied from client to backend, excluding hop-by-hop headers
- **Response headers**: Copied from backend to client, excluding transfer-encoding and CORS headers (which are set by the proxy itself)

## Error Handling

- `OperationCanceledException` -- Logged at debug level (client disconnect)
- Other exceptions -- Logged at error level, returns 502 with a JSON error body
