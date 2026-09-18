using Benow.Conversation.Audio;
using Benow.Conversation.Composition;
using Benow.Conversation.Config;
using Benow.Conversation.Engine;
using Benow.Conversation.Input;
using Benow.Conversation.Personas;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Desktop;

/// <summary>
/// The desktop app's brain, independent of any UI toolkit: it owns the hotkey service, drives
/// Speak/Converse turns, applies personas, and raises state changes the tray/overlay render.
/// Avalonia supplies the windows; everything decision-shaped lives here (and is unit-tested).
///
/// Two verbs, one engine (plan §4.4):
///   Speak    — capture → transcript → paste into the focused app. No LLM, no TTS.
///   Converse — capture → transcript → LLM (+ TTS unless muted) → overlay text.
/// </summary>
public sealed class DesktopHost : IAsyncDisposable
{
    private readonly ILogger<DesktopHost> _logger;
    private readonly ConversationRuntime _runtime;
    private readonly ConfigStore _configStore;
    private readonly PersonaStore _personas;
    private readonly ILoggerFactory _loggerFactory;
    private EvdevHotkeyService? _hotkeys;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private string _lastReply = "";

    public DesktopHost(ILoggerFactory loggerFactory, ConversationRuntime runtime, ConfigStore configStore,
        PersonaStore personas, DesktopEventSink? sink = null)
    {
        _logger = loggerFactory.CreateLogger<DesktopHost>();
        _loggerFactory = loggerFactory;
        _runtime = runtime;
        _configStore = configStore;
        _personas = personas;
        _runtime.Engine.Muted = runtime.Config.Muted;

        _personas.SetActive(runtime.Config.ActivePersona);
        _runtime.Engine.Timeline = null;

        _runtime.Session.Partial += text => SetState(s => s with { Status = $"heard: {Trim(text)}" });
        _runtime.Dictation.TranscriptReady += text => SetState(s => s with { Status = "dictated" });

        // Engine events drive the overlay: streaming text, live status, and honest errors.
        // The host attaches its sink itself — the engine outlives whoever wires it, and a sink
        // that is not attached fails silently (the overlay simply stays empty).
        Sink = sink ?? new DesktopEventSink();
        AttachSink(_runtime, Sink);
        Sink.Chunk += chunk => ReplyChunk?.Invoke(chunk);
        Sink.Transcript += (text, corrected) =>
            SetState(s => s with { Status = corrected ? "heard (corrected)" : "heard" });
        Sink.Speaking += speaking => SetState(s => s with { Status = speaking ? "thinking…" : "speaking" });
        Sink.Error += (stage, message) =>
        {
            SetState(s => s with { Status = "error" });
            Error?.Invoke($"{stage}: {message}");
        };
    }

    /// <summary>The engine's event sink for this host (compose it with a logging sink when wiring).</summary>
    public DesktopEventSink Sink { get; }

    /// <summary>Ensures the host's sink is attached without detaching whatever is already there.</summary>
    private void AttachSink(ConversationRuntime runtime, DesktopEventSink sink)
    {
        var current = runtime.Engine.Sink;
        if (current is CompositeSink composite && composite.Contains(sink)) return;
        if (ReferenceEquals(current, sink)) return;

        runtime.Engine.Sink = ReferenceEquals(current, NullEventSink.Instance)
            ? sink
            : new CompositeSink(current, sink);
        _logger.LogDebug("[host] UI sink attached (previous sink: {Previous})", current.GetType().Name);
    }

    /// <summary>Raised whenever <see cref="State"/> changes (UI thread marshalling is the caller's job).</summary>
    public event Action<TrayState>? StateChanged;
    /// <summary>Streaming assistant text for the overlay (chunks arrive as the LLM emits them).</summary>
    public event Action<string>? ReplyChunk;
    /// <summary>Raised when a turn starts/ends so the overlay can show/hide.</summary>
    public event Action<bool>? ConverseActiveChanged;
    public event Action<string>? Error;

    public TrayState State { get; private set; } = new();
    public string LastReply => _lastReply;
    public ConversationRuntime Runtime => _runtime;

    // ---- Lifecycle ----

    public async Task StartAsync(CancellationToken ct = default)
    {
        await RefreshDevicesAsync(ct);
        SetState(s => s with { Personas = _personas.All.Select(p => p.Name).ToList(), ActivePersona = _personas.ActiveName });
        StartHotkeys();
    }

    private void StartHotkeys()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Windows uses a low-level keyboard hook (phase-3 follow-up); until then the tray and
            // the overlay's own buttons drive everything, so the app is fully usable without them.
            _logger.LogInformation("[host] global hotkeys unavailable on this platform — use the tray or the overlay buttons");
            return;
        }

        var bindings = _runtime.Config.Hotkeys.Bindings().ToList();
        _hotkeys = new EvdevHotkeyService(_loggerFactory.CreateLogger<EvdevHotkeyService>(), bindings);
        _hotkeys.Triggered += action => _ = Task.Run(() => DispatchAsync(action switch
        {
            HotkeyAction.Speak => TrayAction.Speak,
            HotkeyAction.Converse => TrayAction.Converse,
            _ => TrayAction.Interrupt
        }));
        _hotkeys.Start();
        _logger.LogInformation("[host] hotkeys active: {Bindings}",
            string.Join(", ", bindings.Select(b => $"{b.Action}={b.Source}")));
    }

    // ---- Actions ----

    public Task DispatchAsync(TrayAction action, string? argument = null) => action switch
    {
        TrayAction.Speak => ToggleSpeakAsync(),
        TrayAction.Converse => ToggleConverseAsync(),
        TrayAction.Interrupt => InterruptAsync(),
        TrayAction.ToggleMute => Task.FromResult(ToggleMute()),
        TrayAction.SetInputDevice => SetInputDeviceAsync(argument),
        TrayAction.SetOutputDevice => SetOutputDeviceAsync(argument),
        TrayAction.SetPersona => Task.FromResult(SetPersona(argument)),
        TrayAction.ToggleHotkeys => Task.FromResult(ToggleHotkeys()),
        TrayAction.ReplayLast => ReplayLastAsync(),
        _ => Task.CompletedTask
    };

    /// <summary>Speak: dictation into the focused app (toggle on repeated presses).</summary>
    public async Task ToggleSpeakAsync()
    {
        try
        {
            if (_runtime.Dictation.IsRecording)
            {
                SetState(s => s with { Status = "transcribing…" });
                var text = await _runtime.Dictation.StopAndDeliverAsync();
                SetState(s => s with
                {
                    IsRecording = false,
                    Status = string.IsNullOrWhiteSpace(text) ? "nothing heard" : "pasted"
                });
            }
            else
            {
                await _runtime.Dictation.StartAsync();
                SetState(s => s with { IsRecording = true, Status = "listening (Speak)…" });
            }
        }
        catch (Exception ex)
        {
            Fail("speak", ex);
        }
    }

    /// <summary>Converse: capture, then send the transcript to the LLM (with TTS unless muted).</summary>
    public async Task ToggleConverseAsync()
    {
        await _turnGate.WaitAsync();
        try
        {
            if (_runtime.Session.IsRecording)
            {
                SetState(s => s with { Status = "thinking…" });
                var transcript = await _runtime.Session.StopAndTranscribeAsync();
                ConverseActiveChanged?.Invoke(true);
                SetState(s => s with { IsConversing = false });

                if (string.IsNullOrWhiteSpace(transcript))
                {
                    SetState(s => s with { Status = "nothing heard" });
                    ConverseActiveChanged?.Invoke(false);
                    return;
                }

                await SendAsync(transcript);
            }
            else
            {
                ApplyPersona();
                await _runtime.Session.StartAsync();
                ConverseActiveChanged?.Invoke(true);
                SetState(s => s with { IsConversing = true, Status = "listening (Converse)…" });
            }
        }
        catch (Exception ex)
        {
            Fail("converse", ex);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>Sends a turn (voice transcript or typed text) and tracks it to completion.</summary>
    public async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Sink.BeginTurn();
        SetState(s => s with { Status = "thinking…" });
        ConverseActiveChanged?.Invoke(true);

        try
        {
            var result = await _runtime.Engine.ConverseAsync(text);
            var reply = result?.Text ?? "";
            if (!string.IsNullOrWhiteSpace(reply))
            {
                _lastReply = reply;
                SetState(s => s with { HasLastReply = true, Status = _runtime.Engine.Muted ? "replied (muted)" : "replied" });
            }
            else if (Sink.LastError != null)
            {
                // The engine reported a failure (unreachable provider, bad key, TTS error).
                // Saying "no reply" here would hide the actual cause from the user.
                SetState(s => s with { Status = "error" });
            }
            else
            {
                SetState(s => s with { Status = "no reply" });
            }
        }
        catch (Exception ex)
        {
            Fail("turn", ex);
        }
    }

    public Task InterruptAsync()
    {
        _runtime.Engine.Interrupt();
        SetState(s => s with { Status = "idle" });
        return Task.CompletedTask;
    }

    private bool ToggleMute()
    {
        _runtime.Engine.Muted = !_runtime.Engine.Muted;
        _runtime.Config.Muted = _runtime.Engine.Muted;
        _configStore.Save(_runtime.Config);
        _logger.LogInformation("[host] mute {State} — replies will {Action}",
            _runtime.Engine.Muted, _runtime.Engine.Muted ? "not be synthesized at all" : "be spoken");
        SetState(s => s with { Muted = _runtime.Engine.Muted });
        return _runtime.Engine.Muted;
    }

    private bool ToggleHotkeys()
    {
        var enabled = !State.HotkeysEnabled;
        if (_hotkeys != null)
        {
            if (enabled) _hotkeys.Start();
            else _hotkeys.Dispose();
        }
        SetState(s => s with { HotkeysEnabled = enabled, Status = enabled ? "hotkeys enabled" : "hotkeys paused" });
        return enabled;
    }

    public async Task ReplayLastAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastReply)) return;
        if (_runtime.Engine.Muted)
        {
            SetState(s => s with { Status = "muted — unmute to replay" });
            return;
        }
        _runtime.Engine.Speak(_lastReply);
        SetState(s => s with { Status = "speaking" });
        await Task.CompletedTask;
    }

    /// <summary>Test seam: primes the replay buffer without a live provider round-trip.</summary>
    internal void PrimeLastReply(string reply)
    {
        _lastReply = reply;
        SetState(s => s with { HasLastReply = !string.IsNullOrWhiteSpace(reply) });
    }

    private async Task SetInputDeviceAsync(string? device)
    {
        _runtime.Config.InputDevice = device ?? "";
        _configStore.Save(_runtime.Config);
        SetState(s => s with { InputDevice = string.IsNullOrWhiteSpace(device) ? null : device });
        _logger.LogInformation("[host] input device → {Device} (applies to the next session)",
            string.IsNullOrWhiteSpace(device) ? "system default" : device);
        await Task.CompletedTask;
    }

    private async Task SetOutputDeviceAsync(string? device)
    {
        _runtime.Config.OutputDevice = device ?? "";
        _configStore.Save(_runtime.Config);
        SetState(s => s with { OutputDevice = string.IsNullOrWhiteSpace(device) ? null : device });
        _logger.LogInformation("[host] output device → {Device} (applies on restart — ffplay is a live process)",
            string.IsNullOrWhiteSpace(device) ? "system default" : device);
        await Task.CompletedTask;
    }

    /// <summary>Selects the active persona and applies its bundle (prompt + speaker model + TTS voice).</summary>
    public bool SetPersona(string? name)
    {
        var persona = _personas.SetActive(name);
        _runtime.Config.ActivePersona = persona.Name;
        _configStore.Save(_runtime.Config);
        ApplyPersona();
        SetState(s => s with { ActivePersona = persona.Name });
        _logger.LogInformation("[host] persona → {Persona} (model={Model}, tts={Tts}/{Voice}, prompt={PromptChars}c)",
            persona.Name, persona.SpeakerModel ?? "(default)", persona.TtsProvider ?? "(default)",
            persona.TtsVoice ?? "(default)", persona.SystemPrompt.Length);
        return true;
    }

    /// <summary>
    /// Applies the persona bundle to the live engine (prompt + speaker model + TTS engine/voice).
    /// History is cleared so a persona switch cannot leak the previous character's conversation
    /// into the new one. STT and the extractor model are deliberately NOT persona-scoped — see
    /// <see cref="PersonaBinding"/>.
    /// </summary>
    private void ApplyPersona()
    {
        var persona = _personas.Active;
        PersonaBinding.Apply(_runtime.Config, persona);
        _runtime.Engine.History.Clear();
    }

    public async Task RefreshDevicesAsync(CancellationToken ct)
    {
        try
        {
            var inputs = await _runtime.Devices.ListInputsAsync(ct);
            var outputs = await _runtime.Devices.ListOutputsAsync(ct);
            SetState(s => s with
            {
                InputDevices = inputs.Select(d => d.Id).ToList(),
                InputDevice = string.IsNullOrWhiteSpace(_runtime.Config.InputDevice) ? null : _runtime.Config.InputDevice,
                OutputDevices = outputs.Select(d => d.Id).ToList(),
                OutputDevice = string.IsNullOrWhiteSpace(_runtime.Config.OutputDevice) ? null : _runtime.Config.OutputDevice
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[host] device enumeration failed: {Error}", ex.Message);
        }
    }

    private void SetState(Func<TrayState, TrayState> update)
    {
        State = update(State);
        StateChanged?.Invoke(State);
    }

    /// <summary>Pushes a streaming chunk into the overlay and the current turn's state.</summary>
    public void EmitChunk(string chunk) => ReplyChunk?.Invoke(chunk);

    private void Fail(string stage, Exception ex)
    {
        _logger.LogError(ex, "[host] {Stage} failed — {Reason}. Fix: check the provider keys in Settings " +
            "(the app is offline-capable only for nothing) and the device selection.", stage, ex.Message);
        SetState(s => s with { IsRecording = false, IsConversing = false, Status = "error" });
        Error?.Invoke($"{stage}: {ex.Message}");
    }

    private static string Trim(string text) => text.Length <= 48 ? text : text[..48] + "…";

    public ValueTask DisposeAsync()
    {
        _hotkeys?.Dispose();
        return _runtime.DisposeAsync();
    }
}
