using Benow.Conversation.Audio;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Engine;

/// <summary>
/// One push-to-talk capture session: streaming capture → VAD/STT → final transcript, with no
/// opinion about what happens to the text. Speak pastes it into the focused app; Converse feeds
/// it to the LLM. Streaming capture is what keeps time-to-transcript independent of utterance
/// length — segments are transcribed while the user is still speaking.
/// </summary>
public sealed class VoiceSession
{
    private readonly ILogger _logger;
    private readonly ConversationEngine _engine;
    private readonly FfmpegStreamingCapture _capture;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _active;

    public VoiceSession(ILogger logger, ConversationEngine engine, FfmpegStreamingCapture capture)
    {
        _logger = logger;
        _engine = engine;
        _capture = capture;
    }

    public bool IsRecording => _active != null;

    /// <summary>Raised for each live transcript segment (overlay/diagnostics).</summary>
    public event Action<string>? Partial;

    /// <summary>Starts capture. No-op when a session is already running.</summary>
    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        if (_active != null) { _gate.Release(); return; }
        _active = new CancellationTokenSource();
        _gate.Release();

        _engine.BeginSession();
        _logger.LogInformation("[session] recording…");

        var cts = _active;
        _ = Task.Run(async () =>
        {
            try
            {
                await _capture.CaptureAsync(frame => _engine.PushFrameAsync(frame, cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Normal stop path.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[session] capture failed — {Reason}. Fix: check the input device " +
                    "(run --devices) and that ffmpeg can open it (pactl list short sources).", ex.Message);
            }
        });
    }

    /// <summary>Stops capture and returns the final transcript ("" when nothing was heard).</summary>
    public async Task<string> StopAndTranscribeAsync(CancellationToken ct = default)
    {
        var cts = _active;
        if (cts == null) return "";
        _active = null;
        cts.Cancel();

        try
        {
            var transcript = await _engine.EndSessionAsync(ct);
            _logger.LogInformation("[session] transcript ({Chars}c): \"{Text}\"", transcript.Length, transcript);
            if (!string.IsNullOrWhiteSpace(transcript)) Partial?.Invoke(transcript);
            return transcript;
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>Toggle helper for a single hotkey.</summary>
    public Task ToggleAsync() => IsRecording ? StopAndTranscribeAsync() : StartAsync();
}
