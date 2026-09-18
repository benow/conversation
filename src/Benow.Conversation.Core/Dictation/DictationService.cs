using Benow.Conversation.Audio;
using Benow.Conversation.Engine;
using Benow.Conversation.Input;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Dictation;

/// <summary>
/// Speak mode (the dictation wedge): hotkey → streaming capture → VAD/STT as you talk →
/// final transcript → clipboard + simulated paste into the focused app. The transcript is also
/// returned so the caller can log/show it. Capture itself lives in <see cref="VoiceSession"/>.
/// </summary>
public sealed class DictationService
{
    private readonly ILogger<DictationService> _logger;
    private readonly VoiceSession _session;
    private readonly ITextInserter _inserter;

    /// <summary>Raised with the final transcript (for logs / the overlay).</summary>
    public event Action<string>? TranscriptReady;

    public DictationService(ILogger<DictationService> logger, ConversationEngine engine,
        FfmpegStreamingCapture capture, ITextInserter inserter)
        : this(logger, new VoiceSession(logger, engine, capture), inserter) { }

    public DictationService(ILogger<DictationService> logger, VoiceSession session, ITextInserter inserter)
    {
        _logger = logger;
        _session = session;
        _inserter = inserter;
    }

    public bool IsRecording => _session.IsRecording;

    public Task StartAsync() => _session.StartAsync();

    /// <summary>Stops the session, finalizes the transcript, and pastes it into the focused app.</summary>
    public async Task<string> StopAndDeliverAsync(CancellationToken ct = default)
    {
        var transcript = await _session.StopAndTranscribeAsync(ct);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            _logger.LogWarning("[dictate] no transcript produced — nothing pasted");
            return "";
        }

        try
        {
            await _inserter.InsertAsync(transcript, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[dictate] paste failed — the text is on the clipboard, press Ctrl+V manually. Error: {Error}", ex.Message);
        }
        TranscriptReady?.Invoke(transcript);
        return transcript;
    }

    /// <summary>Toggle helper for a single hotkey.</summary>
    public async Task ToggleAsync()
    {
        if (IsRecording) await StopAndDeliverAsync();
        else await StartAsync();
    }
}
