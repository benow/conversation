using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using benow_conversation.Configuration;

namespace benow_conversation.Services.Stt;

public static class SttSetup
{
    private const ushort EV_KEY = 0x01;
    private const int INPUT_EVENT_SIZE = 24;

    public static async Task<int> RunAsync(string projectRoot)
    {
        Console.WriteLine();
        Console.WriteLine("=== STT Trigger Setup ===");
        Console.WriteLine();

        if (!Directory.Exists("/dev/input"))
        {
            Console.WriteLine("Error: /dev/input does not exist.");
            return 1;
        }

        var deviceNames = LinuxInterop.ParseProcBusInputDevices();
        var deviceList = deviceNames
            .OrderBy(kvp =>
            {
                var m = Regex.Match(kvp.Key, @"\d+");
                return int.TryParse(m.Value, out var n) ? n : int.MaxValue;
            })
            .ToList();

        if (deviceList.Count == 0)
        {
            Console.WriteLine("No input devices found in /dev/input/.");
            return 1;
        }

        Console.WriteLine("  #   Device                      Name");
        Console.WriteLine("  --- ---------------------------- -----------------------------------------------");
        for (var i = 0; i < deviceList.Count; i++)
        {
            var (path, name) = deviceList[i];
            var btMarker = name.Contains("[BT]") ? " *" : "";
            var avrcpMarker = name.Contains("AVRCP") ? " [AVRCP]" : "";
            Console.WriteLine($"  {i + 1,3}  {path,-28} {name}{btMarker}{avrcpMarker}");
        }

        Console.WriteLine();
        Console.WriteLine("  * = Bluetooth   [AVRCP] = Media control device");
        Console.WriteLine();

        var avrcpDevices = deviceList.Where(d => d.Value.Contains("AVRCP")).ToList();
        var btDevices = deviceList.Where(d => d.Value.Contains("[BT]") && !d.Value.Contains("AVRCP")).ToList();
        int? defaultSelection = null;
        if (avrcpDevices.Count == 1)
            defaultSelection = deviceList.IndexOf(avrcpDevices[0]) + 1;
        else if (avrcpDevices.Count == 0 && btDevices.Count == 1)
            defaultSelection = deviceList.IndexOf(btDevices[0]) + 1;

        Console.Write($"Select device number{(defaultSelection.HasValue ? $" (default: {defaultSelection})" : "")}: ");
        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input) && defaultSelection.HasValue)
            input = defaultSelection.Value.ToString();

        if (!int.TryParse(input, out var selection) || selection < 1 || selection > deviceList.Count)
        {
            Console.WriteLine("Invalid selection.");
            return 1;
        }

        var selectedDevice = deviceList[selection - 1];
        Console.WriteLine();
        Console.WriteLine($"Selected: {selectedDevice.Key} ({selectedDevice.Value})");
        Console.WriteLine();
        Console.WriteLine("Listening for key events...");
        Console.WriteLine("Press your earbud stem or button repeatedly now.");
        Console.WriteLine("Press Enter here when done listening.");
        Console.WriteLine();

        var fd = LinuxInterop.open(selectedDevice.Key, LinuxInterop.O_RDONLY);
        if (fd < 0)
        {
            var errno = Marshal.GetLastWin32Error();
            if (errno == LinuxInterop.EACCES)
                Console.WriteLine($"Error: No access to {selectedDevice.Key}. Fix: sudo usermod -aG input $USER then re-login");
            else
                Console.WriteLine($"Error: Cannot open {selectedDevice.Key} (errno={errno})");
            return 1;
        }

        var detectedCodes = new HashSet<ushort>();
        using var listenCts = new CancellationTokenSource();

        var listenTask = Task.Run(() =>
        {
            var buffer = new byte[INPUT_EVENT_SIZE];
            var pfd = new LinuxInterop.PollFd { fd = fd, events = LinuxInterop.POLLIN };
            var pollFds = new[] { pfd };

            try
            {
                while (!listenCts.Token.IsCancellationRequested)
                {
                    var pollResult = LinuxInterop.poll(pollFds, new UIntPtr(1), 200);

                    if (pollResult < 0)
                    {
                        var errno = Marshal.GetLastWin32Error();
                        if (errno == LinuxInterop.EINTR) continue;
                        break;
                    }

                    if (pollResult == 0) continue;

                    if ((pollFds[0].revents & (LinuxInterop.POLLERR | LinuxInterop.POLLHUP)) != 0) break;
                    if ((pollFds[0].revents & LinuxInterop.POLLIN) == 0) continue;

                    var bytesRead = (int)LinuxInterop.read(fd, buffer, new IntPtr(INPUT_EVENT_SIZE));
                    if (bytesRead < INPUT_EVENT_SIZE) continue;

                    var type = BitConverter.ToUInt16(buffer, 16);
                    var code = BitConverter.ToUInt16(buffer, 18);
                    var value = BitConverter.ToInt32(buffer, 20);

                    if (type == EV_KEY)
                    {
                        var label = value switch
                        {
                            1 => "press",
                            0 => "release",
                            2 => "repeat",
                            _ => $"value={value}"
                        };

                        var isNew = detectedCodes.Add(code);
                        var newMarker = isNew ? " NEW" : "";
                        Console.WriteLine($"  code={code,3} ({LinuxInterop.GetKeyCodeName(code),-16}) {label}{newMarker}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }, listenCts.Token);

        Console.ReadLine();
        listenCts.Cancel();

        try { LinuxInterop.close(fd); } catch { }

        try { await listenTask; } catch { }

        if (detectedCodes.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No key events detected. Make sure your device is connected and try again.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("Detected key codes:");
        foreach (var code in detectedCodes.OrderBy(c => c))
            Console.WriteLine($"  {code} ({LinuxInterop.GetKeyCodeName(code)})");
        Console.WriteLine();

        var cleanName = selectedDevice.Value.Replace(" [BT]", "");
        var codeList = string.Join(", ", detectedCodes.OrderBy(c => c));
        Console.WriteLine();
        Console.WriteLine($"  Device name : {cleanName}");
        Console.WriteLine($"  Event path  : {selectedDevice.Key} (may change on reconnect)");
        Console.WriteLine($"  Key codes   : [{codeList}]");
        Console.WriteLine();
        Console.Write($"Save to config? (y/n): ");
        var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (confirm != "y" && confirm != "yes")
        {
            Console.WriteLine("Cancelled.");
            return 0;
        }

        SaveTriggerConfig(projectRoot, cleanName, detectedCodes);

        Console.WriteLine();
        Console.WriteLine("Saved to appsettings.json:");
        Console.WriteLine($"  Stt.TriggerDevice: {cleanName}");
        Console.WriteLine($"  Stt.TriggerCodes: {string.Join(",", detectedCodes.OrderBy(c => c))}");
        Console.WriteLine();
        Console.WriteLine("Setup complete. Run with --stt to start.");

        return 0;
    }

    private static void SaveTriggerConfig(string projectRoot, string devicePath, HashSet<ushort> codes)
    {
        var configPath = Path.Combine(projectRoot, "appsettings.json");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        string json;
        using (var fs = File.OpenRead(configPath))
        {
            var doc = JsonDocument.Parse(fs);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            doc.WriteTo(writer);
            writer.Flush();
            json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        var appSettings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        appSettings.Stt.TriggerDevice = devicePath;
        appSettings.Stt.TriggerCodes = string.Join(",", codes.OrderBy(c => c));

        var outputJson = JsonSerializer.Serialize(appSettings, jsonOptions);
        File.WriteAllText(configPath, outputJson);
    }
}
