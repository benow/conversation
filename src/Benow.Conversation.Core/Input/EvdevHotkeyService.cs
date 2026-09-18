using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Input;

/// <summary>
/// Linux global hotkeys via evdev (/dev/input/event*) — reads the keyboard at the kernel level,
/// so it works under Wayland (which has no global-shortcut protocol) exactly as it does on X11.
/// Port of V1's proven EvdevKeyboardTrigger, generalized to multiple bindings.
///
/// Permission note: reading /dev/input/event* requires membership in the `input` group (or a
/// udev rule). When unavailable the service logs the exact fix and stays inert — the tray menu
/// still works, so the app remains usable without hotkeys.
/// </summary>
public sealed class EvdevHotkeyService : IDisposable
{
    private readonly ILogger<EvdevHotkeyService> _logger;
    private readonly HotkeyMatcher _matcher;
    private readonly List<Task> _readers = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised on the reader tasks when a binding fires.</summary>
    public event Action<HotkeyAction>? Triggered;

    public EvdevHotkeyService(ILogger<EvdevHotkeyService> logger, IEnumerable<HotkeyBinding> bindings)
    {
        _logger = logger;
        _matcher = new HotkeyMatcher(bindings);
    }

    public bool Started { get; private set; }

    public void Start()
    {
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogInformation("[hotkeys] evdev backend is Linux-only — Windows uses the SharpHook backend (polish phase)");
            return;
        }

        var devices = FindKeyboardDevices();
        if (devices.Count == 0)
        {
            _logger.LogWarning("[hotkeys] no readable keyboard event devices found. " +
                "Fix: add your user to the input group (sudo usermod -aG input $USER, then re-login)");
            return;
        }

        foreach (var dev in devices)
        {
            _readers.Add(Task.Run(() => ReadLoopAsync(dev, _cts.Token)));
            _logger.LogInformation("[hotkeys] listening on {Device}", dev);
        }
        Started = true;
    }

    private async Task ReadLoopAsync(string device, CancellationToken ct)
    {
        // struct input_event (64-bit): timeval(16) + type(2) + code(2) + value(4) = 24 bytes
        const int eventSize = 24;
        var buffer = new byte[eventSize * 16];

        try
        {
            await using var stream = new FileStream(device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), ct);
                if (read < eventSize) continue;
                for (var off = 0; off + eventSize <= read; off += eventSize)
                {
                    var type = BitConverter.ToUInt16(buffer, off + 16);
                    var code = BitConverter.ToUInt16(buffer, off + 18);
                    var value = BitConverter.ToInt32(buffer, off + 20);
                    var action = _matcher.Feed(type, code, value);
                    if (action.HasValue)
                    {
                        _logger.LogInformation("[hotkeys] {Action} triggered via {Device}", action.Value, Path.GetFileName(device));
                        Triggered?.Invoke(action.Value);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning("[hotkeys] permission denied reading {Device}. " +
                "Fix: sudo usermod -aG input $USER && re-login (or add a udev rule)", device);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[hotkeys] reader for {Device} stopped: {Error}", device, ex.Message);
        }
    }

    /// <summary>Event devices that look like keyboards (EV_KEY + a kbd handler in /proc/bus/input/devices).</summary>
    internal static List<string> FindKeyboardDevices()
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists("/proc/bus/input/devices")) return result;
            var blocks = File.ReadAllText("/proc/bus/input/devices").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            foreach (var block in blocks)
            {
                if (!block.Contains("EV_KEY") && !block.Contains("kbd")) continue;
                // "H: Handlers=sysrq kbd leds event3"
                var handlersLine = block.Split('\n').FirstOrDefault(l => l.StartsWith("H: Handlers="));
                if (handlersLine == null || !handlersLine.Contains("kbd")) continue;
                var eventName = handlersLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(t => t.StartsWith("event"));
                if (eventName == null) continue;
                var path = "/dev/input/" + eventName;
                if (File.Exists(path)) result.Add(path);
            }
        }
        catch
        {
            // non-Linux or restricted /proc — treated as "no devices"
        }
        return result;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll(_readers.ToArray(), TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
