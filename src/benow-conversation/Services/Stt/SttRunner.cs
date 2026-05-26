using System.Diagnostics;
using System.Text.Json;
using benow_conversation.Configuration;
using benow_conversation.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services.Stt;

public class SttRunner : ISttRunner
{
    private readonly IAudioRecorder _recorder;
    private readonly ITranscriptionService _transcriber;
    private readonly ITextTransformer _transformer;
    private readonly IClipboardService _clipboard;
    private readonly IKeyboardSimulator _keyboard;
    private readonly IRecordingTrigger _trigger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettings _settings;
    private readonly ILogger<SttRunner> _logger;

    public SttRunner(
        IAudioRecorder recorder,
        ITranscriptionService transcriber,
        ITextTransformer transformer,
        IClipboardService clipboard,
        IKeyboardSimulator keyboard,
        IRecordingTrigger trigger,
        IHttpClientFactory httpClientFactory,
        IOptions<AppSettings> settings,
        ILogger<SttRunner> logger)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _transformer = transformer;
        _clipboard = clipboard;
        _keyboard = keyboard;
        _trigger = trigger;
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[STT] Pipeline starting...");
        _logger.LogInformation("[STT] Plugins loaded — recorder={Recorder} (available={RecAvail}), transcriber=loaded, transformer=loaded, clipboard={Clipboard} (available={ClipAvail}), keyboard={Keyboard} (available={KbAvail}), trigger=loaded (available={TrigAvail})",
            _settings.Stt.Recorder, _recorder.IsAvailable,
            _settings.Stt.Clipboard, _clipboard.IsAvailable,
            _settings.Stt.Keyboard, _keyboard.IsAvailable,
            _trigger.IsAvailable);

        if (!_recorder.IsAvailable)
        {
            _logger.LogError("[STT] Recorder '{Recorder}' is not available — cannot start", _settings.Stt.Recorder);
            Console.WriteLine("[STT] Error: audio recorder not available. Install required system tools.");
            return;
        }

        if (!_trigger.IsAvailable)
        {
            _logger.LogError("[STT] Trigger is not available — cannot start");
            Console.WriteLine("[STT] Error: recording trigger not available.");
            return;
        }

        var proxyReady = false;
        if (_settings.Proxy.Port > 0)
        {
            _logger.LogInformation("[STT] Waiting for proxy at localhost:{Port}...", _settings.Proxy.Port);
            proxyReady = await WaitForProxyAsync(cancellationToken);
            if (proxyReady)
                _logger.LogInformation("[STT] Proxy is ready at localhost:{Port}", _settings.Proxy.Port);
            else
                _logger.LogInformation("[STT] Proxy not detected at localhost:{Port} — skipping proxy submission", _settings.Proxy.Port);
        }

        _logger.LogInformation("[STT] Ready. Waiting for trigger to start recording...");
        var cycleCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? recordedFile = null;
            cycleCount++;

            try
            {
                Console.WriteLine("[STT] Waiting for trigger to start recording...");
                await _trigger.WaitForTriggerAsync(cancellationToken);
                _logger.LogInformation("[STT] Cycle {Cycle}: trigger received — starting recording", cycleCount);

                var tempFile = Path.Combine(Path.GetTempPath(), $"stt_{Guid.NewGuid():N}.wav");
                recordedFile = tempFile;

                Console.WriteLine("[STT] Recording... waiting for trigger to stop.");
                _logger.LogInformation("[STT] Cycle {Cycle}: recording to {File}", cycleCount, tempFile);

                var sw = Stopwatch.StartNew();

                using var recordCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var recordTask = _recorder.RecordToFileAsync(tempFile, recordCts.Token);

                await _trigger.WaitForTriggerAsync(cancellationToken);
                _logger.LogInformation("[STT] Cycle {Cycle}: stop trigger received after {Ms}ms", cycleCount, sw.ElapsedMilliseconds);

                recordCts.Cancel();

                recordedFile = await recordTask;
                _logger.LogInformation("[STT] Cycle {Cycle}: recording saved to {File}", cycleCount, recordedFile);

                Console.WriteLine("[STT] Transcribing...");
                _logger.LogInformation("[STT] Cycle {Cycle}: transcribing {File}...", cycleCount, recordedFile);

                var transcript = await _transcriber.TranscribeAsync(recordedFile, cancellationToken);

                if (string.IsNullOrWhiteSpace(transcript))
                {
                    _logger.LogWarning("[STT] Cycle {Cycle}: empty transcript — skipping", cycleCount);
                    Console.WriteLine("[STT] Empty transcript, skipping.");
                    continue;
                }

                _logger.LogInformation("[STT] Cycle {Cycle}: raw transcript ({Length} chars): {Text}", cycleCount, transcript.Length, transcript);

                string finalText;
                if (_settings.Stt.CleanupSkip)
                {
                    _logger.LogInformation("[STT] Cycle {Cycle}: skipping cleanup (CleanupSkip=true)", cycleCount);
                    finalText = transcript;
                }
                else
                {
                    Console.WriteLine("[STT] Cleaning up...");
                    _logger.LogInformation("[STT] Cycle {Cycle}: cleaning up transcript via {Model}...", cycleCount, _settings.TranscriptCleanup.Model);
                    finalText = await _transformer.TransformAsync(transcript, cancellationToken);
                }

                _logger.LogInformation("[STT] Cycle {Cycle}: final text ({Length} chars): {Text}", cycleCount, finalText.Length, finalText);
                Console.WriteLine($"[STT] Final: {finalText}");

                if (_clipboard.IsAvailable)
                {
                    _logger.LogInformation("[STT] Cycle {Cycle}: copying to clipboard...", cycleCount);
                    await _clipboard.CopyAsync(finalText, cancellationToken);
                    _logger.LogDebug("[STT] Cycle {Cycle}: clipboard write complete", cycleCount);
                }
                else
                {
                    _logger.LogWarning("[STT] Cycle {Cycle}: clipboard not available — skipping copy", cycleCount);
                }

                await Task.Delay(100, cancellationToken);

                if (_keyboard.IsAvailable)
                {
                    _logger.LogInformation("[STT] Cycle {Cycle}: pasting via keyboard simulator...", cycleCount);
                    await _keyboard.PasteAsync(cancellationToken);
                    await Task.Delay(100, cancellationToken);

                    if (_settings.Stt.AutoSubmit)
                    {
                        _logger.LogInformation("[STT] Cycle {Cycle}: auto-submitting (pressing Enter)...", cycleCount);
                        await _keyboard.PressEnterAsync(cancellationToken);
                    }
                }
                else
                {
                    _logger.LogWarning("[STT] Cycle {Cycle}: keyboard simulator not available — skipping paste", cycleCount);
                }

                if (proxyReady && !string.IsNullOrWhiteSpace(_settings.Proxy.BackendModel))
                {
                    Console.WriteLine("[STT] Submitting to proxy for TTS response...");
                    _logger.LogInformation("[STT] Cycle {Cycle}: submitting to proxy at localhost:{Port} for TTS (model={Model})...", cycleCount, _settings.Proxy.Port, _settings.Proxy.BackendModel);
                    await SubmitToProxyAsync(finalText, cancellationToken);
                    _logger.LogInformation("[STT] Cycle {Cycle}: proxy response received — TTS playback started", cycleCount);
                    Console.WriteLine("[STT] Done. Waiting for next trigger...");
                }
                else
                {
                    Console.WriteLine("[STT] Done. Waiting for next trigger...");
                }

                _logger.LogInformation("[STT] Cycle {Cycle}: complete", cycleCount);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("[STT] Shutdown requested");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STT] Cycle {Cycle}: error in pipeline at step '{Step}': {Message}", cycleCount, ex.TargetSite?.Name ?? "unknown", ex.Message);
                Console.WriteLine($"[STT] Error: {ex.Message}");
                Console.WriteLine("[STT] Waiting for next trigger to try again...");
            }
            finally
            {
                if (recordedFile != null)
                {
                    try
                    {
                        if (File.Exists(recordedFile))
                        {
                            File.Delete(recordedFile);
                            _logger.LogDebug("[STT] Deleted temp file: {File}", recordedFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[STT] Failed to delete temp file {File}: {Error}", recordedFile, ex.Message);
                    }
                }
            }
        }

        _logger.LogInformation("[STT] Pipeline stopped after {Cycles} cycle(s)", cycleCount);
    }

    private async Task<bool> WaitForProxyAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var client = _httpClientFactory.CreateClient("OpenRouter");
        var attempt = 0;

        while (!cts.Token.IsCancellationRequested)
        {
            attempt++;
            try
            {
                var response = await client.GetAsync($"http://localhost:{_settings.Proxy.Port}/v1/models", cts.Token);
                _logger.LogDebug("[STT] Proxy health check attempt {Attempt}: HTTP {Status}", attempt, (int)response.StatusCode);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[STT] Proxy health check attempt {Attempt} failed: {Error}", attempt, ex.Message);
            }

            try
            {
                await Task.Delay(500, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return false;
    }

    private async Task SubmitToProxyAsync(string text, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("OpenRouter");

        var requestBody = new
        {
            model = _settings.Proxy.BackendModel,
            messages = new[] { new { role = "user", content = text } },
            stream = false
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_settings.Proxy.Port}/v1/chat/completions")
        {
            Content = content
        };

        try
        {
            var response = await client.SendAsync(request, cancellationToken);
            _logger.LogInformation("[STT] Proxy response: HTTP {Status}", (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("[STT] Proxy returned non-success: HTTP {Status} — {Error}", (int)response.StatusCode, errorBody.Length > 200 ? errorBody[..200] + "..." : errorBody);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[STT] Proxy submission failed (connection error): {Message}", ex.Message);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[STT] Proxy submission failed: {Message}", ex.Message);
        }
    }
}
