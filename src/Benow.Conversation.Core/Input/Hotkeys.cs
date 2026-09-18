namespace Benow.Conversation.Input;

/// <summary>Actions a global trigger can fire (plan: two hotkeys, two verbs).</summary>
public enum HotkeyAction { Speak, Converse, Interrupt }

/// <summary>
/// A parsed hotkey binding: modifiers + a main key. The matcher is pure so the parsing and
/// matching rules are unit-testable without touching /dev/input.
/// </summary>
public sealed record HotkeyBinding(HotkeyAction Action, bool Ctrl, bool Shift, bool Alt, bool Meta, int KeyCode, string Source)
{
    /// <summary>
    /// Parses "Ctrl+Space", "Ctrl+Shift+Space", "Meta+K", "F9", "MediaPlayPause" into a binding.
    /// Unknown names throw with the accepted list so the config UI can show a useful message.
    /// </summary>
    public static HotkeyBinding Parse(HotkeyAction action, string text)
    {
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException($"empty hotkey for {action}");

        bool ctrl = false, shift = false, alt = false, meta = false;
        var keyCode = -1;
        foreach (var raw in parts)
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "meta" or "super" or "win": meta = true; break;
                default:
                    if (keyCode >= 0) throw new ArgumentException($"'{text}' has more than one main key");
                    keyCode = KeyNameToCode(raw);
                    break;
            }
        }
        if (keyCode < 0) throw new ArgumentException($"'{text}' has modifiers but no main key");
        return new HotkeyBinding(action, ctrl, shift, alt, meta, keyCode, text);
    }

    /// <summary>Linux input-event key codes (linux/input-event-codes.h) for the supported keys.</summary>
    public static int KeyNameToCode(string name) => name.ToLowerInvariant() switch
    {
        "space" => 57,
        "enter" or "return" => 28,
        "tab" => 15,
        "escape" or "esc" => 1,
        "backspace" => 14,
        "insert" => 110,
        "delete" => 111,
        "home" => 102,
        "end" => 107,
        "pageup" => 104,
        "pagedown" => 109,
        "left" => 105,
        "right" => 106,
        "up" => 103,
        "down" => 108,
        "grave" => 41,
        "minus" => 12,
        "equal" => 13,
        "comma" => 51,
        "period" => 52,
        "slash" => 53,
        "semicolon" => 39,
        "apostrophe" => 40,
        "leftbracket" => 26,
        "rightbracket" => 27,
        "backslash" => 43,
        // Media / AVRCP keys — this is how an earbud or headset button arrives (KEY_PLAYPAUSE
        // etc. via uinput/evdev). On Windows these surface through SharpHook instead.
        "mediaplaypause" or "playpause" => 164,
        "medianext" or "nexttrack" => 163,
        "mediaprev" or "prevtrack" => 165,
        // Function keys
        "f1" or "f2" or "f3" or "f4" or "f5" or "f6" or "f7" or "f8" or "f9" or "f10" or "f11" or "f12"
            => 59 + (int.Parse(name[1..]) - 1),
        // Single characters (a-z)
        _ when name.Length == 1 && char.IsLetter(name[0]) => 30 + LetterOffset(char.ToLowerInvariant(name[0])),
        _ when name.Length == 1 && char.IsDigit(name[0]) => name[0] == '0' ? 11 : 1 + (name[0] - '1'),
        _ => throw new ArgumentException($"unknown key '{name}' — use e.g. Space, K, F9, MediaPlayPause")
    };

    private static int LetterOffset(char c) => c switch
    {
        'a' => 0, 'b' => 3, 'c' => 2, 'd' => 2, 'e' => 2, 'f' => 3, 'g' => 4, 'h' => 5, 'i' => 7,
        'j' => 6, 'k' => 7, 'l' => 8, 'm' => 6, 'n' => 5, 'o' => 8, 'p' => 9, 'q' => 0, 'r' => 3,
        's' => 1, 't' => 4, 'u' => 6, 'v' => 3, 'w' => 1, 'x' => 1, 'y' => 5, 'z' => 0,
        _ => 0
    };
}

/// <summary>Hotkey configuration (defaults: Ctrl+Space = Speak, Ctrl+Shift+Space = Converse).</summary>
public sealed class HotkeyConfig
{
    public List<string> Speaks { get; set; } = new() { "Ctrl+Space" };
    public List<string> Converses { get; set; } = new() { "Ctrl+Shift+Space" };
    /// <summary>Media-key bindings (earbud/headset AVRCP buttons).</summary>
    public List<string> MediaSpeaks { get; set; } = new() { "MediaPlayPause" };
    public List<string> MediaConverses { get; set; } = new() { "MediaNext" };

    public IEnumerable<HotkeyBinding> Bindings()
    {
        foreach (var s in Speaks) yield return HotkeyBinding.Parse(HotkeyAction.Speak, s);
        foreach (var s in Converses) yield return HotkeyBinding.Parse(HotkeyAction.Converse, s);
        foreach (var s in MediaSpeaks) yield return HotkeyBinding.Parse(HotkeyAction.Speak, s);
        foreach (var s in MediaConverses) yield return HotkeyBinding.Parse(HotkeyAction.Converse, s);
    }
}

/// <summary>
/// Pure key-state tracker: fed raw evdev events, raises an action when a binding's exact
/// modifier state + key press occurs. Kept separate from device I/O so it is fully testable
/// (and reusable if a Windows backend reports the same normalized events).
/// </summary>
public sealed class HotkeyMatcher
{
    private const int EvKey = 1;
    private const int KeyPress = 1;
    private const int KeyRepeat = 2;

    private readonly List<HotkeyBinding> _bindings;
    private readonly HashSet<int> _pressed = new();
    private bool _ctrl, _shift, _alt, _meta;

    public HotkeyMatcher(IEnumerable<HotkeyBinding> bindings) => _bindings = bindings.ToList();

    /// <summary>Feeds one raw evdev key event; returns the action when a binding matches.</summary>
    public HotkeyAction? Feed(ushort type, ushort code, int value)
    {
        if (type != EvKey) return null;

        if (value == KeyPress || value == KeyRepeat)
        {
            switch (code)
            {
                case 29 or 97: _ctrl = true; break;
                case 42 or 54: _shift = true; break;
                case 56 or 100: _alt = true; break;
                case 125 or 126: _meta = true; break;
            }
        }
        if (value is 0 or 1 or 2)
        {
            if (value == 0)
            {
                switch (code)
                {
                    case 29 or 97: _ctrl = false; break;
                    case 42 or 54: _shift = false; break;
                    case 56 or 100: _alt = false; break;
                    case 125 or 126: _meta = false; break;
                }
            }
        }

        // Fire on the main key's press (ignore repeats so holding the combo doesn't retrigger).
        if (type != EvKey || value != KeyPress) return null;
        foreach (var b in _bindings)
        {
            if (b.KeyCode != code) continue;
            if (b.Ctrl != _ctrl || b.Shift != _shift || b.Alt != _alt || b.Meta != _meta) continue;
            return b.Action;
        }
        return null;
    }
}
