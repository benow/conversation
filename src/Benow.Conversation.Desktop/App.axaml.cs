using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Benow.Conversation.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benow.Conversation.Desktop;

/// <summary>
/// The shell: a tray icon that is the app's front door (plan §4.4) plus a Converse overlay.
/// There is no main window — the app lives in the tray and appears only while you talk.
/// </summary>
public sealed class App : Application
{
    private static TrayIcon? _tray;

    /// <summary>Set by Program before StartWithClassicDesktopLifetime (Avalonia constructs App itself).</summary>
    internal static DesktopHost Host = null!;
    internal static SettingsHost Settings = null!;
    internal static ILogger<App> Log { get; set; } = NullLogger<App>.Instance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-only: closing the overlay must not end the process.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _tray = new TrayIcon
            {
                Icon = TrayIconFactory.Create(),
                ToolTipText = TrayModel.Tooltip(Host.State),
                IsVisible = true
            };
            _tray.Clicked += (_, _) => ShowOverlay();
            RebuildMenu(Host.State);
            _tray.IsVisible = true;

            Host.StateChanged += state => Dispatcher.UIThread.Post(() =>
            {
                if (_tray != null)
                {
                    _tray.ToolTipText = TrayModel.Tooltip(state);
                    RebuildMenu(state);
                }
            });

            Log.LogInformation("[app] tray ready — settings at {Url}", Settings.Url);

            // Start the host once the toolkit is up: hotkeys, device enumeration and persona
            // state all come from here, and StateChanged rebuilds the tray as they arrive.
            _ = Task.Run(async () =>
            {
                try { await Host.StartAsync(); }
                catch (Exception ex) { Log.LogError(ex, "[app] host start failed: {Error}", ex.Message); }
            });

            // A tray-less desktop (GNOME without the AppIndicator extension) has no way to open
            // the overlay, so honour an explicit request to show it.
            if (Environment.GetCommandLineArgs().Contains("--show")) ShowOverlay();

            // First-run guidance: with no keys the app cannot do anything useful, so say so where
            // the user will see it (tray tooltip) rather than only in the log.
            if (string.IsNullOrWhiteSpace(Host.Runtime.Config.GroqApiKey) &&
                string.IsNullOrWhiteSpace(Host.Runtime.Config.OpenRouterApiKey))
            {
                Log.LogWarning("[app] no provider keys configured — open Settings ({Url}) and add a Groq " +
                    "and/or OpenRouter key. Speak (dictation) needs Groq; Converse needs OpenRouter.", Settings.Url);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Maps the toolkit-free <see cref="TrayModel"/> output onto Avalonia's native menu.</summary>
    private static void RebuildMenu(TrayState state)
    {
        if (_tray == null) return;
        _tray.Menu = BuildMenuForTest(state);
    }

    /// <summary>Exposed for headless tests: builds the native menu without a tray or a window.</summary>
    internal static NativeMenu BuildMenuForTest(TrayState state)
    {
        var menu = new NativeMenu();
        foreach (var entry in TrayModel.Build(state))
            menu.Add(ToMenuItem(entry));
        return menu;
    }

    private static NativeMenuItem ToMenuItem(TrayEntry entry)
    {
        if (!entry.IsEnabled && entry.Children.Count == 0)
            return new NativeMenuItem(entry.Label) { IsEnabled = false };

        var item = new NativeMenuItem(entry.Label) { IsEnabled = entry.IsEnabled };
        if (entry.IsChecked) item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = entry.IsChecked;

        if (entry.Children.Count > 0)
        {
            var sub = new NativeMenu();
            foreach (var child in entry.Children) sub.Add(ToMenuItem(child));
            item.Menu = sub;
        }
        else
        {
            var action = entry.Action;
            var argument = entry.Argument;
            item.Click += (_, _) => Dispatch(action, argument);
        }
        return item;
    }

    private static void Dispatch(TrayAction action, string? argument)
    {
        if (action == TrayAction.Settings)
        {
            OpenSettings();
            return;
        }
        if (action == TrayAction.Quit)
        {
            Log.LogInformation("[app] quitting from tray");
            (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            return;
        }

        // Converse gets the overlay up front so the user sees the capture state immediately.
        if (action is TrayAction.Converse or TrayAction.ReplayLast) ShowOverlay();
        _ = Task.Run(async () =>
        {
            try { await Host.DispatchAsync(action, argument); }
            catch (Exception ex) { Log.LogError(ex, "[app] tray action {Action} failed: {Error}", action, ex.Message); }
        });
    }

    private static ConverseOverlay? _overlay;

    internal static void ShowOverlay()
    {
        if (_overlay == null)
        {
            var window = new ConverseOverlay();
            window.Closed += (_, _) => _overlay = null;
            _overlay = window;
            window.Show();
        }
        else
        {
            _overlay.Activate();
        }
    }

    internal static void OpenSettings()
    {
        var url = Settings.Url;
        try
        {
            // xdg-open on Linux, `start` on Windows; failure just prints the URL.
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "[app] could not open a browser — open {Url} manually", url);
        }
    }
}

/// <summary>
/// Builds the tray icon in code (no binary asset in git). A rounded speech bubble in the app's
/// accent colour — distinguishable at 22px in a tray.
/// </summary>
internal static class TrayIconFactory
{
    internal static WindowIcon Create()
    {
        const int size = 32;
        var bmp = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // Rounded square (radius 7) + three dots knocked out as the "speech" glyph.
                    var inside = Rounded(x, y, size, 7);
                    var dot = Dots(x, y);
                    var color = inside && !dot ? unchecked((int)0xFFE6A95A) /* BGRA: accent */ : 0;
                    System.Runtime.InteropServices.Marshal.WriteInt32(fb.Address, y * fb.RowBytes + x * 4, color);
                }
            }
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        return new WindowIcon(ms);
    }

    private static bool Rounded(int x, int y, int size, int r)
    {
        var cx = Math.Min(Math.Max(x, r), size - 1 - r);
        var cy = Math.Min(Math.Max(y, r), size - 1 - r);
        var dx = x - cx;
        var dy = y - cy;
        return dx * dx + dy * dy <= r * r || (x >= r && x < size - r) || (y >= r && y < size - r);
    }

    private static bool Dots(int x, int y)
    {
        if (y < 12 || y > 20) return false;
        foreach (var cx in new[] { 9, 16, 23 })
        {
            var dx = x - cx;
            var dy = y - 16;
            if (dx * dx + dy * dy <= 4) return true;
        }
        return false;
    }
}
