using System.Diagnostics;
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
    private readonly AppSettings _settings;
    private readonly ILogger<SttRunner> _logger;

    public SttRunner(
        IAudioRecorder recorder,
        ITranscriptionService transcriber,
        ITextTransformer transformer,
        IClipboardService clipboard,
        IKeyboardSimulator keyboard,
        IRecordingTrigger trigger,
        IOptions<AppSettings> settings,
        ILogger<SttRunner> logger)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _transformer = transformer;
        _clipboard = clipboard;
        _keyboard = keyboard;
        _trigger = trigger;
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

        _logger.LogInformation("[STT] Ready. Waiting for trigger to start recording...");
        var cycleCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? recordedFile = null;
            cycleCount++;

            try
            {
                Console.WriteLine("[STT] Waiting for trigger...");
                await _trigger.WaitForTriggerAsync(cancellationToken);
                _logger.LogInformation("[STT] Cycle {Cycle}: trigger received", cycleCount);

                var ext = _settings.Stt.RecorderFormat.Equals("wav", StringComparison.OrdinalIgnoreCase) ? "wav" : "mp3";
                var tempFile = Path.Combine(Path.GetTempPath(), $"stt_{Guid.NewGuid():N}.{ext}");
                recordedFile = tempFile;

                Console.WriteLine("[STT] Recording... Ctrl+Space to stop.");
                _logger.LogInformation("[STT] Cycle {Cycle}: recording to {File}", cycleCount, tempFile);

                var sw = Stopwatch.StartNew();
                using var recCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                if (_settings.Stt.FeedbackBeep)
                {
                    await PlayBeepAsync(880, 0.15, CancellationToken.None);
                    await Task.Delay(80, CancellationToken.None);
                }

                var recordingTask = _recorder.RecordToFileAsync(tempFile, recCts.Token);

                await _trigger.WaitForTriggerAsync(cancellationToken);
                _logger.LogInformation("[STT] Cycle {Cycle}: stop trigger received", cycleCount);
                recCts.Cancel();

                if (_settings.Stt.FeedbackBeep)
                    await PlayBeepAsync(440, 0.2, cancellationToken);

                recordedFile = await recordingTask;
                _logger.LogInformation("[STT] Cycle {Cycle}: recording saved ({Ms}ms, {Size} bytes)", cycleCount, sw.ElapsedMilliseconds, new FileInfo(recordedFile).Length);

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
                if (_settings.Stt.CleanupSkip || string.IsNullOrWhiteSpace(_settings.TranscriptCleanup.Model))
                {
                    _logger.LogInformation("[STT] Cycle {Cycle}: skipping cleanup (CleanupSkip={Skip}, Model={Model})", cycleCount, _settings.Stt.CleanupSkip, _settings.TranscriptCleanup.Model ?? "(empty)");
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
                        _logger.LogInformation("[STT] Cycle {Cycle}: pressing Enter (submits via focused app)...", cycleCount);
                        await _keyboard.PressEnterAsync(cancellationToken);
                    }
                }
                else
                {
                    _logger.LogWarning("[STT] Cycle {Cycle}: keyboard simulator not available — skipping paste", cycleCount);
                }

                Console.WriteLine("[STT] Done. Waiting for next trigger...");
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

    private async Task PlayBeepAsync(int frequencyHz, double durationSec, CancellationToken ct)
    {
        try
        {
            var beepFile = Path.Combine(Path.GetTempPath(), $"stt_beep_{frequencyHz}.mp3");

            if (!File.Exists(beepFile))
            {
                _logger.LogInformation("[STT] Generating beep file {Hz}Hz", frequencyHz);
                var genArgs = $"-y -f lavfi -i \"sine=frequency={frequencyHz}:duration={durationSec}\" -ac 1 -ar 16000 -acodec libmp3lame -b:a 32k \"{beepFile}\"";
                var genPsi = new ProcessStartInfo
                {
                    FileName = _settings.Stt.FfmpegPath,
                    Arguments = genArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var genProc = Process.Start(genPsi);
                await genProc!.WaitForExitAsync(ct);
            }

            var playArgs = $"-nodisp -autoexit -volume 80 \"{beepFile}\"";
            var playPsi = new ProcessStartInfo
            {
                FileName = "ffplay",
                Arguments = playArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _logger.LogInformation("[STT] Playing beep: {Hz}Hz {Duration}s", frequencyHz, durationSec);
            using var playProc = Process.Start(playPsi);
            await playProc!.WaitForExitAsync(ct);
            _logger.LogInformation("[STT] Beep finished: {Hz}Hz (exit {ExitCode})", frequencyHz, playProc.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[STT] Beep playback failed: {Error}", ex.Message);
        }
    }
}
