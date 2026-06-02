using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace benow_conversation.Services.Stt;

internal static class LinuxInterop
{
    internal const int O_RDONLY = 0;
    internal const short POLLIN = 0x0001;
    internal const short POLLERR = 0x0008;
    internal const short POLLHUP = 0x0010;
    internal const ushort BUS_BLUETOOTH = 0x05;
    internal const int EINTR = 4;
    internal const int EACCES = 13;

    [DllImport("libc", SetLastError = true)]
    internal static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    internal static extern IntPtr read(int fd, byte[] buf, IntPtr count);

    [DllImport("libc", SetLastError = true)]
    internal static extern int close(int fd);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [DllImport("libc", SetLastError = true)]
    internal static extern int poll([In, Out] PollFd[] fds, UIntPtr nfds, int timeoutMs);

    internal static readonly Dictionary<ushort, string> KeyCodeNames = new()
    {
        [163] = "KEY_NEXTSONG",
        [164] = "KEY_PLAYPAUSE",
        [165] = "KEY_PREVIOUSSONG",
        [128] = "KEY_STOP",
        [166] = "KEY_RECORD",
        [167] = "KEY_REWIND",
        [181] = "KEY_FORWARD",
        [200] = "KEY_PLAYCD",
        [201] = "KEY_PAUSECD",
        [226] = "KEY_MEDIA",
    };

    internal static string GetKeyCodeName(ushort code)
    {
        return KeyCodeNames.TryGetValue(code, out var name) ? name : $"UNKNOWN({code})";
    }

    internal static Dictionary<string, string> ParseProcBusInputDevices()
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
                    FlushDevice(currentHandlers, currentName, currentBus, names);
                    currentName = null;
                    currentHandlers = null;
                    currentBus = null;
                }
            }

            FlushDevice(currentHandlers, currentName, currentBus, names);
        }
        catch { }

        return names;
    }

    private static void FlushDevice(string? handlers, string? name, ushort? bus, Dictionary<string, string> names)
    {
        if (handlers == null || name == null) return;
        foreach (Match m in Regex.Matches(handlers, @"event(\d+)"))
            names[$"/dev/input/event{m.Groups[1].Value}"] = name + (bus == BUS_BLUETOOTH ? " [BT]" : "");
    }
}
