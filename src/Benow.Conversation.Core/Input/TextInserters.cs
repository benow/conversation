using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Input;

/// <summary>Inserts text into whatever has focus (the Speak/dictation delivery step).</summary>
public interface ITextInserter
{
    /// <summary>True when this inserter can run on this machine (tools present / platform supported).</summary>
    bool IsAvailable { get; }
    Task InsertAsync(string text, CancellationToken ct);
}

/// <summary>
/// Linux clipboards + key injection. Wayland has no global input API, so the delivery path is
/// TWO steps: put the text on the clipboard, then synthesize Ctrl+V via ydotool (which works on
/// both Wayland and X11 through uinput). Falls back to X11 tools (xclip + xdotool) when ydotool
/// is absent, and reports honestly when nothing can paste.
/// Port of V1's delivery approach (WaylandClipboardService + YdotoolKeyboardSimulator).
/// </summary>
public sealed class LinuxTextInserter : ITextInserter
{
    private readonly ILogger<LinuxTextInserter> _logger;

    public LinuxTextInserter(ILogger<LinuxTextInserter> logger) => _logger = logger;

    public bool IsAvailable => OperatingSystem.IsLinux() && (Has("ydotool") || Has("xdotool"));

    public async Task InsertAsync(string text, CancellationToken ct)
    {
        // 1. Clipboard: wl-copy on Wayland, xclip on X11. Prefer wl-copy when a Wayland session
        //    is present — the X11 clipboard is not shared back to Wayland apps.
        var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (wayland && Has("wl-copy")) await SetClipboardAsync("wl-copy", "", text, ct);
        else if (Has("xclip")) await SetClipboardAsync("xclip", "-selection clipboard", text, ct);
        else if (Has("wl-copy")) await SetClipboardAsync("wl-copy", "", text, ct);
        else
        {
            _logger.LogError("[insert] no clipboard tool found (need wl-clipboard or xclip). " +
                "Fix: install wl-clipboard (Wayland) or xclip (X11)");
            throw new InvalidOperationException("no clipboard tool available");
        }

        // 2. Paste: give the clipboard a moment, then Ctrl+V via ydotool/xdotool.
        await Task.Delay(80, ct);
        if (Has("ydotool"))
        {
            await RunAsync("ydotool", "key 29:1 47:1 47:0 29:0", ct); // KEY_LEFTCTRL + KEY_V
            _logger.LogInformation("[insert] pasted {Chars}c via ydotool", text.Length);
        }
        else if (Has("xdotool"))
        {
            await RunAsync("xdotool", "key --clearmodifiers ctrl+v", ct);
            _logger.LogInformation("[insert] pasted {Chars}c via xdotool", text.Length);
        }
        else
        {
            _logger.LogWarning("[insert] text is on the clipboard but no key-injection tool is available — press Ctrl+V. " +
                "Fix: install ydotool (and enable its service) for automatic paste");
        }
    }

    private async Task SetClipboardAsync(string tool, string args, string text, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {tool}");
        await proc.StandardInput.WriteAsync(text.AsMemory(), ct);
        proc.StandardInput.Close();
        await proc.WaitForExitAsync(ct);
    }

    private async Task RunAsync(string tool, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) throw new InvalidOperationException($"failed to start {tool}");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            _logger.LogWarning("[insert] {Tool} exited with {Code}", tool, proc.ExitCode);
    }

    internal static bool Has(string binary)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(':') ?? Array.Empty<string>();
        foreach (var dir in paths)
        {
            try { if (File.Exists(Path.Combine(dir, binary))) return true; } catch { }
        }
        return false;
    }
}

/// <summary>
/// Windows delivery: Set-Clipboard via PowerShell, then SendKeys Ctrl+V. Kept deliberately
/// simple (no extra dependencies); replaced by SharpHook input synthesis in the polish phase
/// when the tray/hotkey stack moves to it.
/// </summary>
public sealed class WindowsTextInserter : ITextInserter
{
    private readonly ILogger<WindowsTextInserter> _logger;
    public WindowsTextInserter(ILogger<WindowsTextInserter> logger) => _logger = logger;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public async Task InsertAsync(string text, CancellationToken ct)
    {
        var escaped = text.Replace("'", "''");
        await RunAsync("powershell", $"-NoProfile -Command \"Set-Clipboard -Value '{escaped}'\"", ct);
        await Task.Delay(80, ct);
        await RunAsync("powershell",
            "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('^v')\"", ct);
        _logger.LogInformation("[insert] pasted {Chars}c via SendKeys", text.Length);
    }

    private static async Task RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {file}");
        await proc.WaitForExitAsync(ct);
    }
}

/// <summary>Test/dev inserter that records what would have been pasted.</summary>
public sealed class CapturingTextInserter : ITextInserter
{
    public List<string> Inserted { get; } = new();
    public bool IsAvailable => true;
    public Task InsertAsync(string text, CancellationToken ct)
    {
        lock (Inserted) Inserted.Add(text);
        return Task.CompletedTask;
    }
}

/// <summary>Picks the platform inserter. Honest when the platform has none yet (macOS: phase-4).</summary>
public static class TextInserters
{
    public static ITextInserter Create(ILogger logger)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsTextInserter(LoggerAs<WindowsTextInserter>(logger));
        if (OperatingSystem.IsLinux())
            return new LinuxTextInserter(LoggerAs<LinuxTextInserter>(logger));

        logger.LogWarning("[insert] no text inserter for {Platform} — Speak will copy to the clipboard only",
            RuntimeInformation.OSDescription);
        return new CapturingTextInserter();
    }

    private static ILogger<T> LoggerAs<T>(ILogger logger) =>
        logger is ILogger<T> typed ? typed : new LoggerAdapter<T>(logger);

    private sealed class LoggerAdapter<T> : ILogger<T>
    {
        private readonly ILogger _inner;
        public LoggerAdapter(ILogger inner) => _inner = inner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
