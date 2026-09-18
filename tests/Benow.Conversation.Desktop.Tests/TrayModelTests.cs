using Benow.Conversation.Desktop;
using Xunit;

namespace Benow.Conversation.Desktop.Tests;

/// <summary>
/// Tray menu shape and state mapping. These run without a windowing system because
/// <see cref="TrayModel"/> is deliberately toolkit-free — the Avalonia layer only translates the
/// entries it produces into NativeMenuItems.
/// </summary>
public class TrayModelTests
{
    private static readonly TrayState Idle = new()
    {
        Personas = new[] { "Default", "Assistant", "Storyteller" },
        ActivePersona = "Assistant",
        InputDevices = new[] { "alsa_input.usb-mic", "alsa_input.pci-analog" },
        InputDevice = "alsa_input.usb-mic",
        OutputDevices = new[] { "alsa_output.hdmi" }
    };

    private static TrayEntry Find(IReadOnlyList<TrayEntry> entries, TrayAction action) =>
        entries.First(e => e.Action == action);

    [Fact]
    public void Build_ExposesBothVerbsAndQuit()
    {
        var entries = TrayModel.Build(Idle);

        Assert.Contains(entries, e => e.Action == TrayAction.Speak);
        Assert.Contains(entries, e => e.Action == TrayAction.Converse);
        Assert.Equal("Quit", entries[^1].Label);
        Assert.Equal(TrayAction.Quit, entries[^1].Action);
    }

    [Fact]
    public void Build_WhileRecording_LabelsTheVerbAsStop()
    {
        var entries = TrayModel.Build(Idle with { IsRecording = true });
        Assert.StartsWith("Stop & paste", Find(entries, TrayAction.Speak).Label);

        var conversing = TrayModel.Build(Idle with { IsConversing = true });
        Assert.StartsWith("Stop & send", Find(conversing, TrayAction.Converse).Label);
    }

    [Fact]
    public void Build_ReflectsMuteAsACheckmark()
    {
        Assert.False(Find(TrayModel.Build(Idle), TrayAction.ToggleMute).IsChecked);
        Assert.True(Find(TrayModel.Build(Idle with { Muted = true }), TrayAction.ToggleMute).IsChecked);
    }

    [Fact]
    public void Build_InputDeviceSubmenu_ChecksCurrentAndOffersSystemDefault()
    {
        var submenu = Find(TrayModel.Build(Idle), TrayAction.SetInputDevice).Children;

        Assert.Equal(TrayModel.SystemDefault, submenu[0].Label);
        Assert.False(submenu[0].IsChecked);
        Assert.Equal("", submenu[0].Argument);
        Assert.True(submenu.Single(e => e.Label == "alsa_input.usb-mic").IsChecked);
        Assert.Equal("alsa_input.usb-mic", submenu.Single(e => e.Label == "alsa_input.usb-mic").Argument);
        Assert.False(submenu.Single(e => e.Label == "alsa_input.pci-analog").IsChecked);
    }

    [Fact]
    public void Build_NoDeviceSelected_ChecksSystemDefault()
    {
        var submenu = Find(TrayModel.Build(Idle with { InputDevice = null }), TrayAction.SetInputDevice).Children;
        Assert.True(submenu[0].IsChecked);
    }

    [Fact]
    public void Build_NoDevicesFound_ShowsADisabledPlaceholder()
    {
        var submenu = Find(TrayModel.Build(Idle with { InputDevices = Array.Empty<string>() }), TrayAction.SetInputDevice)
            .Children;
        var placeholder = submenu.Single(e => e.Label.Contains("no devices"));
        Assert.False(placeholder.IsEnabled);
    }

    [Fact]
    public void Build_PersonasFanOutWithTheActiveOneChecked()
    {
        var entry = Find(TrayModel.Build(Idle), TrayAction.SetPersona);

        Assert.Contains("Assistant", entry.Label);
        Assert.Equal(new[] { "Default", "Assistant", "Storyteller" }, entry.Children.Select(c => c.Label));
        Assert.True(entry.Children.Single(c => c.Label == "Assistant").IsChecked);
        Assert.All(entry.Children, c => Assert.Equal(TrayAction.SetPersona, c.Action));
    }

    [Fact]
    public void Build_ReplayIsDisabledUntilThereIsAReply()
    {
        Assert.False(Find(TrayModel.Build(Idle), TrayAction.ReplayLast).IsEnabled);
        Assert.True(Find(TrayModel.Build(Idle with { HasLastReply = true }), TrayAction.ReplayLast).IsEnabled);
    }

    [Fact]
    public void Build_HotkeysToggleCarriesTheEnabledState()
    {
        var paused = Find(TrayModel.Build(Idle with { HotkeysEnabled = false }), TrayAction.ToggleHotkeys);
        Assert.False(paused.IsChecked);
        Assert.Equal("Hotkeys paused", paused.Label);
    }

    [Fact]
    public void Tooltip_CarriesTheLiveStatus()
    {
        Assert.Equal("Conversation — listening (Converse)…",
            TrayModel.Tooltip(Idle with { Status = "listening (Converse)…" }));
    }
}
