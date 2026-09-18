using Benow.Conversation.Personas;

namespace Benow.Conversation.Desktop;

/// <summary>An entry in the tray menu. Actions are ids, not delegates, so the model is testable
/// without a UI toolkit and the Avalonia layer owns the dispatch table.</summary>
public sealed record TrayEntry(
    string Label,
    TrayAction Action,
    string? Argument = null,
    bool IsChecked = false,
    bool IsEnabled = true,
    bool SeparatorBefore = false)
{
    public List<TrayEntry> Children { get; init; } = new();
}

/// <summary>Everything the tray can ask the host to do (plan §4.4 menu).</summary>
public enum TrayAction
{
    Speak,
    Converse,
    Interrupt,
    ToggleMute,
    SetInputDevice,
    SetOutputDevice,
    SetPersona,
    ToggleHotkeys,
    ReplayLast,
    Settings,
    Quit
}

/// <summary>State the tray renders from. Kept deliberately small — the host updates it and
/// re-renders; nothing in here touches a toolkit type.</summary>
public sealed record TrayState
{
    public bool IsRecording { get; init; }
    public bool IsConversing { get; init; }
    public bool Muted { get; init; }
    public bool HotkeysEnabled { get; init; } = true;
    public string ActivePersona { get; init; } = PersonaStore.DefaultName;
    public IReadOnlyList<string> Personas { get; init; } = Array.Empty<string>();
    public string? InputDevice { get; init; }
    public IReadOnlyList<string> InputDevices { get; init; } = Array.Empty<string>();
    public string? OutputDevice { get; init; }
    public IReadOnlyList<string> OutputDevices { get; init; } = Array.Empty<string>();
    public bool HasLastReply { get; init; }
    /// <summary>Live status line (recording/thinking/speaking) — shown as the tray tooltip.</summary>
    public string Status { get; init; } = "idle";
}

/// <summary>
/// Builds the tray menu from <see cref="TrayState"/>. Pure: same state in, same menu out — which
/// is what makes the tray behaviour testable without a windowing system.
/// </summary>
public static class TrayModel
{
    public const string SystemDefault = "(system default)";

    public static IReadOnlyList<TrayEntry> Build(TrayState state)
    {
        var entries = new List<TrayEntry>
        {
            new(state.IsRecording ? "Stop & paste (Speak)" : "Speak — dictate into the focused app",
                TrayAction.Speak),
            new(state.IsConversing ? "Stop & send (Converse)" : "Converse — talk to the assistant",
                TrayAction.Converse),
            new("Interrupt speech", TrayAction.Interrupt,
                IsEnabled: state.IsConversing || state.Status == "speaking"),
            new(state.Muted ? "Unmute replies" : "Mute replies", TrayAction.ToggleMute, IsChecked: state.Muted)
        };

        entries.Add(new TrayEntry("Input device", TrayAction.SetInputDevice, SeparatorBefore: true)
        {
            Children = DeviceEntries(state.InputDevices, state.InputDevice, TrayAction.SetInputDevice)
        });

        entries.Add(new TrayEntry("Output device", TrayAction.SetOutputDevice)
        {
            Children = DeviceEntries(state.OutputDevices, state.OutputDevice, TrayAction.SetOutputDevice)
        });

        entries.Add(new TrayEntry($"Persona: {state.ActivePersona}", TrayAction.SetPersona)
        {
            Children = state.Personas.Select(p => new TrayEntry(p, TrayAction.SetPersona, p, p == state.ActivePersona)).ToList()
        });

        entries.Add(new TrayEntry(state.HotkeysEnabled ? "Hotkeys enabled" : "Hotkeys paused",
            TrayAction.ToggleHotkeys, IsChecked: state.HotkeysEnabled, SeparatorBefore: true));

        entries.Add(new TrayEntry("Replay last reply", TrayAction.ReplayLast, IsEnabled: state.HasLastReply));
        entries.Add(new TrayEntry("Settings…", TrayAction.Settings));
        entries.Add(new TrayEntry("Quit", TrayAction.Quit, SeparatorBefore: true));

        return entries;
    }

    private static List<TrayEntry> DeviceEntries(IReadOnlyList<string> devices, string? current, TrayAction action)
    {
        var list = new List<TrayEntry>
        {
            new(SystemDefault, action, Argument: "", IsChecked: string.IsNullOrWhiteSpace(current))
        };
        list.AddRange(devices.Select(d => new TrayEntry(d, action, d, IsChecked: d == current)));
        if (devices.Count == 0)
            list.Add(new TrayEntry("(no devices found)", action, IsEnabled: false));
        return list;
    }

    /// <summary>Tooltip text: the app name plus live status.</summary>
    public static string Tooltip(TrayState state) => $"Conversation — {state.Status}";
}
