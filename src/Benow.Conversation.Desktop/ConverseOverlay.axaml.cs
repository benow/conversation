using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Benow.Conversation.Personas;

namespace Benow.Conversation.Desktop;

/// <summary>
/// The Converse surface: live transcript, streaming reply, mute/interrupt, typed input, and
/// persona selection (the tray's fan-out lives in the tray menu; this is the keyboard-friendly
/// path to the same action — and the fallback on desktops without tray support).
/// </summary>
public sealed partial class ConverseOverlay : Window
{
    private readonly List<string> _lines = new();
    private bool _personasLoaded;

    public ConverseOverlay()
    {
        InitializeComponent();

        var host = App.Host;
        host.StateChanged += state => Dispatcher.UIThread.Post(() => Apply(state));
        host.ReplyChunk += chunk => Dispatcher.UIThread.Post(() => Append(chunk));
        host.Error += message => Dispatcher.UIThread.Post(() => AppendLine($"⚠ {message}"));

        MuteButton.Click += (_, _) => _ = host.DispatchAsync(TrayAction.ToggleMute);
        InterruptButton.Click += (_, _) => _ = host.DispatchAsync(TrayAction.Interrupt);
        ReplayButton.Click += (_, _) => _ = host.DispatchAsync(TrayAction.ReplayLast);
        SettingsButton.Click += (_, _) => App.OpenSettings();
        SpeakButton.Click += (_, _) => _ = host.DispatchAsync(TrayAction.Speak);
        ConverseButton.Click += (_, _) => _ = host.DispatchAsync(TrayAction.Converse);
        SendButton.Click += (_, _) => SendTyped();
        InputBox.KeyDown += OnInputKeyDown;
        PersonaBox.SelectionChanged += OnPersonaChanged;
        Opened += (_, _) => { LoadPersonas(); InputBox.Focus(); };

        Apply(host.State);
    }

    private void LoadPersonas()
    {
        if (_personasLoaded) return;
        var host = App.Host;
        PersonaBox.ItemsSource = host.Runtime.Config is null
            ? Array.Empty<string>()
            : host.State.Personas.Count > 0
                ? host.State.Personas
                : new List<string> { PersonaStore.DefaultName };
        PersonaBox.SelectedItem = host.State.ActivePersona;
        _personasLoaded = true;
    }

    private void OnPersonaChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PersonaBox.SelectedItem is string name && name != App.Host.State.ActivePersona)
            _ = App.Host.DispatchAsync(TrayAction.SetPersona, name);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            SendTyped();
        }
    }

    private void SendTyped()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        InputBox.Clear();
        AppendLine($"> {text}");
        _ = Task.Run(() => App.Host.SendAsync(text));
    }

    private void Apply(TrayState state)
    {
        StatusText.Text = state.Status;
        MuteButton.Content = state.Muted ? "Unmute" : "Mute";
        PersonaBox.SelectedItem = _personasLoaded ? state.ActivePersona : PersonaBox.SelectedItem;
        if (_personasLoaded && state.Personas.Count > 0 && PersonaBox.ItemCount != state.Personas.Count)
            PersonaBox.ItemsSource = state.Personas;
    }

    private void Append(string chunk)
    {
        if (_lines.Count == 0) _lines.Add("");
        _lines[^1] += chunk;
        Render();
    }

    private void AppendLine(string line)
    {
        if (_lines.Count > 0 && _lines[^1].Length == 0) _lines[^1] = line;
        else _lines.Add(line);
        Render();
    }

    private void Render()
    {
        Transcript.Text = string.Join("\n", _lines);
        Scroller.ScrollToEnd();
    }
}
