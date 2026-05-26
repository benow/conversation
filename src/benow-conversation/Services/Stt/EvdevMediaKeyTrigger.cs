using System.Text.RegularExpressions;
using System.Threading.Channels;
using benow_conversation.Configuration;
using benow_conversation.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services.Stt;

public partial class EvdevMediaKeyTrigger : IRecordingTrigger
{
    private const ushort EV_KEY = 0x01;
    private const ushort KEY_PLAYPAUSE = 164;
    private const ushort KEY_MEDIA = 226;
    private const int INPUT_EVENT_SIZE = 24;
    private const ushort BUS_BLUETOOTH = 0x05;

    private readonly ILogger<EvdevMediaKeyTrigger> _logger;
    private readonly int _debounceMs;
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    private readonly List<FileStream> _streams = [];
    private readonly List<Task> _readerTasks = [];
    private readonly CancellationTokenSource _readCts = new();
    private long _lastTriggerTicks;
    private readonly bool _isAvailable;

    public bool IsAvailable => _isAvailable;

    public EvdevMediaKeyTrigger(IOptions<AppSettings> settings, ILogger<EvdevMediaKeyTrigger> logger)
    {
        _logger = logger;
        _debounceMs = settings.Value.Stt.TriggerDebounceMs;
        _isAvailable = Initialize();
    }

    private bool Initialize()
    {
        _logger.LogInformation("[Trigger] Initializing evdev media key monitor...");

        if (!Directory.Exists("/dev/input"))
        {
            _logger.LogWarning("[Trigger] /dev/input does not exist — evdev not available");
            return false;
        }

        var deviceNames = ParseProcBusInputDevices();
        var eventFiles = Directory.GetFiles("/dev/input", "event*")
            .OrderBy(f => int.TryParse(Regex.Match(f, @"\d+").Value, out var n) ? n : int.MaxValue)
            .ToList();

        _logger.LogInformation("[Trigger] Found {Count} input devices in /dev/input", eventFiles.Count);

        var monitorCount = 0;

        foreach (var eventFile in eventFiles)
        {
            var deviceName = deviceNames.GetValueOrDefault(eventFile, "unknown");

            FileStream stream;
            try
            {
                stream = new FileStream(eventFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug("[Trigger] No access to {Device}: {Error} (user may need 'input' group)", eventFile, ex.Message);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[Trigger] Cannot open {Device}: {Error}", eventFile, ex.Message);
                continue;
            }

            _streams.Add(stream);
            _logger.LogInformation("[Trigger] Monitoring: {Device} ({Name})", eventFile, deviceName);
            StartDeviceReader(stream, eventFile);
            monitorCount++;
        }

        if (monitorCount == 0)
        {
            _logger.LogWarning("[Trigger] No input devices accessible. Add user to 'input' group: sudo usermod -aG input $USER");
            return false;
        }

        _logger.LogInformation("[Trigger] Monitoring {Count} device(s) for media key events (KEY_PLAYPAUSE={PlayPause}, KEY_MEDIA={Media})", monitorCount, KEY_PLAYPAUSE, KEY_MEDIA);
        return true;
    }

    private void StartDeviceReader(FileStream stream, string devicePath)
    {
        var task = Task.Run(() =>
        {
            var buffer = new byte[INPUT_EVENT_SIZE];
            _logger.LogDebug("[Trigger] Reader started for {Device}", devicePath);

            while (!_readCts.Token.IsCancellationRequested)
            {
                try
                {
                    var read = stream.Read(buffer, 0, INPUT_EVENT_SIZE);
                    if (read < INPUT_EVENT_SIZE)
                        continue;

                    var type = BitConverter.ToUInt16(buffer, 16);
                    var code = BitConverter.ToUInt16(buffer, 18);
                    var value = BitConverter.ToInt32(buffer, 20);

                    if (type != EV_KEY || value != 1)
                        continue;

                    if (code == KEY_PLAYPAUSE || code == KEY_MEDIA)
                    {
                        var now = DateTime.UtcNow.Ticks;
                        var last = Interlocked.Read(ref _lastTriggerTicks);
                        var elapsed = TimeSpan.FromTicks(now - last).TotalMilliseconds;

                        if (elapsed < _debounceMs)
                        {
                            _logger.LogDebug("[Trigger] Debounced media key event on {Device}: code={Code} ({Elapsed:F0}ms < {Debounce}ms)", devicePath, code, elapsed, _debounceMs);
                            continue;
                        }

                        Interlocked.Exchange(ref _lastTriggerTicks, now);
                        var keyName = code == KEY_PLAYPAUSE ? "KEY_PLAYPAUSE" : "KEY_MEDIA";
                        _logger.LogInformation("[Trigger] Media key detected on {Device}: {Key} (code={Code})", devicePath, keyName, code);
                        _channel.Writer.TryWrite(true);
                    }
                }
                catch (ObjectDisposedException)
                {
                    _logger.LogDebug("[Trigger] Stream closed for {Device}", devicePath);
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Trigger] Error reading from {Device}: {Error}", devicePath, ex.Message);
                    break;
                }
            }

            _logger.LogDebug("[Trigger] Reader stopped for {Device}", devicePath);
        }, _readCts.Token);

        _readerTasks.Add(task);
    }

    public async Task WaitForTriggerAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Trigger] Waiting for media key event (earbud stem pinch)...");
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
            _logger.LogWarning("[Trigger] Channel closed — no more trigger events possible");
            throw new OperationCanceledException("Trigger channel closed");
        }
    }

    private static Dictionary<string, string> ParseProcBusInputDevices()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists("/proc/bus/input/devices"))
                return names;

            var lines = File.ReadAllLines("/proc/bus/input/devices");
            string? currentName = null;
            string? currentHandlers = null;
            ushort? currentBus = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("I: Bus="))
                {
                    var busStr = line.Substring(7).Split()[0];
                    ushort.TryParse(busStr, System.Globalization.NumberStyles.HexNumber, null, out var bus);
                    currentBus = bus;
                }
                else if (line.StartsWith("N: Name="))
                {
                    currentName = line[9..].Trim('"');
                }
                else if (line.StartsWith("H: Handlers="))
                {
                    currentHandlers = line[12..];
                }
                else if (string.IsNullOrEmpty(line))
                {
                    if (currentHandlers != null && currentName != null)
                    {
                        foreach (Match m in EventNumberRegex().Matches(currentHandlers))
                        {
                            names[$"/dev/input/event{m.Groups[1].Value}"] = currentName + (currentBus == BUS_BLUETOOTH ? " [BT]" : "");
                        }
                    }

                    currentName = null;
                    currentHandlers = null;
                    currentBus = null;
                }
            }

            if (currentHandlers != null && currentName != null)
            {
                foreach (Match m in EventNumberRegex().Matches(currentHandlers))
                {
                    names[$"/dev/input/event{m.Groups[1].Value}"] = currentName + (currentBus == BUS_BLUETOOTH ? " [BT]" : "");
                }
            }
        }
        catch (Exception)
        {
        }

        return names;
    }

    [GeneratedRegex(@"event(\d+)", RegexOptions.Compiled)]
    private static partial Regex EventNumberRegex();
}
