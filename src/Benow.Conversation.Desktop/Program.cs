using Avalonia;
using Benow.Conversation.Composition;
using Benow.Conversation.Config;
using Benow.Conversation.Engine;
using Benow.Conversation.Personas;
using Benow.Conversation.Settings;
using Benow.Conversation.Voices;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Desktop;

/// <summary>
/// Entry point and composition root. Local mode only (plan §4.6): this process talks to the AI
/// providers directly and to nothing else — no NASTV server, no plugin host. It is its own
/// long-running TV-backend-shaped thing, scaled down to one user.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var cli = new CliArgs(args);
        if (cli.Has("--help") || cli.Has("-h")) { PrintHelp(); return 0; }

        using var loggerFactory = BuildLogging(cli);
        var log = loggerFactory.CreateLogger("Conversation");

        var configPath = cli.Value("--config");
        var store = new ConfigStore(configPath);
        var config = ConfigStore.FromEnvironment(store.Load());

        log.LogInformation("[app] conversation desktop {Version} — config {Config}",
            typeof(Program).Assembly.GetName().Version, store.FilePath);

        // ---- services ----
        ConversationRuntime runtime;
        DesktopEventSink uiSink = new();
        try
        {
            runtime = ConversationFactory.CreateAsync(loggerFactory, config,
                new EngineOptions { SpeakReplies = true, FullAudioCorrection = true },
                sink: new CompositeSink(new LoggingSink(loggerFactory.CreateLogger("events")), uiSink))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "[app] startup failed while wiring providers — {Reason}. Fix: check the " +
                "provider config at {Config} and that ffmpeg/ffplay are on PATH.", ex.Message, store.FilePath);
            return 2;
        }

        var personas = new PersonaStore();
        var personasDir = Path.Combine(Path.GetDirectoryName(store.FilePath) ?? ".", "characters");
        var imported = personas.ImportCharactersDirectory(personasDir, log);
        if (imported > 0) log.LogInformation("[app] imported {Count} character file(s) from {Dir}", imported, personasDir);

        var settings = new SettingsHost(loggerFactory.CreateLogger<SettingsHost>(), store, config,
            runtime.Devices, runtime.Voices, personas,
            testVoice: text => TestVoiceAsync(runtime, text, loggerFactory));

        var host = new DesktopHost(loggerFactory, runtime, store, personas, uiSink);

        App.Host = host;
        App.Settings = settings;
        App.Log = loggerFactory.CreateLogger<App>();

        // The settings page is useful even when the tray is not visible (headless/ssh).
        settings.Start(cli.Value("--port") is { } p && int.TryParse(p, out var port) ? port : 8791);

        if (cli.Has("--settings")) { Console.WriteLine(settings.Url); return 0; }
        if (cli.Has("--no-ui"))
        {
            // Headless verification path: services up, no windowing system needed.
            log.LogInformation("[app] --no-ui: running without a UI (settings at {Url}). Ctrl+C to exit.", settings.Url);
            using var stop = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
            stop.Wait();
            settings.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            return 0;
        }

        return RunUi(host, settings, loggerFactory, log);
    }

    private static int RunUi(DesktopHost host, SettingsHost settings, ILoggerFactory loggerFactory, ILogger log)
    {
        var started = false;
        try
        {
            var builder = AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();

            builder.StartWithClassicDesktopLifetime(Array.Empty<string>(), lifetime =>
            {
                lifetime.ShutdownRequested += (_, _) =>
                {
                    log.LogInformation("[app] shutting down");
                    settings.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                };
            });
            return 0;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "[app] UI failed to start — {Reason}. Fix: on a headless session use --no-ui " +
                "(or provide DISPLAY/WAYLAND_DISPLAY). Settings stay available at {Url}.", ex.Message, settings.Url);
            settings.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            return 3;
        }
        finally
        {
            if (started) host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task<string> TestVoiceAsync(ConversationRuntime runtime, string text, ILoggerFactory lf)
    {
        var log = lf.CreateLogger("voicetest");
        try
        {
            runtime.Engine.Speak(text);
            log.LogInformation("[voicetest] queued {Chars}c for synthesis", text.Length);
            await Task.Delay(400);
            return "queued — listen for it";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[voicetest] failed: {Error}", ex.Message);
            return $"failed: {ex.Message}";
        }
    }

    private static ILoggerFactory BuildLogging(CliArgs cli)
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "conversation", "logs");
        Directory.CreateDirectory(logsDir);
        var logFile = Path.Combine(logsDir, $"conversation-{DateTime.Now:yyyyMMdd}.log");

        return LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
            b.AddProvider(new FileLoggerProvider(logFile));
            b.SetMinimumLevel(cli.Has("--verbose") ? LogLevel.Debug : LogLevel.Information);
        });
    }

    private static void PrintHelp() => Console.WriteLine("""
        conversation — voice dictation and conversation for the desktop

        usage: conversation [options]
          --config <path>   config file (default ~/.config/conversation/config.json)
          --port <n>        settings page port (default 8791)
          --settings        print the settings URL and exit
          --show            open the Converse overlay at startup (no tray needed)
          --screenshot <p>  render the overlay to a PNG and exit (Wayland-safe self-capture)
          --no-ui           run services without a UI (headless / ssh)
          --verbose         debug logging
          --help            this text

        Hotkeys (default, tune in Settings):
          Ctrl+Space        Speak    — dictate into the focused app
          Ctrl+Shift+Space  Converse — talk to the assistant
          Media play/pause  Speak    (Bluetooth earbud button)
          Media next        Converse (multifunction earbud button)
        """);

    /// <summary>Minimal argument reader (--flag, --key value) — no dependency, no surprises.</summary>
    private sealed class CliArgs
    {
        private readonly List<string> _args;
        public CliArgs(string[] args) => _args = args.ToList();
        public bool Has(string name) => _args.Contains(name);
        public string? Value(string name)
        {
            var i = _args.IndexOf(name);
            return i >= 0 && i + 1 < _args.Count ? _args[i + 1] : null;
        }
    }
}

/// <summary>
/// Forwards engine events to the log. The UI subscribes to <see cref="DesktopHost"/> directly;
/// this exists so a headless run still records transcripts, chunk counts and errors.
/// </summary>
internal sealed class LoggingSink : IConversationEventSink
{
    private readonly ILogger _log;
    private int _chunks;

    public LoggingSink(ILogger log) => _log = log;

    public void SessionStarted() => _log.LogDebug("[events] session started");
    public void SessionEnded(long durationMs) => _log.LogDebug("[events] session ended ({Ms}ms)", durationMs);
    public void TranscriptPartial(string text) => _log.LogDebug("[events] partial: {Text}", text);
    public void TranscriptFinal(string text, bool corrected) =>
        _log.LogInformation("[events] final transcript ({Chars}c{Corrected}): {Text}", text.Length,
            corrected ? ", corrected" : "", text);
    public void LlmStarted() { _chunks = 0; _log.LogDebug("[events] llm started"); }
    public void LlmChunk(string text) => _chunks++;
    public void LlmFinal(string text, long ms) =>
        _log.LogInformation("[events] llm final ({Ms}ms, {Chunks} chunks, {Chars}c)", ms, _chunks, text.Length);
    public void TtsStarted(string text) => _log.LogDebug("[events] tts: {Text}", text);
    public void TtsDone(string text, long audioMs) => _log.LogDebug("[events] tts done ({AudioMs}ms)", audioMs);
    public void Error(string stage, string message) => _log.LogError("[events] {Stage}: {Message}", stage, message);
}

/// <summary>
/// Appends log lines to ~/.local/state/conversation/logs/. The tray app has no console on Windows,
/// and this is where a user looks when something does not work.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    private void Write(string line)
    {
        lock (_gate) _writer.WriteLine(line);
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLoggerProvider _provider;

        public FileLogger(string category, FileLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var shortCategory = _category.Contains('.') ? _category[(_category.LastIndexOf('.') + 1)..] : _category;
            _provider.Write($"{DateTime.Now:HH:mm:ss.fff} [{logLevel,-4}] {shortCategory}: {formatter(state, exception)}");
            if (exception != null) _provider.Write($"    {exception}");
        }
    }
}
