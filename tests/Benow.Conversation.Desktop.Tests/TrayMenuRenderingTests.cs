using Avalonia.Controls;
using Avalonia.Headless;
using Benow.Conversation.Desktop;
using Xunit;

namespace Benow.Conversation.Desktop.Tests;

/// <summary>
/// Renders the real tray menu on Avalonia's headless platform: proves the toolkit-free
/// <see cref="TrayModel"/> output actually becomes a working native menu (labels, submenus,
/// checkmarks, disabled entries). This is the one area that needs a UI thread; the state logic
/// itself is tested directly in <see cref="TrayModelTests"/>.
/// </summary>
public sealed class TrayMenuRenderingTests
{
    // One session per process: starting an Avalonia app per test is slow and the toolkit does not
    // like being re-initialised concurrently. xunit runs tests within a class sequentially.
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(App)));

    private static async Task<T> OnUi<T>(Func<T> work) =>
        await Session.Value.Dispatch(work, CancellationToken.None);

    private static readonly TrayState State = new()
    {
        Personas = new[] { "Default", "Assistant" },
        ActivePersona = "Assistant",
        InputDevices = new[] { "alsa_input.usb-mic" },
        InputDevice = "alsa_input.usb-mic",
        OutputDevices = Array.Empty<string>(),
        Muted = true,
        HotkeysEnabled = false
    };

    private static List<NativeMenuItem> MenuItems(TrayState state) =>
        App.BuildMenuForTest(state).Items.OfType<NativeMenuItem>().ToList();

    private static List<NativeMenuItem> SubItems(NativeMenuItem item) =>
        item.Menu!.Items.OfType<NativeMenuItem>().ToList();

    private static string Label(NativeMenuItem item) => item.Header?.ToString() ?? "";

    private static NativeMenuItem Find(List<NativeMenuItem> items, string prefix) =>
        items.First(i => Label(i).StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public async Task Menu_MapsTheModelOntoNativeMenuItems()
    {
        var labels = await OnUi(() => MenuItems(State).Select(Label).ToList());

        Assert.Contains("Unmute replies", labels);  // state has Muted = true
        Assert.Contains(labels, l => l.StartsWith("Persona: Assistant", StringComparison.Ordinal));
        Assert.Equal("Quit", labels[^1]);
    }

    [Fact]
    public async Task Menu_RendersSubmenusForDevicesAndPersonas()
    {
        var result = await OnUi(() =>
        {
            var items = MenuItems(State);
            var device = SubItems(Find(items, "Input device")).Select(Label).ToList();
            var personas = SubItems(Find(items, "Persona"));
            return (device, personas.Select(Label).ToList(), personas.Single(i => Label(i) == "Assistant").IsChecked);
        });

        Assert.Equal(new[] { TrayModel.SystemDefault, "alsa_input.usb-mic" }, result.device);
        Assert.Equal(new[] { "Default", "Assistant" }, result.Item2);
        Assert.True(result.Item3);
    }

    [Fact]
    public async Task Menu_CheckableEntriesUseCheckboxToggleType()
    {
        var result = await OnUi(() =>
        {
            var items = MenuItems(State);
            var mute = Find(items, "Unmute replies");
            var hotkeys = Find(items, "Hotkeys");
            return (MuteType: mute.ToggleType, MuteChecked: mute.IsChecked, HotkeyChecked: hotkeys.IsChecked);
        });

        Assert.Equal(MenuItemToggleType.CheckBox, result.MuteType);
        Assert.True(result.MuteChecked);
        Assert.False(result.HotkeyChecked);
    }

    [Fact]
    public async Task Menu_DisabledEntriesStayDisabled()
    {
        var disabled = await OnUi(() => Find(MenuItems(State), "Replay last").IsEnabled);
        Assert.False(disabled);

        var enabled = await OnUi(() => Find(MenuItems(State with { HasLastReply = true }), "Replay last").IsEnabled);
        Assert.True(enabled);
    }

    [Fact]
    public async Task Menu_WhileRecording_ShowsTheStopVerb()
    {
        var label = await OnUi(() => Label(MenuItems(State with { IsRecording = true })[0]));

        Assert.StartsWith("Stop & paste", label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Menu_WithoutDevices_ShowsADisabledPlaceholder()
    {
        var (label, enabled) = await OnUi(() =>
        {
            var items = MenuItems(State with { InputDevices = Array.Empty<string>(), InputDevice = null });
            var placeholder = SubItems(Find(items, "Input device")).Last();
            return (Label(placeholder), placeholder.IsEnabled);
        });

        Assert.Contains("no devices", label);
        Assert.False(enabled);
    }
}
