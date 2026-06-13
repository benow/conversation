using System.Runtime.InteropServices;
using System.Threading.Channels;
using benow_conversation.Configuration;
using benow_conversation.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services.Stt;

public class EvdevKeyboardTrigger : IRecordingTrigger, IDisposable
{
    private const ushort EV_KEY = 0x01;
    private const int INPUT_EVENT_SIZE = 24;

    private static readonly Dictionary<string, ushort> KeyNameToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 57, ["Enter"] = 28, ["Esc"] = 1, ["Escape"] = 1,
        ["Tab"] = 15, ["Backspace"] = 14, ["Delete"] = 111, ["Insert"] = 110,
        ["Home"] = 102, ["End"] = 107, ["PageUp"] = 104, ["PageDown"] = 109,
        ["Up"] = 103, ["Down"] = 108, ["Left"] = 105, ["Right"] = 106,
        ["Ctrl"] = 29, ["LeftCtrl"] = 29, ["RightCtrl"] = 97,
        ["Alt"] = 56, ["LeftAlt"] = 56, ["RightAlt"] = 100,
        ["Shift"] = 42, ["LeftShift"] = 42, ["RightShift"] = 54,
        ["Super"] = 125, ["Meta"] = 125,
        ["F1"] = 59, ["F2"] = 60, ["F3"] = 61, ["F4"] = 62,
        ["F5"] = 63, ["F6"] = 64, ["F7"] = 65, ["F8"] = 66,
        ["F9"] = 67, ["F10"] = 68, ["F11"] = 87, ["F12"] = 88,
        ["A"] = 30, ["B"] = 48, ["C"] = 46, ["D"] = 32, ["E"] = 18,
        ["F"] = 33, ["G"] = 34, ["H"] = 35, ["I"] = 23, ["J"] = 36,
        ["K"] = 37, ["L"] = 38, ["M"] = 50, ["N"] = 49, ["O"] = 24,
        ["P"] = 25, ["Q"] = 16, ["R"] = 19, ["S"] = 31, ["T"] = 20,
        ["U"] = 22, ["V"] = 47, ["W"] = 17, ["X"] = 45, ["Y"] = 21, ["Z"] = 44,
        ["0"] = 11, ["1"] = 2, ["2"] = 3, ["3"] = 4, ["4"] = 5,
        ["5"] = 6, ["6"] = 7, ["7"] = 8, ["8"] = 9, ["9"] = 10,
        ["Grave"] = 41, ["Minus"] = 12, ["Equal"] = 13, ["BracketLeft"] = 26,
        ["BracketRight"] = 27, ["Backslash"] = 43, ["Semicolon"] = 39,
        ["Apostrophe"] = 40, ["Comma"] = 51, ["Period"] = 52, ["Slash"] = 53,
        ["CapsLock"] = 58, ["NumLock"] = 69, ["ScrollLock"] = 70,
        ["Print"] = 99, ["PrintScreen"] = 99, ["Pause"] = 119, ["Break"] = 419,
    };

    private static readonly HashSet<ushort> ModifierCodes = [29, 97, 56, 100, 42, 54, 125];

    private readonly ILogger<EvdevKeyboardTrigger> _logger;
    private readonly int _debounceMs;
    private readonly HashSet<ushort> _modifierCodes;
    private readonly ushort _triggerKeyCode;
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    private readonly Dictionary<string, int> _pathToFd = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pathToFdLock = new();
    private readonly List<Task> _readerTasks = [];
    private readonly CancellationTokenSource _readCts = new();
    private long _lastTriggerTicks;
    private readonly bool _isAvailable;

    private readonly HashSet<ushort> _heldModifiers = [];

    public bool IsAvailable => _isAvailable;

    public EvdevKeyboardTrigger(IOptions<AppSettings> settings, ILogger<EvdevKeyboardTrigger> logger)
    {
        _logger = logger;
        _debounceMs = settings.Value.Stt.TriggerDebounceMs;

        var keySpec = settings.Value.Stt.TriggerKey;
        var (modifiers, triggerKey) = ParseKeySpec(keySpec);
        _modifierCodes = modifiers;
        _triggerKeyCode = triggerKey;

        _logger.LogInformation("[Trigger] Key binding: {KeySpec} → mods=[{Mods}] trigger={Trigger}({Code})",
            keySpec, string.Join("+", _modifierCodes.Select(c => KeyNameToCode.FirstOrDefault(k => k.Value == c).Key ?? c.ToString())),
            KeyNameToCode.FirstOrDefault(k => k.Value == _triggerKeyCode).Key ?? _triggerKeyCode.ToString(), _triggerKeyCode);

        _isAvailable = Initialize();
    }

    public static (HashSet<ushort> modifiers, ushort triggerKey) ParseKeySpec(string? keySpec)
    {
        var modifiers = new HashSet<ushort>();
        ushort triggerKey = 57;

        if (string.IsNullOrWhiteSpace(keySpec))
            return (new HashSet<ushort> { 29 }, 57);

        var parts = keySpec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts[..^1])
        {
            if (KeyNameToCode.TryGetValue(part, out var code))
                modifiers.Add(code);
        }

        if (parts.Length > 0 && KeyNameToCode.TryGetValue(parts[^1], out var triggerCode))
            triggerKey = triggerCode;

        if (modifiers.Count == 0)
            modifiers.Add(29);

        return (modifiers, triggerKey);
    }

    // ─── Initialization ────────────────────────────────────────────────

    private bool Initialize()
    {
        if (!Directory.Exists("/dev/input"))
        {
            _logger.LogWarning("[Trigger] /dev/input does not exist");
            return false;
        }

        var keyboards = LinuxInterop.ParseProcBusInputDevices()
            .Where(kvp => IsKeyboard(kvp.Value))
            .ToList();

        if (keyboards.Count == 0)
        {
            _logger.LogWarning("[Trigger] No keyboard devices found");
            return false;
        }

        var monitorCount = 0;
        foreach (var (path, name) in keyboards)
        {
            var fd = LinuxInterop.open(path, LinuxInterop.O_RDONLY);
            if (fd < 0)
            {
                _logger.LogDebug("[Trigger] Cannot open {Device} ({Name}): errno={Errno}", path, name,
                    Marshal.GetLastWin32Error());
                continue;
            }

            lock (_pathToFdLock) _pathToFd[path] = fd;
            _logger.LogInformation("[Trigger] Monitoring: {Device} ({Name})", path, name);
            StartReader(fd, path);
            monitorCount++;
        }

        if (monitorCount == 0)
        {
            _logger.LogWarning("[Trigger] Could not open any keyboard device (need 'input' group?)");
            return false;
        }

        StartHeartbeat();
        return true;
    }

    // ─── Reader (simple poll loop, no reconnect) ───────────────────────

    private void StartReader(int fd, string path)
    {
        var task = Task.Run(() =>
        {
            var buffer = new byte[INPUT_EVENT_SIZE];

            while (!_readCts.Token.IsCancellationRequested)
            {
                try
                {
                    var pfd = new LinuxInterop.PollFd { fd = fd, events = LinuxInterop.POLLIN };
                    var pollFds = new[] { pfd };
                    var result = LinuxInterop.poll(pollFds, new UIntPtr(1), 1000);
                    if (result < 0)
                    {
                        var errno = Marshal.GetLastWin32Error();
                        if (errno == LinuxInterop.EINTR) continue;
                        _logger.LogDebug("[Trigger] poll() error on {Path}: errno={Errno}", path, errno);
                        break;
                    }
                    if (result == 0) continue;
                    if ((pollFds[0].revents & (LinuxInterop.POLLERR | LinuxInterop.POLLHUP)) != 0)
                    {
                        _logger.LogDebug("[Trigger] {Path} disconnected", path);
                        break;
                    }
                    if ((pollFds[0].revents & LinuxInterop.POLLIN) == 0) continue;

                    var bytesRead = (int)LinuxInterop.read(fd, buffer, new IntPtr(INPUT_EVENT_SIZE));
                    if (bytesRead < INPUT_EVENT_SIZE) continue;

                    var type = BitConverter.ToUInt16(buffer, 16);
                    var code = BitConverter.ToUInt16(buffer, 18);
                    var value = BitConverter.ToInt32(buffer, 20);

                    if (type != EV_KEY) continue;

                    if (ModifierCodes.Contains(code))
                    {
                        if (value == 1) _heldModifiers.Add(code);
                        else if (value == 0) _heldModifiers.Remove(code);
                        continue;
                    }

                    if (code == _triggerKeyCode && value == 1 &&
                        _modifierCodes.All(m => _heldModifiers.Contains(m)))
                    {
                        var now = DateTime.UtcNow.Ticks;
                        var last = Interlocked.Read(ref _lastTriggerTicks);
                        if (TimeSpan.FromTicks(now - last).TotalMilliseconds < _debounceMs) continue;

                        Interlocked.Exchange(ref _lastTriggerTicks, now);
                        _logger.LogInformation("[Trigger] Hotkey detected on {Path}", path);
                        _channel.Writer.TryWrite(true);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Trigger] Reader error on {Path}: {Message}", path, ex.Message);
                    break;
                }
            }

            _logger.LogDebug("[Trigger] Reader stopped for {Path}", path);
        }, _readCts.Token);

        _readerTasks.Add(task);
    }

    // ─── Heartbeat (rescans and diffs keyboard devices) ────────────────

    private void StartHeartbeat()
    {
        _readerTasks.Add(Task.Run(async () =>
        {
            while (!_readCts.Token.IsCancellationRequested)
            {
                try { SyncKeyboardDevices(); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Trigger] Heartbeat error: {Message}", ex.Message);
                }

                try { await Task.Delay(2000, _readCts.Token); }
                catch { break; }
            }

            _logger.LogDebug("[Trigger] Heartbeat stopped");
        }, _readCts.Token));
    }

    private void SyncKeyboardDevices()
    {
        var currentKeyboards = LinuxInterop.ParseProcBusInputDevices()
            .Where(kvp => IsKeyboard(kvp.Value))
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_pathToFdLock)
        {
            // Close FDs for keyboards that no longer exist
            var gone = _pathToFd.Keys.Except(currentKeyboards, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var path in gone)
            {
                try { LinuxInterop.close(_pathToFd[path]); } catch { }
                _pathToFd.Remove(path);
                _logger.LogDebug("[Trigger] Stopped monitoring {Path} (device gone)", path);
            }

            // Open FDs and start readers for new keyboards
            foreach (var path in currentKeyboards)
            {
                if (_pathToFd.ContainsKey(path)) continue;
                var fd = LinuxInterop.open(path, LinuxInterop.O_RDONLY);
                if (fd >= 0)
                {
                    _pathToFd[path] = fd;
                    _logger.LogInformation("[Trigger] Started monitoring new keyboard {Path}", path);
                    StartReader(fd, path);
                }
            }
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private static bool IsKeyboard(string deviceName)
    {
        if (!deviceName.Contains("keyboard", StringComparison.OrdinalIgnoreCase) &&
            !deviceName.Contains("Keyboard", StringComparison.OrdinalIgnoreCase))
            return false;

        if (deviceName.Contains("ydotoold", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("Controller", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public async Task WaitForTriggerAsync(CancellationToken ct)
    {
        try
        {
            await _channel.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ChannelClosedException) { throw new OperationCanceledException("Trigger channel closed"); }
    }

    public void Dispose()
    {
        _readCts.Cancel();
        try { Task.WaitAll(_readerTasks.ToArray(), TimeSpan.FromSeconds(2)); } catch { }
        lock (_pathToFdLock)
        {
            foreach (var fd in _pathToFd.Values)
                try { LinuxInterop.close(fd); } catch { }
            _pathToFd.Clear();
        }
        _readCts.Dispose();
    }
}
