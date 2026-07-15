using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace benow_conversation.Services.Stt;

/// <summary>
/// Lightweight evdev keyboard hotkey trigger for clipboard TTS.
/// Takes key spec and debounce as constructor parameters (not from IOptions).
/// </summary>
public class ClipboardTtsTrigger : IDisposable
{
    private const ushort EV_KEY = 0x01;
    private const int INPUT_EVENT_SIZE = 24;

    private readonly ILogger<ClipboardTtsTrigger> _logger;
    private readonly int _debounceMs;
    private readonly HashSet<ushort> _modifierCodes;
    private readonly ushort _triggerKeyCode;
    private readonly string _keySpec;
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

    public ClipboardTtsTrigger(string keySpec, int debounceMs, ILogger<ClipboardTtsTrigger> logger)
    {
        _logger = logger;
        _keySpec = keySpec;
        _debounceMs = debounceMs;

        var (modifiers, triggerKey) = EvdevKeyboardTrigger.ParseKeySpec(keySpec);
        _modifierCodes = modifiers;
        _triggerKeyCode = triggerKey;

        _logger.LogInformation("[ClipboardTts:Trigger] Key binding: {KeySpec} → mods=[{Mods}] trigger={Code}",
            keySpec,
            string.Join("+", _modifierCodes.Select(c =>
                EvdevKeyboardTrigger.KeyNameToCode.FirstOrDefault(k => k.Value == c).Key ?? c.ToString())),
            EvdevKeyboardTrigger.KeyNameToCode.FirstOrDefault(k => k.Value == _triggerKeyCode).Key ?? _triggerKeyCode.ToString());

        _isAvailable = Initialize();
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

    // ─── Initialization ────────────────────────────────────────────────

    private bool Initialize()
    {
        if (!Directory.Exists("/dev/input"))
        {
            _logger.LogWarning("[ClipboardTts:Trigger] /dev/input does not exist");
            return false;
        }

        var keyboards = LinuxInterop.ParseProcBusInputDevices()
            .Where(kvp => IsKeyboard(kvp.Value))
            .ToList();

        if (keyboards.Count == 0)
        {
            _logger.LogWarning("[ClipboardTts:Trigger] No keyboard devices found");
            return false;
        }

        var monitorCount = 0;
        foreach (var (path, name) in keyboards)
        {
            var fd = LinuxInterop.open(path, LinuxInterop.O_RDONLY);
            if (fd < 0)
            {
                _logger.LogDebug("[ClipboardTts:Trigger] Cannot open {Device} ({Name}): errno={Errno}", path, name,
                    Marshal.GetLastWin32Error());
                continue;
            }

            lock (_pathToFdLock) _pathToFd[path] = fd;
            _logger.LogInformation("[ClipboardTts:Trigger] Monitoring: {Device} ({Name})", path, name);
            StartReader(fd, path);
            monitorCount++;
        }

        if (monitorCount == 0)
        {
            _logger.LogWarning("[ClipboardTts:Trigger] Could not open any keyboard device (need 'input' group?)");
            return false;
        }

        StartHeartbeat();
        return true;
    }

    // ─── Reader (simple poll loop) ───────────────────────────────────

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
                        _logger.LogDebug("[ClipboardTts:Trigger] poll() error on {Path}: errno={Errno}", path, errno);
                        break;
                    }
                    if (result == 0) continue;
                    if ((pollFds[0].revents & (LinuxInterop.POLLERR | LinuxInterop.POLLHUP)) != 0)
                    {
                        _logger.LogDebug("[ClipboardTts:Trigger] {Path} disconnected", path);
                        break;
                    }
                    if ((pollFds[0].revents & LinuxInterop.POLLIN) == 0) continue;

                    var bytesRead = (int)LinuxInterop.read(fd, buffer, new IntPtr(INPUT_EVENT_SIZE));
                    if (bytesRead < INPUT_EVENT_SIZE) continue;

                    var type = BitConverter.ToUInt16(buffer, 16);
                    var code = BitConverter.ToUInt16(buffer, 18);
                    var value = BitConverter.ToInt32(buffer, 20);

                    if (type != EV_KEY) continue;

                    if (EvdevKeyboardTrigger.ModifierCodes.Contains(code))
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
                        _logger.LogInformation("[ClipboardTts:Trigger] Hotkey detected on {Path}", path);
                        _channel.Writer.TryWrite(true);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[ClipboardTts:Trigger] Reader error on {Path}: {Message}", path, ex.Message);
                    break;
                }
            }

            _logger.LogDebug("[ClipboardTts:Trigger] Reader stopped for {Path}", path);
        }, _readCts.Token);

        _readerTasks.Add(task);
    }

    // ─── Heartbeat (rescans keyboard devices) ──────────────────────────

    private void StartHeartbeat()
    {
        _readerTasks.Add(Task.Run(async () =>
        {
            while (!_readCts.Token.IsCancellationRequested)
            {
                try { SyncKeyboardDevices(); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[ClipboardTts:Trigger] Heartbeat error: {Message}", ex.Message);
                }

                try { await Task.Delay(2000, _readCts.Token); }
                catch { break; }
            }

            _logger.LogDebug("[ClipboardTts:Trigger] Heartbeat stopped");
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
            var gone = _pathToFd.Keys.Except(currentKeyboards, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var path in gone)
            {
                try { LinuxInterop.close(_pathToFd[path]); } catch { }
                _pathToFd.Remove(path);
                _logger.LogDebug("[ClipboardTts:Trigger] Stopped monitoring {Path} (device gone)", path);
            }

            foreach (var path in currentKeyboards)
            {
                if (_pathToFd.ContainsKey(path)) continue;
                var fd = LinuxInterop.open(path, LinuxInterop.O_RDONLY);
                if (fd >= 0)
                {
                    _pathToFd[path] = fd;
                    _logger.LogInformation("[ClipboardTts:Trigger] Started monitoring new keyboard {Path}", path);
                    StartReader(fd, path);
                }
            }
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private static bool IsKeyboard(string deviceName)
    {
        if (!deviceName.Contains("keyboard", StringComparison.OrdinalIgnoreCase))
            return false;
        if (deviceName.Contains("ydotoold", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("Controller", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
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
