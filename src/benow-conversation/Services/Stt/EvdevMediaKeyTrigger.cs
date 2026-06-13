using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using benow_conversation.Configuration;
using benow_conversation.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services.Stt;

public partial class EvdevMediaKeyTrigger : IRecordingTrigger, IDisposable
{
    private const ushort EV_KEY = 0x01;
    private const ushort KEY_PLAYPAUSE = 164;
    private const ushort KEY_MEDIA = 226;
    private const ushort KEY_PLAYCD = 200;
    private const ushort KEY_PAUSECD = 201;
    private const int INPUT_EVENT_SIZE = 24;

    private static readonly HashSet<ushort> DefaultTriggerCodes =
    [
        KEY_PLAYPAUSE, KEY_MEDIA, KEY_PLAYCD, KEY_PAUSECD
    ];

    private readonly ILogger<EvdevMediaKeyTrigger> _logger;
    private readonly int _debounceMs;
    private readonly HashSet<ushort> _triggerCodes;
    private readonly string? _configuredDevice;
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    private readonly List<int> _fds = [];
    private readonly object _fdsLock = new();
    private readonly List<Task> _readerTasks = [];
    private readonly CancellationTokenSource _readCts = new();
    private long _lastTriggerTicks;
    private readonly bool _isAvailable;
    private int _totalKeyEventsLogged;

    public bool IsAvailable => _isAvailable;

    public EvdevMediaKeyTrigger(IOptions<AppSettings> settings, ILogger<EvdevMediaKeyTrigger> logger)
    {
        _logger = logger;
        _debounceMs = settings.Value.Stt.TriggerDebounceMs;
        _configuredDevice = settings.Value.Stt.TriggerDevice;
        _triggerCodes = ParseTriggerCodes(settings.Value.Stt.TriggerCodes) ?? DefaultTriggerCodes;
        _isAvailable = Initialize();
    }

    private static HashSet<ushort>? ParseTriggerCodes(string? codes)
    {
        if (string.IsNullOrWhiteSpace(codes))
            return null;

        var result = new HashSet<ushort>();
        foreach (var part in codes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ushort.TryParse(part, out var code))
                result.Add(code);
        }

        return result.Count > 0 ? result : null;
    }

    private bool Initialize()
    {
        _logger.LogInformation("[Trigger] Initializing evdev media key monitor (using poll/read)...");

        if (!Directory.Exists("/dev/input"))
        {
            _logger.LogWarning("[Trigger] /dev/input does not exist — evdev not available");
            return false;
        }

        var deviceNames = LinuxInterop.ParseProcBusInputDevices();
        var btDevices = deviceNames.Where(kvp => kvp.Value.Contains("[BT]")).ToList();
        var avrcpDevices = deviceNames.Where(kvp => kvp.Value.Contains("AVRCP")).ToList();

        _logger.LogInformation("[Trigger] Found {Total} input devices, {Bt} Bluetooth, {Avrcp} AVRCP",
            deviceNames.Count, btDevices.Count, avrcpDevices.Count);

        foreach (var (path, name) in avrcpDevices)
            _logger.LogInformation("[Trigger] AVRCP device: {Path} ({Name})", path, name);

        foreach (var (path, name) in btDevices.Where(b => !b.Value.Contains("AVRCP")))
            _logger.LogDebug("[Trigger] BT non-AVRCP: {Path} ({Name})", path, name);

        List<string> devicesToOpen;

        if (!string.IsNullOrWhiteSpace(_configuredDevice))
        {
            var resolved = ResolveConfiguredDevice(deviceNames);
            if (resolved != null)
            {
                devicesToOpen = [resolved];
            }
            else
            {
                _logger.LogWarning("[Trigger] Configured device '{Device}' not found — falling back to scanning all", _configuredDevice);
                devicesToOpen = GetAllEventFiles();
            }
        }
        else
        {
            devicesToOpen = GetAllEventFiles();
        }

        _logger.LogInformation("[Trigger] Opening {Count} device(s)", devicesToOpen.Count);

        var monitorCount = 0;
        var accessDenied = 0;

        foreach (var devicePath in devicesToOpen)
        {
            var deviceName = deviceNames.GetValueOrDefault(devicePath, "unknown");

            var fd = LinuxInterop.open(devicePath, LinuxInterop.O_RDONLY);
            if (fd < 0)
            {
                var errno = Marshal.GetLastWin32Error();
                if (errno == LinuxInterop.EACCES)
                {
                    accessDenied++;
                    _logger.LogDebug("[Trigger] No access to {Device} ({Name}) — user needs 'input' group", devicePath, deviceName);
                }
                else
                {
                    _logger.LogDebug("[Trigger] Cannot open {Device} ({Name}): errno={Errno}", devicePath, deviceName, errno);
                }
                continue;
            }

            _fds.Add(fd);
            _logger.LogInformation("[Trigger] Monitoring: {Device} ({Name})", devicePath, deviceName);
            StartDeviceReader(fd, devicePath, deviceName);
            monitorCount++;
        }

        if (monitorCount == 0)
        {
            if (accessDenied > 0)
            {
                _logger.LogError("[Trigger] {Count} device(s) found but ALL denied access. Fix: sudo usermod -aG input $USER then re-login",
                    accessDenied);
                _logger.LogInformation("[Trigger] Verify with: groups | grep input");
            }
            else
            {
                _logger.LogWarning("[Trigger] No /dev/input/event* devices found");
            }
            return false;
        }

        if (avrcpDevices.Count == 0 && monitorCount > 0)
        {
            _logger.LogWarning("[Trigger] No AVRCP device detected — Bluetooth earbuds may not be connected");
            _logger.LogInformation("[Trigger] Check with: grep -i avrcp /proc/bus/input/devices");
        }

        _logger.LogInformation("[Trigger] Monitoring {Count} device(s) for codes: {Codes}",
            monitorCount, string.Join(", ", _triggerCodes.Select(c => $"{c} ({LinuxInterop.GetKeyCodeName((ushort)c)})")));
        return true;
    }

    private string? ResolveConfiguredDevice(Dictionary<string, string> deviceNames)
    {
        if (_configuredDevice!.StartsWith("/dev/input/event") && File.Exists(_configuredDevice))
        {
            _logger.LogInformation("[Trigger] Using configured device path: {Device}", _configuredDevice);
            return _configuredDevice;
        }

        foreach (var (path, name) in deviceNames)
        {
            var compareName = name.Replace(" [BT]", "");
            if (string.Equals(compareName, _configuredDevice, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, _configuredDevice, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[Trigger] Resolved device '{Name}' → {Path}", _configuredDevice, path);
                return path;
            }
        }

        foreach (var (path, name) in deviceNames)
        {
            var compareName = name.Replace(" [BT]", "");
            if (compareName.Contains(_configuredDevice, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(_configuredDevice, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[Trigger] Resolved device (partial match) '{Name}' → {Path}", _configuredDevice, path);
                return path;
            }
        }

        return null;
    }

    private static List<string> GetAllEventFiles()
    {
        return Directory.GetFiles("/dev/input", "event*")
            .OrderBy(f => int.TryParse(Regex.Match(f, @"\d+").Value, out var n) ? n : int.MaxValue)
            .ToList();
    }

    private void StartDeviceReader(int fd, string devicePath, string deviceName)
    {
        var task = Task.Run(() =>
        {
            var buffer = new byte[INPUT_EVENT_SIZE];
            var retryDelay = 1000;
            var fdsTrackedFd = fd;
            var currentPath = devicePath;

            _logger.LogDebug("[Trigger] Reader started for {Device} (fd={Fd})", currentPath, fd);

            while (!_readCts.Token.IsCancellationRequested)
            {
                var pfd = new LinuxInterop.PollFd { fd = fd, events = LinuxInterop.POLLIN };
                var pollFds = new[] { pfd };
                var shouldReconnect = false;

                while (!_readCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        pollFds[0].fd = fd;
                        var pollResult = LinuxInterop.poll(pollFds, new UIntPtr(1), 1000);

                        if (pollResult < 0)
                        {
                            var errno = Marshal.GetLastWin32Error();
                            if (errno == LinuxInterop.EINTR) continue;
                            _logger.LogError("[Trigger] poll() error on {Device}: errno={Errno}", currentPath, errno);
                            shouldReconnect = true;
                            break;
                        }

                        if (pollResult == 0) continue;

                        if ((pollFds[0].revents & (LinuxInterop.POLLERR | LinuxInterop.POLLHUP)) != 0)
                        {
                            _logger.LogWarning("[Trigger] Device {Device} disconnected (revents={Events})", currentPath, pollFds[0].revents);
                            shouldReconnect = true;
                            break;
                        }

                        if ((pollFds[0].revents & LinuxInterop.POLLIN) == 0) continue;

                        var bytesRead = (int)LinuxInterop.read(fd, buffer, new IntPtr(INPUT_EVENT_SIZE));

                        if (bytesRead < 0)
                        {
                            var errno = Marshal.GetLastWin32Error();
                            if (errno == LinuxInterop.EINTR) continue;
                            _logger.LogError("[Trigger] read() error on {Device}: errno={Errno}", currentPath, errno);
                            shouldReconnect = true;
                            break;
                        }

                        if (bytesRead == 0)
                        {
                            _logger.LogWarning("[Trigger] EOF on {Device} — device disconnected?", currentPath);
                            shouldReconnect = true;
                            break;
                        }

                        if (bytesRead < INPUT_EVENT_SIZE)
                        {
                            _logger.LogDebug("[Trigger] Partial read ({Read}/{Expected}) on {Device}", bytesRead, INPUT_EVENT_SIZE, currentPath);
                            continue;
                        }

                        var type = BitConverter.ToUInt16(buffer, 16);
                        var code = BitConverter.ToUInt16(buffer, 18);
                        var value = BitConverter.ToInt32(buffer, 20);

                        if (type == EV_KEY)
                        {
                            var logged = Interlocked.Increment(ref _totalKeyEventsLogged);
                            if (logged <= 20)
                            {
                                _logger.LogInformation("[Trigger] KEY event on {Device}: code={Code} ({Name}), value={Value} (press=1, release=0, repeat=2)",
                                    currentPath, code, LinuxInterop.GetKeyCodeName(code), value);
                            }
                            else if (logged == 21)
                            {
                                _logger.LogInformation("[Trigger] Suppressing further KEY event logs (logged {Count} events)", logged);
                            }
                        }

                        if (type != EV_KEY || value != 1)
                            continue;

                        if (_triggerCodes.Contains(code))
                        {
                            var now = DateTime.UtcNow.Ticks;
                            var last = Interlocked.Read(ref _lastTriggerTicks);
                            var elapsed = TimeSpan.FromTicks(now - last).TotalMilliseconds;

                            if (elapsed < _debounceMs)
                            {
                                _logger.LogDebug("[Trigger] Debounced key={Code} ({Name}) on {Device} ({Elapsed:F0}ms < {Debounce}ms)",
                                    code, LinuxInterop.GetKeyCodeName(code), currentPath, elapsed, _debounceMs);
                                continue;
                            }

                            Interlocked.Exchange(ref _lastTriggerTicks, now);
                            _logger.LogInformation("[Trigger] TRIGGER DETECTED on {Device}: key={Code} ({Name})",
                                currentPath, code, LinuxInterop.GetKeyCodeName(code));
                            _channel.Writer.TryWrite(true);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        shouldReconnect = false;
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Trigger] Error reading from {Device}: {Error}", currentPath, ex.Message);
                        shouldReconnect = true;
                        break;
                    }
                }

                if (!shouldReconnect || _readCts.Token.IsCancellationRequested)
                    break;

                try { LinuxInterop.close(fd); } catch { }

                _logger.LogInformation("[Trigger] Reconnecting to {Device} ({Name}) in {Delay}ms...", currentPath, deviceName, retryDelay);
                try { Task.Delay(retryDelay, _readCts.Token).Wait(_readCts.Token); } catch { break; }

                fd = LinuxInterop.open(currentPath, LinuxInterop.O_RDONLY);
                if (fd < 0)
                {
                    var errno = Marshal.GetLastWin32Error();
                    _logger.LogWarning("[Trigger] Reconnect failed for {Device} ({Name}): errno={Errno} — retrying", currentPath, deviceName, errno);

                    if (errno == LinuxInterop.ENOENT && deviceName != "unknown")
                    {
                        var devices = LinuxInterop.ParseProcBusInputDevices();
                        var cleanName = deviceName.Replace(" [BT]", "");
                        var matches = devices
                            .Where(kvp => cleanName.Contains(kvp.Value.Replace(" [BT]", ""), StringComparison.OrdinalIgnoreCase)
                                       || kvp.Value.Replace(" [BT]", "").Contains(cleanName, StringComparison.OrdinalIgnoreCase))
                            .Select(kvp => kvp.Key)
                            .ToList();

                        if (matches.Count > 0)
                        {
                            var anyOpened = false;
                            foreach (var candidate in matches)
                            {
                                if (candidate == currentPath) continue;
                                var testFd = LinuxInterop.open(candidate, LinuxInterop.O_RDONLY);
                                if (testFd >= 0)
                                {
                                    LinuxInterop.close(testFd);
                                    _logger.LogInformation("[Trigger] Device path changed: {OldPath} → {NewPath}", currentPath, candidate);
                                    currentPath = candidate;
                                    anyOpened = true;
                                    break;
                                }
                            }
                            if (!anyOpened)
                                _logger.LogDebug("[Trigger] Name-matched candidates [{Candidates}] but none could be opened", string.Join(", ", matches));
                        }
                        else
                        {
                            _logger.LogWarning("[Trigger] Device '{Name}' not found in /proc/bus/input/devices ({Count} devices total)", deviceName, devices.Count);
                        }
                    }

                    retryDelay = Math.Min(retryDelay * 2, 15000);
                    _logger.LogDebug("[Trigger] Reconnect backoff: next attempt in {Delay}ms", retryDelay);
                    continue;
                }

                _logger.LogInformation("[Trigger] Reconnected to {Device} ({Name}) (new fd={Fd})", currentPath, deviceName, fd);
                lock (_fdsLock)
                {
                    var idx = _fds.IndexOf(fdsTrackedFd);
                    if (idx >= 0)
                    {
                        _fds[idx] = fd;
                        fdsTrackedFd = fd;
                    }
                }
                retryDelay = 1000;
            }

            _logger.LogDebug("[Trigger] Reader stopped for {Device}", currentPath);
        }, _readCts.Token);

        _readerTasks.Add(task);
    }

    public async Task WaitForTriggerAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Trigger] Waiting for media key event...");
        try
        {
            await _channel.Reader.ReadAsync(ct);
            _logger.LogInformation("[Trigger] Trigger event received");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("[Trigger] Wait cancelled");
            throw;
        }
        catch (ChannelClosedException)
        {
            _logger.LogWarning("[Trigger] Channel closed — no more trigger events");
            throw new OperationCanceledException("Trigger channel closed");
        }
    }

    public void Dispose()
    {
        _readCts.Cancel();
        try { Task.WaitAll(_readerTasks.ToArray(), TimeSpan.FromSeconds(2)); } catch { }
        lock (_fdsLock)
        {
            foreach (var fd in _fds)
            {
                try { LinuxInterop.close(fd); } catch { }
            }
            _fds.Clear();
        }
        _readCts.Dispose();
    }
}
