# Multi-Character TTS — Part 2: Architecture & Services

## Current Flow (Single Character)

```
LLM Response → [SentenceSplitter] → SpeechQueue (serial) → TTS API → ffplay
              (optional, only when ChunkedTts=true in streaming path)
```

## New Flow (Multi-Character)

```
LLM Response (full text, accumulated)
  → ModifierInjector (cheap fast LLM inserts speech modifiers)
  → CharacterParser (parse blocks, resolve [thought] tags, extract modifiers)
  → PersonaAllocator (assign personas to characters)
  → Parallel TTS tasks (one per segment, persona-specific instructions + modifiers)
  → Producer thread: synthesized streams added to ordered buffer
  → Consumer thread: reads index 0, pipes to PersistentAudioPipeline → ffplay
```

## Key Design Decisions

1. **Modifier injection runs first on full text** — The entire LLM response is sent to a cheap fast model to insert contextually appropriate `(modifier)` tags before any parsing. This ensures the modifier model has full conversational context.

2. **Parser runs on annotated text** — `CharacterParser` splits the annotated response into ordered `CharacterSegment` objects, each containing: index, character name, spoken text, persona key, modifier.

3. **Parallel TTS with producer-consumer playback** — Each segment is submitted to `ITtsService.SynthesizeToStreamAsync()` concurrently via `Task.WhenAll()`. Each task uses the assigned persona's voice, model, composed instructions (base + modifier suffix), and temperature. Synthesized audio streams are placed into a thread-safe ordered buffer as they complete. A separate consumer thread reads from index 0 (oldest first) and pipes each stream sequentially to `IPersistentAudioPipeline`. New items are appended to the end of the buffer. This decouples synthesis from playback and handles out-of-order completion.

4. **Graceful cancellation on persona/thought boundaries** — When cancellation is requested (new message arrives, user disconnect), playback does not abort mid-sentence. The current segment finishes playing, then the sequence terminates at the next persona or thought boundary. In-flight TTS tasks for remaining segments are cancelled.

5. **Integration point** — `ProxyService` is the integration point. After extracting text from the LLM response (both streaming and non-streaming paths), check for `[Name]` markers. If found, pass through `ModifierInjector` → `CharacterParser`. If no markers, fall through to existing single-character TTS path (no modifier injection).

6. **No audio post-processing** — All tonal differentiation (thoughts, emotions, register shifts) is handled via the TTS `instructions` parameter. The `PersistentAudioPipeline` remains unchanged.

7. **Streaming chunked TTS is flushed and re-processed for multi-character** — During SSE streaming, the `SentenceSplitter` may enqueue chunks that contain raw `[Name]` markers. After the stream ends, if `[Name]` markers are detected in the accumulated text, any previously enqueued chunked audio is flushed/cancelled and the full text is re-processed through the multi-character pipeline. The default persona handles any pre-marker text. No text is ever dropped.

## Data Models

### CharacterSegment

```csharp
public class CharacterSegment
{
    public int SequenceIndex { get; init; }
    public string CharacterName { get; init; } = "";
    public string Gender { get; init; } = "F";
    public string SpokenText { get; init; } = "";
    public string? PersonaKey { get; init; }
    public bool IsThought { get; init; }
    public string? Modifier { get; init; }
}
```

### PersonaUsageEntry

```csharp
public class PersonaUsageEntry
{
    public string? LastCharacter { get; set; }
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}
```

### MultiCharacterSettings

```csharp
public class MultiCharacterSettings
{
    public List<string> ModifierModels { get; set; } = new()
    {
        "z-ai/glm-4.5-air:free",
        "deepseek/deepseek-v4-flash"
    };
    public int ModifierTimeoutMs { get; set; } = 5000;
    public bool AutoInjectModifiers { get; set; } = true;
    public string ModifierSystemPrompt { get; set; } = ""; // default in Part 3
}
```

See Part 3 for full details on model selection, system prompt, and fallback chain.

## New Services

### CharacterParser

Parses annotated text (after modifier injection) into ordered segments. Stateless — no interface needed.

```
class CharacterParser
  Parse(string text) → List<CharacterSegment>

  - Regex: \[([^\]:]+)(?::([FM]))?\] to find character markers
  - Text before first [Name] marker → assigned to default persona (IsDefault: true)
  - Split text at markers
  - Extract [thought]...[/thought] as IsThought flag (text inside tags becomes spoken)
    - If [Name] appears before [/thought], auto-close thought at that boundary
  - Extract (modifier) tags immediately after character markers only (mid-text = literal)
  - Strip [Name:G] gender suffix, store as Gender field
  - Trim whitespace from each segment
  - Skip segments with empty spoken text
```

### PersonaAllocator (interface: IPersonaAllocator)

Assigns personas to characters with random selection, gender filtering, and 48-hour expiry. The default persona (`IsDefault: true`) is included in the allocation pool — it is assigned like any other persona. It serves as the fallback for unmarked text and single-character mode, but it is not excluded from random allocation.

```
interface IPersonaAllocator
  AllocateForCharacter(string name, string gender) → string? (persona key)
  Reset()
  GetPersona(string personaKey) → VoicePersona?

class PersonaAllocator : IPersonaAllocator
  constructor(IOptions<AppSettings>, ILogger<PersonaAllocator>)
     - Reads personas from AppSettings.Personas
     - Reads PersonaUsage from AppSettings.PersonaUsage
     - Reads currentModel from AppSettings.OpenRouter.TtsModel
     - Uses IOptions<AppSettings> (snapshot at construction, consistent with existing codebase pattern)
     - Note: PersonaAllocator reads config at construction. Runtime persona additions via SavePersona require a restart to take effect in PersonaAllocator (same as all other services). PersonaUsage state is managed in-memory and persisted to file independently.

   - Tracks character→persona mapping (ConcurrentDictionary)
  - File persistence uses a lock (SemaphoreSlim(1,1)) to serialize reads/writes of PersonaUsage to appsettings.json. Without this, concurrent allocations from parallel requests could interleave read-modify-write cycles and lose data (same race that exists in SavePersona today, but PersonaAllocator is a singleton so a single lock suffices).
  - Filters personas by model + gender (default persona IS in the pool)
  - Picks randomly from available pool
  - Respects 48-hour reuse window: stale personas (>48h) return to available pool
  - Updates LastUsedUtc on each new allocation
  - Loads/saves PersonaUsage state to appsettings.json
  - Reset(): clears all character→persona mappings, persists immediately
  - GetPersona(): returns VoicePersona by key for ParallelTtsPlayer to resolve voice/instructions
```

### ModifierInjector (interface: IModifierInjector)

Sends full raw text to a cheap fast LLM to insert emotion modifiers before parsing. Uses a dedicated named HttpClient with timeout configured from `MultiCharacterSettings.ModifierTimeoutMs`.

```
interface IModifierInjector
  InjectModifiersAsync(string text, CancellationToken ct) → Task<string>

class ModifierInjector : IModifierInjector
  constructor(IHttpClientFactory, IOptions<AppSettings>, ILogger<ModifierInjector>)
    - Uses named HttpClient "ModifierInjector" (timeout = ModifierTimeoutMs)
    - Falls back to "OpenRouter" client if named client not available
    - System prompt: see Part 3

  InjectModifiersAsync:
    - If AutoInjectModifiers is false → return original text immediately
    - Iterates ModifierModels list in order, trying each until success
    - Post-injection validation: count `[Name]` markers in output vs input. If the modifier LLM corrupted the markers (added/removed any), log a warning and return the original unmodified text. This guards against LLMs that strip or rewrite character markers despite the system prompt.
    - Returns annotated text with (modifier) tags inserted
    - Returns original text unchanged if all models fail/timeout/429
    - Preserves all [Name], [Name:G], and [thought]...[/thought] tags
    - Follows LlmTextTransformer pattern for HTTP call and error handling
    - See Part 3 for full system prompt, modifier mapping, and fallback chain details
```

### ParallelTtsPlayer

Orchestrates parallel TTS synthesis and sequential playback via a producer-consumer pattern. Bypasses `SpeechQueue` entirely — manages its own synthesis and piping. Stateless helper — no interface needed.

```
class ParallelTtsPlayer
  constructor(ITtsService, IPersistentAudioPipeline?, IAudioPlayer, IOptions<AppSettings>, ILogger)
  PlaySegmentsAsync(List<CharacterSegment> segments, CancellationToken ct) → Task

  Producer-consumer architecture:
    - Producer: launches all TTS synthesis tasks concurrently (Task.WhenAll)
      - For each segment:
        1. Resolve persona from PersonaAllocator.GetPersona(segment.PersonaKey)
        2. Compose instructions:
           - Base = persona.OpenAiInstructions
           - If IsThought: append persona.ThoughtInstructions
           - If Modifier: append modifier→instruction suffix mapping (see Part 3)
        3. Temperature = persona.Temperature ± Random.Shared.NextDouble(-0.05, 0.05) + (0.1 if IsThought)
        4. Call ITtsService.SynthesizeToStreamAsync() with composed params
      - Each completed task adds result to thread-safe ordered buffer:
        buffer[segment.SequenceIndex] = (audioStream, format)

    - Consumer thread: loops reading from buffer starting at index 0
      - Waits for buffer[0] to be available (blocking wait with cancellation check)
      - Pipes audioStream to IPersistentAudioPipeline.PipeAsync() (or IAudioPlayer if no pipeline)
      - Removes buffer[0], advances to next index
      - If ct is cancelled: finish current segment, then stop before next segment

  Cancellation behavior:
    - Cancellation is checked between segments (not mid-playback of a single segment)
    - Current segment plays to completion
    - All remaining in-flight TTS tasks are cancelled via shared CancellationToken
    - Buffer is cleared
```

## Integration Points

### ProxyService.cs — StreamWithTtsAsync

During the SSE stream, chunked TTS runs normally (SentenceSplitter enqueues sentences). After the stream ends and `textBuffer` is fully accumulated:

```
After stream ends, text = textBuffer.ToString().Trim():

  if text is empty:
    done

  if text contains [Name] markers (regex check):
    _speechQueue.FlushAndCancel()   // flush any chunked audio already enqueued during stream
    annotated = await ModifierInjector.InjectModifiersAsync(text)
    segments = CharacterParser.Parse(annotated)
    // Allocate personas for each unique character
    foreach unique character in segments:
      personaKey = PersonaAllocator.AllocateForCharacter(name, gender)
      set PersonaKey on all segments for that character
    await ParallelTtsPlayer.PlaySegmentsAsync(segments)
  else:
    // existing single-character behavior unchanged
    enqueue remaining text via splitter or full text
```

### ProxyService.cs — NonStreamWithTtsAsync

Same integration, after extracting text from the non-streaming response JSON:

```
After extracting text from response body:

  if text contains [Name] markers:
    annotated = await ModifierInjector.InjectModifiersAsync(text)
    segments = CharacterParser.Parse(annotated)
    // Allocate personas for each unique character
    foreach unique character in segments:
      personaKey = PersonaAllocator.AllocateForCharacter(name, gender)
      set PersonaKey on all segments for that character
    await ParallelTtsPlayer.PlaySegmentsAsync(segments)
  else:
    _speechQueue.Enqueue(text)
```

### ProxyService.cs — FlushAndCancel interaction

When `HandleChatCompletionsCore` calls `_speechQueue.FlushAndCancel()` at the start of a new request (line 109), it must also cancel any running `ParallelTtsPlayer.PlaySegmentsAsync` operation.

**Concrete mechanism:**

```
In ProxyService, add two fields:
  private CancellationTokenSource? _activePlaybackCts;
  private Task? _activePlaybackTask;

In HandleChatCompletionsCore (at the FlushAndCancel call site):
  if (_activePlaybackCts != null)
  {
      _activePlaybackCts.Cancel();
      // Await with timeout to ensure old playback finishes before starting new one
      if (_activePlaybackTask != null)
          await Task.WhenAny(_activePlaybackTask, Task.Delay(2000));
      _activePlaybackCts.Dispose();
      _activePlaybackCts = null;
      _activePlaybackTask = null;
  }
  _speechQueue.FlushAndCancel();

When launching PlaySegmentsAsync (in StreamWithTtsAsync / NonStreamWithTtsAsync):
  _activePlaybackCts = new CancellationTokenSource();
  var ct = _activePlaybackCts.Token;
  _activePlaybackTask = ParallelTtsPlayer.PlaySegmentsAsync(segments, ct);
  await _activePlaybackTask;   // or fire-and-forget if streaming path
```

This ensures:
- Old playback is cancelled and awaited (with a 2s safety timeout) before new playback begins
- No two `PlaySegmentsAsync` calls pipe to `IPersistentAudioPipeline` simultaneously
- The `CancellationToken` propagates to both the consumer thread (stops between segments) and producer tasks (cancelled in-flight)

### `/reset` Command

When a user sends `/reset` as a chat message through the proxy, `ProxyService` intercepts it before forwarding to the backend. Detection: parse the request body JSON, find the last `role: "user"` message, check if its content is exactly `/reset`.

```
In HandleChatCompletionsCore, after reading request body:

  var lastUserMessage = ExtractLastUserMessage(requestBody)
  if lastUserMessage == "/reset":
    PersonaAllocator.Reset()
    // Return empty streaming response (don't forward to backend)
    context.Response.ContentType = "text/event-stream"
    context.Response.Headers.CacheControl = "no-cache"
    await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"},\"index\":0}]}\n\n")
    await context.Response.WriteAsync("data: [DONE]\n")
    await context.Response.Body.FlushAsync()
    return
```

## Service Registration (Program.cs)

```csharp
// Multi-character TTS services
services.AddSingleton<IPersonaAllocator, PersonaAllocator>();
services.AddSingleton<IModifierInjector, ModifierInjector>();
services.AddSingleton<ParallelTtsPlayer>();
services.AddSingleton<CharacterParser>();

// Named HttpClient for modifier injection (shorter timeout than OpenRouter default)
services.AddHttpClient("ModifierInjector", client =>
{
    client.Timeout = TimeSpan.FromMilliseconds(
        host.Services.GetRequiredService<IOptions<AppSettings>>().Value
            .MultiCharacter.ModifierTimeoutMs);
});
```

## Streaming Consideration

In the current streaming path, the full text is accumulated in `textBuffer` and the final TTS enqueue happens after the stream ends. During streaming, the `SentenceSplitter` may enqueue chunks that contain raw `[Name]` markers as spoken text.

For multi-character responses, after the stream ends and `[Name]` markers are detected in the accumulated text, `FlushAndCancel()` is called to discard any chunked audio that was prematurely enqueued. The full accumulated text is then processed through the multi-character pipeline (`ModifierInjector` → `CharacterParser` → `ParallelTtsPlayer`). Text before the first `[Name]` marker is assigned to the default persona, so no text is ever dropped.

For earlier TTS dispatch (chunked per character within the stream), that would require detecting character boundaries mid-stream, which is a future enhancement. For now, parse after full text is accumulated.
