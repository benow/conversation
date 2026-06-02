# Latency Optimization Plan

## Problem

After dictating text via STT and submitting (Enter), the user experiences a significant delay before hearing the spoken response. The current pipeline is **fully sequential**:

```
STT complete → text paste → Enter → [LLM generates full response] → [TTS synthesizes full audio] → [buffer entire audio] → [spawn ffplay] → speakers
```

Every stage blocks the next. For a 20-second LLM response with 5 seconds of TTS generation, the user waits **25+ seconds** before hearing a single word. Additional delays come from:
- Per-sentence ffplay process spawn (~100-200ms each)
- Device contention when multiple ffplay instances compete for the audio output
- Double-buffering: TtsService buffers the HTTP response into a byte array, then SpeechQueue copies it into a MemoryStream before piping to ffplay

## Target Pipeline

```
STT complete → text paste → Enter → [LLM streams tokens...]
                                          ↓ first sentence detected (~3-5s)
                                     [TTS API call starts for sentence 1]
                                          ↓ first audio bytes arrive (~1-2s)
                                     [persistent ffplay starts playing sentence 1]
                                          ↓ while sentence 1 plays...
                                     [LLM still streaming sentence 3+]
                                     [TTS API call for sentence 2 completes]
                                     [sentence 2 audio piped into same ffplay — no gap]
```

Time-to-first-audio drops from **LLM total + TTS total** to **~1 sentence of LLM + TTS for 1 sentence**.

---

## Optimization 1: Sentence-Level Incremental TTS

**Impact: Highest — eliminates the biggest sequential bottleneck.**

### Current Behavior

`ProxyService.cs:240-298` — `StreamWithTtsAsync` accumulates every streaming token from the LLM into a single `StringBuilder` (`textBuffer`, line 252). Only after `[DONE]` is received (line 265) is the full text enqueued for TTS via `_speechQueue.Enqueue(text)` (line 297).

### New Behavior

As SSE deltas arrive, detect sentence boundaries and enqueue each complete sentence for TTS **immediately**, while the LLM is still generating subsequent sentences.

### New File: `src/benow-conversation/Services/SentenceSplitter.cs`

```csharp
using System.Text;

namespace benow_conversation.Services;

public class SentenceSplitter
{
    private readonly StringBuilder _buffer = new();
    private readonly int _minSentenceLength;
    private readonly Queue<string> _completed = new();
    private bool _inCodeFence;

    public SentenceSplitter(int minSentenceLength = 20)
    {
        _minSentenceLength = minSentenceLength;
    }

    public void Append(string fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return;
        _buffer.Append(fragment);
        Scan();
    }

    public bool TryDequeue(out string sentence)
    {
        return _completed.TryDequeue(out sentence!);
    }

    public string? Flush()
    {
        var remaining = _buffer.ToString().Trim();
        _buffer.Clear();
        if (string.IsNullOrWhiteSpace(remaining))
            return null;
        if (remaining.Length < _minSentenceLength / 2)
            return null;
        return remaining;
    }

    private void Scan()
    {
        while (true)
        {
            var text = _buffer.ToString();
            if (text.Length == 0) break;

            // Track fenced code blocks (``` markers)
            int fenceIdx;
            while ((fenceIdx = text.IndexOf("```", StringComparison.Ordinal)) >= 0)
            {
                _inCodeFence = !_inCodeFence;
                _buffer.Remove(0, fenceIdx + 3);
                text = _buffer.ToString();
            }

            if (_inCodeFence) break;
            if (text.Length < _minSentenceLength) break;

            // Find sentence boundary: . ! ? followed by whitespace or end
            var splitAt = -1;
            for (var i = 0; i < text.Length - 1; i++)
            {
                var c = text[i];
                if (c != '.' && c != '!' && c != '?') continue;
                var next = text[i + 1];
                if (next == ' ' || next == '\n' || next == '\r' || next == '\t')
                {
                    var candidate = text[..(i + 1)].Trim();
                    if (candidate.Length >= _minSentenceLength)
                    {
                        splitAt = i + 1;
                        break;
                    }
                }
            }

            // Also split on double-newline (paragraph break) regardless of punctuation
            if (splitAt < 0)
            {
                var dblNl = text.IndexOf("\n\n", StringComparison.Ordinal);
                if (dblNl >= 0 && text[..dblNl].Trim().Length >= _minSentenceLength)
                    splitAt = dblNl + 2;
            }

            if (splitAt < 0) break;

            var sentence = text[..splitAt].Trim();
            _buffer.Remove(0, splitAt);

            if (!string.IsNullOrWhiteSpace(sentence) && sentence.Length >= _minSentenceLength / 2)
                _completed.Enqueue(sentence);
        }
    }
}
```

**Key design decisions:**

- **`_minSentenceLength` (default 20)**: Prevents splitting on abbreviations like "Dr.", "vs.", "e.g.", "i.e." which are always shorter than 20 chars. The final `Flush()` uses half this threshold since the last fragment may legitimately be short.
- **Code fence tracking**: Toggles `_inCodeFence` on each `` ``` `` occurrence. While inside a code fence, no splitting occurs. The entire code block is emitted as one chunk when the fence closes.
- **Paragraph breaks**: Double newlines (`\n\n`) are treated as sentence boundaries even without terminal punctuation — handles list items, markdown paragraphs.
- **`Scan()` loop**: Repeatedly extracts sentences from the buffer until no more boundaries are found. This handles the case where a single `Append()` call delivers multiple sentences at once.

### Modified: `ProxyService.cs` — `StreamWithTtsAsync` (lines 240-298)

Replace the `StringBuilder textBuffer` with a `SentenceSplitter`. The method signature and all SSE relay logic stay the same — only the text accumulation and enqueue logic changes.

**Before** (lines 252-298):
```csharp
var textBuffer = new StringBuilder();
// ... in loop:
    textBuffer.Append(content);
// ... after loop:
var text = textBuffer.ToString().Trim();
if (!string.IsNullOrEmpty(text))
    _speechQueue.Enqueue(text);
```

**After**:
```csharp
var splitter = new SentenceSplitter(_settings.Proxy.MinSentenceLength);
// ... in loop, after extracting content delta:
    splitter.Append(content);
    while (splitter.TryDequeue(out var sentence))
        _speechQueue.Enqueue(sentence, cancelCurrent: false);
// ... after loop (after [DONE] break):
var remaining = splitter.Flush();
if (remaining != null)
    _speechQueue.Enqueue(remaining, cancelCurrent: false);
```

### Modified: `ProxyService.cs` — `NonStreamWithTtsAsync` (lines 301-336)

This path handles non-streaming LLM responses. It currently calls `_speechQueue.Enqueue(text)` (line 327). Change to:

```csharp
_speechQueue.Enqueue(text, cancelCurrent: true);
```

This preserves the existing "new response cancels old" behavior for non-streaming responses.

### Modified: `SpeechQueue.cs` — Interface and Enqueue

**Interface change** (`ISpeechQueue`, line 8-13):

```csharp
public interface ISpeechQueue
{
    void Enqueue(string text, bool cancelCurrent = true);
    void FlushAndCancel();
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
```

**Implementation** — replace `Enqueue` method (lines 62-68) and add `FlushAndCancel`:

```csharp
public void Enqueue(string text, bool cancelCurrent = true)
{
    if (string.IsNullOrWhiteSpace(text)) return;
    _logger.LogInformation("Enqueueing speech ({Length} chars, cancel={Cancel}): {Preview}...",
        text.Length, cancelCurrent, text.Length > 80 ? text[..80] : text);
    if (cancelCurrent)
    {
        try { _currentPlaybackCts?.Cancel(); } catch (ObjectDisposedException) { }
    }
    _channel.Writer.TryWrite(text);
}

public void FlushAndCancel()
{
    // Drain any pending sentences from the channel
    while (_channel.Reader.TryRead(out _)) { }
    try { _currentPlaybackCts?.Cancel(); } catch (ObjectDisposedException) { }
}
```

**Why this matters:** When `cancelCurrent: false` (incremental sentences from the same LLM response), the current playback is NOT cancelled. Sentences queue in the channel and play sequentially. When `cancelCurrent: true` (first sentence of a new user message, or non-streaming response), the current playback IS cancelled — the user hears the new response immediately.

`FlushAndCancel()` is called from `ProxyService.HandleChatCompletionsCore` when a new request arrives (line 107), to discard any queued sentences from a previous response that hasn't finished playing.

### Modified: `ProxyService.cs` — `HandleChatCompletionsCore` (line 107)

Add a call to flush previous response at the start of each new request:

```csharp
private async Task HandleChatCompletionsCore(HttpContext context)
{
    // Cancel any in-progress TTS from previous response
    _speechQueue.FlushAndCancel();

    var requestBody = await ReadBodyAsync(context);
    // ... rest unchanged ...
}
```

This ensures that when the user submits a new message, any queued or playing audio from the previous response is immediately stopped.

---

## Optimization 2: Persistent ffplay Process + Live TTS Streaming

**Impact: Highest — eliminates buffering latency, device contention, and per-sentence process overhead.**

### Current Behavior — Three Sequential Bottlenecks

**Bottleneck A — TTS HTTP response fully buffered** (`TtsService.cs:214`):
```csharp
audioBytes = await ReadAudioResponseAsync(response); // ReadAsByteArrayAsync internally
return (new MemoryStream(audioBytes), apiFormat);
```
The entire MP3 is downloaded into memory before any audio plays.

**Bottleneck B — Double copy in SpeechQueue** (`SpeechQueue.cs:104-106`):
```csharp
using var ms = new MemoryStream();
await audioStream.CopyToAsync(ms, cts.Token); // copies MemoryStream to MemoryStream
ms.Position = 0;
```
An unnecessary copy from one MemoryStream to another.

**Bottleneck C — New ffplay process per sentence** (`AudioPlayer.cs:111-112`):
```csharp
using var process = new Process { StartInfo = psi };
process.Start();
```
Each sentence spawns a new ffplay process (~100-200ms startup), acquires the audio device, plays, exits, releases the device. The next sentence repeats this cycle, creating audible gaps.

### New Behavior

A single persistent ffplay process receives all audio through its stdin pipe. TTS HTTP responses stream directly from OpenRouter into ffplay — no buffering, no intermediate copies, no process spawn.

### New File: `src/benow-conversation/Services/PersistentAudioPipeline.cs`

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace benow_conversation.Services;

public interface IPersistentAudioPipeline : IAsyncDisposable
{
    Task PipeAsync(Stream source, CancellationToken ct);
    Task InterruptAsync();
    Task StartAsync(CancellationToken ct);
}

public class PersistentAudioPipeline : IPersistentAudioPipeline
{
    private readonly ILogger<PersistentAudioPipeline> _logger;
    private readonly string _ffplayPath;
    private readonly string _format;
    private readonly int? _volume;
    private readonly string? _device;
    private Process? _process;
    private Stream? _stdin;
    private SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public PersistentAudioPipeline(
        ILogger<PersistentAudioPipeline> logger,
        string ffplayPath,
        string format = "mp3",
        int? volume = null,
        string? device = null)
    {
        _logger = logger;
        _ffplayPath = ffplayPath;
        _format = format;
        _volume = volume;
        _device = device;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await EnsureProcessAsync(ct);
    }

    public async Task PipeAsync(Stream source, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureProcessAsync(ct);
            await source.CopyToAsync(_stdin!, ct);
            // Do NOT close stdin — keep the pipe open for the next sentence
            // Flush to push buffered bytes to ffplay immediately
            await _stdin!.FlushAsync(ct);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Stdin pipe broken — restarting ffplay");
            await RestartProcessAsync(ct);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error piping audio to ffplay");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task InterruptAsync()
    {
        await _lock.WaitAsync();
        try
        {
            KillProcess();
            _stdin = null;
            _process = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        KillProcess();
        _lock.Dispose();
        await ValueTask.CompletedTask;
    }

    private async Task EnsureProcessAsync(CancellationToken ct)
    {
        if (_process != null && !_process.HasExited) return;
        _logger.LogInformation("Starting persistent ffplay (format={Format})", _format);
        StartProcess();
        // Brief delay to let ffplay initialize and open the audio device
        await Task.Delay(50, ct);
    }

    private void StartProcess()
    {
        var args = BuildArgs();
        var psi = new ProcessStartInfo
        {
            FileName = _ffplayPath,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffplay");
        _stdin = _process.StandardInput.BaseStream;

        // Drain stderr in background to prevent blocking
        _ = Task.Run(() => _process.StandardError.ReadToEndAsync());
    }

    private async Task RestartProcessAsync(CancellationToken ct)
    {
        KillProcess();
        _stdin = null;
        _process = null;
        await EnsureProcessAsync(ct);
    }

    private void KillProcess()
    {
        if (_process == null || _process.HasExited) return;
        try { _stdin?.Close(); } catch { }
        try { _process.Kill(entireProcessTree: true); } catch { }
        try { _process.WaitForExit(2000); } catch { }
        _logger.LogInformation("ffplay process terminated");
    }

    private string BuildArgs()
    {
        var args = "-nodisp -autoexit -loglevel quiet";

        if (_format.Equals("pcm", StringComparison.OrdinalIgnoreCase))
            args += " -f s16le -ar 24000";
        else if (_format.Equals("mp3", StringComparison.OrdinalIgnoreCase))
            args += " -f mp3";

        // Critical for low-latency: minimal probe/analyze so ffplay starts
        // playing after reading just a few bytes, not the default 5MB
        args += " -probesize 32 -analyzeduration 0";

        if (_volume.HasValue)
            args += $" -volume {Math.Clamp(_volume.Value, 0, 100)}";
        if (!string.IsNullOrWhiteSpace(_device))
            args += $" -audiodevice \"{_device}\"";

        args += " -i pipe:0";
        return args;
    }
}
```

**Key design decisions:**

- **`_lock` (SemaphoreSlim)**: Serializes writes to stdin. Only one `PipeAsync` call writes at a time. This preserves sentence ordering — sentence N+1 waits for sentence N to finish writing before writing its bytes.
- **`-probesize 32 -analyzeduration 0`**: These ffplay flags are critical. Without them, ffplay buffers up to 5MB of data (~30 seconds of MP3) before starting playback. With them, ffplay starts playing after reading just 32 bytes.
- **`-autoexit`**: ffplay exits when stdin closes. We keep stdin open between sentences, so ffplay stays alive. It only exits when we explicitly kill it or the daemon shuts down.
- **`PipeAsync` does NOT close stdin**: After copying a sentence's audio to stdin, the pipe stays open. The next sentence writes to the same pipe. MP3 frames are self-delimiting, so ffplay decodes and plays each frame as it arrives.
- **`InterruptAsync`**: Kills ffplay immediately. Used when a new user message arrives to stop the current audio output. The new response starts a fresh ffplay process via `EnsureProcessAsync` on the next `PipeAsync` call.
- **Lazy process creation**: `EnsureProcessAsync` starts ffplay on first use. If ffplay crashes mid-stream, `PipeAsync` catches the `IOException`, restarts ffplay, and re-throws (the current sentence is lost; the queue moves to the next sentence).

### New Method: `TtsService.SynthesizeLiveStreamAsync`

Add to `ITtsService.cs`:
```csharp
Task<Stream> SynthesizeLiveStreamAsync(string text, string? voice = null, string? instructions = null, double? temperature = null, int? seed = null, string? model = null);
```

Add to `TtsService.cs`:
```csharp
public async Task<Stream> SynthesizeLiveStreamAsync(
    string text,
    string? voice = null,
    string? instructions = null,
    double? temperature = null,
    int? seed = null,
    string? model = null)
{
    ValidateApiKey();

    var resolvedVoice = voice ?? "alloy";
    var resolvedModel = model ?? _settings.OpenRouter.TtsModel;
    var apiFormat = _settings.Audio.OutputFormat;

    // Use cached format if available (required for streaming — can't retry mid-stream)
    if (_modelFormats.TryGetValue(resolvedModel, out var cachedFormat))
        apiFormat = cachedFormat;

    // If format is not cached, fall back to buffered path to discover it
    if (!_modelFormats.ContainsKey(resolvedModel))
    {
        _logger.LogInformation("Format not cached for {Model} — using buffered path first", resolvedModel);
        var (ms, fmt) = await SynthesizeToStreamAsync(text, voice, instructions, temperature, seed, model);
        ms.Position = 0;
        return ms;
    }

    var request = new TtsRequest
    {
        Model = resolvedModel,
        Input = text,
        Voice = resolvedVoice,
        ResponseFormat = apiFormat,
        Temperature = temperature,
        Seed = seed
    };

    if (!string.IsNullOrWhiteSpace(instructions) && resolvedModel.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
    {
        request.Provider = new ProviderOptions
        {
            Options = new Dictionary<string, Dictionary<string, string>>
            {
                ["openai"] = new() { ["instructions"] = instructions }
            }
        };
    }

    _logger.LogInformation("Live stream synthesis: model={Model}, voice={Voice}, format={Format}, textLength={Length}",
        request.Model, request.Voice, apiFormat, text.Length);

    var client = _httpClientFactory.CreateClient("OpenRouter");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.OpenRouter.ApiKey);

    // Send with ResponseHeadersRead — returns as soon as headers arrive, before body downloads
    var response = await client.SendAsync(
        new HttpRequestMessage(HttpMethod.Post, $"{_settings.OpenRouter.BaseUrl}/audio/speech")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        },
        HttpCompletionOption.ResponseHeadersRead);

    // Validate before returning the stream
    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
    {
        response.Dispose();
        throw new InvalidOperationException("OpenRouter API key is invalid or unauthorized.");
    }
    if (response.StatusCode == HttpStatusCode.TooManyRequests)
    {
        response.Dispose();
        throw new InvalidOperationException("OpenRouter rate limit exceeded.");
    }
    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync();
        response.Dispose();
        throw new InvalidOperationException($"TTS API failed (HTTP {(int)response.StatusCode}): {TryExtractErrorMessage(errorBody)}");
    }

    var contentType = response.Content.Headers.ContentType?.MediaType;
    if (contentType != null && contentType.Contains("json"))
    {
        var errorBody = await response.Content.ReadAsStringAsync();
        response.Dispose();
        throw new InvalidOperationException($"TTS API error: {TryExtractErrorMessage(errorBody)}");
    }

    _logger.LogInformation("Live stream response headers received (HTTP {Status})", (int)response.StatusCode);

    // Return the raw HTTP stream — caller is responsible for disposing
    // (disposing the stream disposes the HttpResponseMessage)
    return await response.Content.ReadAsStreamAsync();
}
```

**Key difference from `SynthesizeToStreamAsync`:** Uses `HttpCompletionOption.ResponseHeadersRead` and returns `response.Content.ReadAsStreamAsync()` directly — no `ReadAsByteArrayAsync`. The caller gets the stream as bytes arrive from OpenRouter, not after the full download completes.

**Format caching**: The first call for a model goes through `SynthesizeToStreamAsync` (buffered) to discover the correct format and cache it. All subsequent calls use the live streaming path. This is safe because the model doesn't change during a session.

### Modified: `SpeechQueue.cs` — Constructor and ProcessQueueAsync

**Constructor** — add `IPersistentAudioPipeline` dependency:

```csharp
public class SpeechQueue : ISpeechQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private readonly ITtsService _ttsService;
    private readonly IPersistentAudioPipeline? _pipeline;
    private readonly AppSettings _settings;
    private readonly ILogger<SpeechQueue> _logger;
    private readonly string _ttsModel;
    private readonly string _ttsVoice;
    private readonly string? _ttsInstructions;
    private readonly double? _ttsTemperature;
    private readonly int? _ttsSeed;
    private CancellationTokenSource? _currentPlaybackCts;
    private Task? _processingTask;

    public SpeechQueue(
        ITtsService ttsService,
        IAudioPlayer audioPlayer,
        IPersistentAudioPipeline? pipeline,
        IOptions<AppSettings> settings,
        ILogger<SpeechQueue> logger)
    {
        _ttsService = ttsService;
        _pipeline = pipeline;
        _settings = settings.Value;
        _logger = logger;

        // ... persona resolution unchanged ...
    }
```

Note: `IAudioPlayer` stays in the constructor for backward compatibility (CLI mode still uses per-call ffplay), but the daemon path uses `_pipeline` instead.

**ProcessQueueAsync** — replace lines 83-127:

```csharp
private async Task ProcessQueueAsync(CancellationToken cancellationToken)
{
    await foreach (var text in _channel.Reader.ReadAllAsync(cancellationToken))
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentPlaybackCts = cts;

        try
        {
            _logger.LogInformation("Speaking ({Length} chars): {Preview}...",
                text.Length, text.Length > 60 ? text[..60] : text);

            if (_pipeline != null)
            {
                // Streaming path: TTS HTTP response → persistent ffplay stdin
                var audioStream = await _ttsService.SynthesizeLiveStreamAsync(
                    text, _ttsVoice, _ttsInstructions, _ttsTemperature, _ttsSeed, _ttsModel);
                await using var _ = audioStream;
                await _pipeline.PipeAsync(audioStream, cts.Token);
            }
            else
            {
                // Fallback path: buffered synthesis + per-call ffplay (CLI mode)
                var (audioStream, format) = await _ttsService.SynthesizeToStreamAsync(
                    text, _ttsVoice, _ttsInstructions, _ttsTemperature, _ttsSeed, _ttsModel);
                using var ms = new MemoryStream();
                await audioStream.CopyToAsync(ms, cts.Token);
                ms.Position = 0;
                // This path uses AudioPlayer — kept for backward compat
                var audioPlayer = ...; // resolved from DI if needed
                await audioPlayer.PlayStreamAsync(ms, format, cancellationToken: cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Speech cancelled — new speech incoming");
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TTS playback failed: {Error}", ex.Message);
        }
        finally
        {
            cts.Dispose();
        }
    }
}
```

Wait — the `else` branch needs `IAudioPlayer`. Since `SpeechQueue` already receives it in the constructor, we keep it as a field. The full constructor stores both `_pipeline` and `_audioPlayer`:

```csharp
private readonly ITtsService _ttsService;
private readonly IAudioPlayer _audioPlayer;
private readonly IPersistentAudioPipeline? _pipeline;
```

Then the fallback path uses `_audioPlayer` directly (unchanged from current code). When `_pipeline` is not null (daemon mode), the streaming path is used instead.

### Modified: `SpeechQueue.cs` — `FlushAndCancel` Implementation

```csharp
public void FlushAndCancel()
{
    _logger.LogInformation("Flushing speech queue and cancelling current playback");
    while (_channel.Reader.TryRead(out _)) { }
    try { _currentPlaybackCts?.Cancel(); } catch (ObjectDisposedException) { }
    _pipeline?.InterruptAsync().GetAwaiter().GetResult();
}
```

This drains all pending sentences from the channel, cancels the current TTS HTTP stream (via the linked CTS), and kills the ffplay process. The next `PipeAsync` call will start a fresh ffplay.

### Modified: `Program.cs` — DI Registration

Register `PersistentAudioPipeline` as a singleton, but only when in daemon mode. However, since DI is configured before command-line args are fully parsed (lines 39-76), we register it conditionally:

```csharp
// In ConfigureServices block (around line 56):
services.AddSingleton<ISpeechQueue, SpeechQueue>();

// Register PersistentAudioPipeline — it will only be used when running as daemon
// The constructor requires ILogger, ffplayPath, format, volume, device
// We use a factory to resolve these from config
services.AddSingleton<IPersistentAudioPipeline>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<PersistentAudioPipeline>>();
    var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    var audioPlayer = sp.GetRequiredService<IAudioPlayer>();

    // Find ffplay path — reuse AudioPlayer's cached path
    if (!audioPlayer.IsAvailable)
        return null!; // ffplay not available, pipeline won't work

    var persona = ResolvePersonaForPipeline(settings);

    return new PersistentAudioPipeline(
        logger,
        FindFfplayPath() ?? "ffplay",
        format: settings.Audio.OutputFormat,
        volume: ResolveVolume(settings),
        device: ResolveDevice(settings));
});
```

But there's a problem: we don't know at DI registration time whether daemon mode will be used. Two options:

**Option A (recommended)**: Always register the pipeline. If ffplay is not available or we're not in daemon mode, it's never used. `SpeechQueue` checks `_pipeline != null` before using it.

**Option B**: Lazy initialization — `SpeechQueue` creates the pipeline on first use if in daemon mode.

Going with **Option A**. The pipeline is cheap when not used — it's just an object in memory. The ffplay process doesn't start until `StartAsync` is called.

Add to the daemon startup path (line 331):
```csharp
// In daemon mode startup (lines 318-346):
Log.Information("Starting proxy daemon...");
var pipeline = host.Services.GetRequiredService<IPersistentAudioPipeline>();
await pipeline.StartAsync(cts.Token);
await speechQueue.StartAsync(cts.Token);
```

Add cleanup on shutdown (before line 342):
```csharp
await speechQueue.StopAsync(CancellationToken.None);
if (pipeline is IAsyncDisposable disposablePipeline)
    await disposablePipeline.DisposeAsync();
```

Same for STT+daemon mode (lines 268-298).

### Fallback Path — `SpeechQueue` without Pipeline

When `IPersistentAudioPipeline` is null (ffplay not available) or when `PersistentPipeline: false` in config, `SpeechQueue` falls back to the current behavior: buffered `SynthesizeToStreamAsync` + per-call `AudioPlayer.PlayStreamAsync`. This ensures backward compatibility for CLI mode.

---

## Optimization 3: Skip LLM Transcript Cleanup

**Status: DONE**

### Files Changed

- `Services/Stt/NullTextTransformer.cs` — new file, pass-through `ITextTransformer`
- `Program.cs:913` — added `"none"` case to the DI switch

### Configuration

Set `"Transformer": "none"` in `appsettings.json` to skip the LLM cleanup pass after Groq transcription. Already applied.

---

## Optimization 4: Configuration

**Impact: Medium — gives the user control over the latency/quality tradeoff.**

### Modified: `Configuration/AppSettings.cs` — `ProxySettings` (line 65-75)

Add new properties:

```csharp
public class ProxySettings
{
    public string BindAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8080;
    public string BackendUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string BackendModel { get; set; } = "";
    public string TtsPersona { get; set; } = "";
    public string TtsModel { get; set; } = "";
    public string TtsVoice { get; set; } = "";
    public bool LogBodies { get; set; }

    // New latency optimization settings:
    public bool ChunkedTts { get; set; } = true;           // Enable sentence-level incremental TTS
    public int MinSentenceLength { get; set; } = 20;        // Minimum chars before splitting on punctuation
    public bool StreamTtsAudio { get; set; } = true;        // Stream TTS HTTP response directly (no buffering)
    public bool PersistentPipeline { get; set; } = true;    // Use single long-lived ffplay process
}
```

### Configuration Behavior

| Setting | `true` (default) | `false` |
|---------|-------------------|---------|
| `ChunkedTts` | Split LLM output into sentences, enqueue each immediately | Wait for full LLM response before TTS (current behavior) |
| `MinSentenceLength` | Higher = fewer, longer sentences (natural but slower first audio) | Lower = more aggressive splitting (faster first audio but choppier) |
| `StreamTtsAudio` | Pipe TTS HTTP response directly to ffplay stdin | Buffer entire TTS response before playing |
| `PersistentPipeline` | Single long-lived ffplay process | New ffplay process per sentence (current behavior) |

When `ChunkedTts: false` AND `StreamTtsAudio: false` AND `PersistentPipeline: false`, behavior is identical to the current code.

### Usage in `appsettings.json`

```json
{
  "Proxy": {
    "BindAddress": "0.0.0.0",
    "Port": 8080,
    "BackendUrl": "https://openrouter.ai/api/v1",
    "BackendModel": "meta-llama/llama-3.3-70b-instruct",
    "TtsPersona": "female-1",
    "TtsModel": "",
    "TtsVoice": "",
    "LogBodies": false,
    "ChunkedTts": true,
    "MinSentenceLength": 20,
    "StreamTtsAudio": true,
    "PersistentPipeline": true
  }
}
```

### How Settings Gate Each Optimization

In `ProxyService.StreamWithTtsAsync`:
```csharp
if (_settings.Proxy.ChunkedTts)
{
    var splitter = new SentenceSplitter(_settings.Proxy.MinSentenceLength);
    // ... incremental enqueue ...
}
else
{
    var textBuffer = new StringBuilder();
    // ... current behavior, enqueue after [DONE] ...
}
```

In `SpeechQueue` constructor:
```csharp
// Pipeline is injected via DI. If PersistentPipeline is false, the DI factory
// returns null, and SpeechQueue falls back to per-call AudioPlayer.
```

---

## Implementation Order — Detailed Steps

### Phase 1: Foundation (no behavioral change, everything behind config flags)

#### Step 1.1: Add config properties

**File**: `Configuration/AppSettings.cs`
- Add `ChunkedTts`, `MinSentenceLength`, `StreamTtsAudio`, `PersistentPipeline` to `ProxySettings`.
- All default to `true` except `MinSentenceLength` which defaults to `20`.

**File**: `appsettings.json`
- Add the four new properties to the `Proxy` section.

**Verify**: `dotnet build` — no behavioral change, just new properties.

#### Step 1.2: Create SentenceSplitter

**File**: `Services/SentenceSplitter.cs` (new)
- Implement as shown in Optimization 1 above.
- No dependencies on other services — pure utility class.

**File**: `tests/benow-conversation.Tests/SentenceSplitterTests.cs` (new)
- Test cases:
  - Single sentence split on period
  - Multiple sentences in one Append
  - Incremental Append (one word at a time)
  - Abbreviations not split (e.g., "Dr. Smith went to the store.")
  - Code fence tracking (no split inside `` ``` `` blocks)
  - Double-newline paragraph break
  - Flush emits remaining text
  - Minimum length threshold respected
  - Empty/whitespace filtered out

**Verify**: `dotnet test` — all tests pass.

#### Step 1.3: Add SynthesizeLiveStreamAsync

**File**: `Services/ITtsService.cs`
- Add method signature.

**File**: `Services/TtsService.cs`
- Implement as shown in Optimization 2 above.
- Uses `HttpCompletionOption.ResponseHeadersRead`.
- Falls back to buffered `SynthesizeToStreamAsync` if format is not cached.

**Verify**: `dotnet build` — new method exists but nothing calls it yet.

#### Step 1.4: Create PersistentAudioPipeline

**File**: `Services/PersistentAudioPipeline.cs` (new)
- Implement as shown in Optimization 2 above.
- Interface `IPersistentAudioPipeline` with `PipeAsync`, `InterruptAsync`, `StartAsync`, `DisposeAsync`.
- ffplay args include `-probesize 32 -analyzeduration 0` for low-latency startup.

**Verify**: `dotnet build` — new class exists but nothing uses it yet.

### Phase 2: Wire up persistent pipeline (config-gated)

#### Step 2.1: Register pipeline in DI

**File**: `Program.cs` — in `ConfigureServices` block (after line 56)

```csharp
services.AddSingleton<IPersistentAudioPipeline>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    if (!settings.Proxy.PersistentPipeline)
        return null!;

    var logger = sp.GetRequiredService<ILogger<PersistentAudioPipeline>>();
    var audioPlayer = sp.GetRequiredService<IAudioPlayer>();
    if (!audioPlayer.IsAvailable)
        return null!;

    // Resolve output profile for volume/device
    var profile = settings.OutputProfiles.FirstOrDefault(p => p.Value.IsDefault).Value;
    var persona = ResolvePipelinePersona(settings);

    return new PersistentAudioPipeline(
        logger,
        ffplayPath: "ffplay",
        format: settings.Audio.OutputFormat,
        volume: profile?.Volume,
        device: string.IsNullOrEmpty(profile?.Device) ? null : profile.Device);
});
```

#### Step 2.2: Modify SpeechQueue constructor

**File**: `Services/SpeechQueue.cs`
- Add `IPersistentAudioPipeline?` parameter to constructor.
- Store as `_pipeline` field.
- `IAudioPlayer` remains as `_audioPlayer` for fallback path.

#### Step 2.3: Start and stop pipeline with daemon lifecycle

**File**: `Program.cs`

In daemon startup (lines 318-346):
```csharp
if (daemonMode)
{
    var proxyService = host.Services.GetRequiredService<IProxyService>();
    var speechQueue = host.Services.GetRequiredService<ISpeechQueue>();
    var pipeline = host.Services.GetRequiredService<IPersistentAudioPipeline>();

    using var cts = new CancellationTokenSource();
    // ...

    if (pipeline != null)
        await pipeline.StartAsync(cts.Token);
    await speechQueue.StartAsync(cts.Token);

    // ... run proxy ...
    // On shutdown:
    await speechQueue.StopAsync(CancellationToken.None);
    if (pipeline is IAsyncDisposable d)
        await d.DisposeAsync();
}
```

Same pattern for STT+daemon mode (lines 268-298).

#### Step 2.4: Modify ProcessQueueAsync to use pipeline

**File**: `Services/SpeechQueue.cs` — `ProcessQueueAsync`

Replace lines 96-108 with the pipeline/fallback logic shown in Optimization 2.

**Verify**: Run daemon mode. With `PersistentPipeline: false`, behavior is identical to current. With `PersistentPipeline: true`, single ffplay process should be visible in `ps` output and audio should play without gaps.

### Phase 3: Wire up sentence-level incremental TTS

#### Step 3.1: Modify SpeechQueue Enqueue interface

**File**: `Services/SpeechQueue.cs`
- Add `cancelCurrent` parameter to `Enqueue` interface method.
- Add `FlushAndCancel` method.

#### Step 3.2: Modify ProxyService.StreamWithTtsAsync

**File**: `Services/ProxyService.cs`
- Replace `StringBuilder textBuffer` with `SentenceSplitter`.
- Enqueue sentences with `cancelCurrent: false`.
- Gated by `_settings.Proxy.ChunkedTts`.

#### Step 3.3: Add FlushAndCancel to new request handling

**File**: `ProxyService.cs` — `HandleChatCompletionsCore`
- Call `_speechQueue.FlushAndCancel()` at the start of each request.

#### Step 3.4: Update NonStreamWithTtsAsync

**File**: `ProxyService.cs`
- Change `_speechQueue.Enqueue(text)` to `_speechQueue.Enqueue(text, cancelCurrent: true)`.

**Verify**: Run daemon with `ChunkedTts: true`. Log output should show individual sentences being enqueued while LLM is still streaming. Audio should start playing before the LLM response completes.

### Phase 4: Polish

#### Step 4.1: Timing metrics

Add `Stopwatch` logging in:
- `SpeechQueue.ProcessQueueAsync` — log time from enqueue to TTS response received to audio piped.
- `ProxyService.StreamWithTtsAsync` — log time to first sentence detected, total LLM time.

#### Step 4.2: Error handling for pipeline crash

In `SpeechQueue.ProcessQueueAsync`, wrap the `_pipeline.PipeAsync` call:
- If `PipeAsync` throws (stdin broken), log the error and continue to next sentence in queue.
- The pipeline auto-restarts on next `EnsureProcessAsync` call.

#### Step 4.3: Update tests

- Add `SentenceSplitterTests` (unit tests for the splitter).
- Add `PersistentAudioPipelineTests` (integration test with a real ffplay process, if available).
- Update `ProxyServiceTests` to cover the chunked TTS path.

---

## Gaps and Risks Addressed

### Gap 1: Format Discovery on First Call
**Risk**: `SynthesizeLiveStreamAsync` can't retry on format error mid-stream.
**Mitigation**: First call for a model uses buffered `SynthesizeToStreamAsync` which discovers and caches the format. Subsequent calls use streaming. The cache persists for the session lifetime.

### Gap 2: Sentence Splitter False Positives
**Risk**: Splitting on "e.g." or "Dr." produces unnatural sentence boundaries.
**Mitigation**: `MinSentenceLength` threshold (default 20). Abbreviations are always shorter. Additionally, `Flush()` at `[DONE]` uses half-threshold to emit the last short fragment.

### Gap 3: Race Between FlushAndCancel and ProcessQueueAsync
**Risk**: `FlushAndCancel()` cancels `_currentPlaybackCts` from the HTTP request thread while `ProcessQueueAsync` is reading from it on the channel reader thread.
**Mitigation**: `CancellationTokenSource` is thread-safe for cancel operations. The linked CTS in `ProcessQueueAsync` will throw `OperationCanceledException`, which is caught by the existing handler (lines 110-117). The channel drain in `FlushAndCancel` uses `TryRead` which is also thread-safe.

### Gap 4: ffplay Stdin Deadlock
**Risk**: If ffplay's stdin buffer fills up (ffplay is slow to consume), `CopyToAsync` in `PipeAsync` blocks, holding the `_lock` semaphore indefinitely.
**Mitigation**: The `CancellationToken` from the linked CTS is passed to `CopyToAsync`. If the sentence is cancelled (new user message), `FlushAndCancel` cancels the CTS, which unblocks `CopyToAsync` with `OperationCanceledException`. Additionally, MP3 data for a single sentence is typically 50-200KB, well within pipe buffer limits.

### Gap 5: Multiple Concurrent TTS Responses
**Risk**: If TTS for sentence N is slow and sentence N+1's TTS completes first, N+1 might pipe before N.
**Mitigation**: The `SemaphoreSlim` in `PersistentAudioPipeline` serializes writes. But this doesn't prevent TTS for N+1 from starting before N finishes piping. The real serialization happens in `ProcessQueueAsync` — it processes one item from the channel at a time. Sentence N+1 stays in the channel until N finishes (TTS + pipe). This is sequential, not concurrent. For future optimization, we could overlap TTS synthesis with playback (start N+1's TTS while N is piping), but this requires a more complex pipeline.

### Gap 6: CLI Mode Regression
**Risk**: Changes to `SpeechQueue` or `ITtsService` break CLI mode (`--stream`, `--play`, etc.).
**Mitigation**: CLI mode does not use `PersistentAudioPipeline` (it's not started for CLI mode). `SpeechQueue` is only used in daemon mode. CLI mode calls `TtsService` and `AudioPlayer` directly from `Program.cs:HandleStream` (line 704). These paths are unchanged. The new `SynthesizeLiveStreamAsync` method is only called from `SpeechQueue`, never from CLI code.

### Gap 7: PCM Format with Persistent Pipeline
**Risk**: If the model outputs PCM, the persistent pipeline is started with `-f s16le -ar 24000`. If the format changes later (different model), the pipeline can't switch formats.
**Mitigation**: The pipeline's format is set at construction time and doesn't change. In practice, the daemon uses a single persona/model for its entire lifetime (configured in `appsettings.json`). If the model changes, the daemon restarts, creating a new pipeline with the correct format.

### Gap 8: ffplay Process Leak on Crash
**Risk**: If the .NET process crashes without calling `DisposeAsync`, the ffplay process becomes orphaned.
**Mitigation**: ffplay is started with `-autoexit` — when stdin closes (because the .NET process died), ffplay exits. Additionally, the existing `PipeWireRecorder.KillOrphanedProcesses` pattern (killing orphaned ffmpeg processes on startup) can be extended to ffplay.

---

## File Change Summary

| File | Change | Phase |
|------|--------|-------|
| `Configuration/AppSettings.cs` | Add 4 properties to `ProxySettings` | 1.1 |
| `appsettings.json` | Add 4 new proxy config entries | 1.1 |
| `Services/SentenceSplitter.cs` | **New** — incremental sentence boundary detection | 1.2 |
| `tests/.../SentenceSplitterTests.cs` | **New** — unit tests for SentenceSplitter | 1.2 |
| `Services/ITtsService.cs` | Add `SynthesizeLiveStreamAsync` method signature | 1.3 |
| `Services/TtsService.cs` | Implement `SynthesizeLiveStreamAsync` | 1.3 |
| `Services/PersistentAudioPipeline.cs` | **New** — single long-lived ffplay process | 1.4 |
| `Program.cs` | Register `IPersistentAudioPipeline` in DI | 2.1 |
| `Services/SpeechQueue.cs` | Add `IPersistentAudioPipeline` dependency, pipeline/fallback in `ProcessQueueAsync`, `cancelCurrent` param on `Enqueue`, `FlushAndCancel` method | 2.2, 2.4, 3.1 |
| `Program.cs` | Start/stop pipeline in daemon lifecycle | 2.3 |
| `Services/ProxyService.cs` | `SentenceSplitter` in `StreamWithTtsAsync`, `FlushAndCancel` on new request, `cancelCurrent` on non-stream enqueue | 3.2, 3.3, 3.4 |
| `Services/ISpeechQueue.cs` (in `SpeechQueue.cs`) | Add `cancelCurrent` param, `FlushAndCancel` method | 3.1 |

---

## Expected Latency Improvement

| Scenario | Before | After | Improvement |
|----------|--------|-------|-------------|
| Short response (1 sentence, ~5s LLM) | ~8s (5s LLM + 3s TTS + startup) | ~5-6s (TTS starts immediately, streams to persistent ffplay) | ~30% |
| Medium response (3 sentences, ~12s LLM) | ~18s (12s LLM + 6s TTS + 3x startup) | ~5-6s (first sentence at ~4s + ~2s TTS) | ~70% |
| Long response (6+ sentences, ~30s LLM) | ~40s+ (30s LLM + 10s TTS + 6x startup) | ~5-8s (first sentence heard while rest generates) | ~80% |

The user hears audio at roughly **time-to-first-LLM-sentence + TTS-time-for-one-sentence**, regardless of total response length. Today it's **total-LLM-time + total-TTS-time + N × process-startup**.
