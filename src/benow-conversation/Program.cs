using System.Diagnostics;
using System.Text.Json;
using benow_conversation.Configuration;
using benow_conversation.Models;
using benow_conversation.Services;
using benow_conversation.Services.Abstractions;
using benow_conversation.Services.Stt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

var projectRoot = FindProjectRoot();
var logDir = Path.GetFullPath("logs", projectRoot);
if (Directory.Exists(logDir))
{
    foreach (var file in Directory.GetFiles(logDir))
        File.Delete(file);
}
Directory.CreateDirectory(logDir);

var logPath = Path.Combine(logDir, "benow-conversation.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(logPath, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Log.Information("Application starting");
Log.Information("Project root: {ProjectRoot}", projectRoot);
Log.Information("Log file: {LogPath}", logPath);

var cliArgs = args;

var settingsJson = Path.Combine(projectRoot, "appsettings.json");
var settingsEnvJson = Path.Combine(projectRoot, $"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json");

var host = Host.CreateDefaultBuilder()
    .UseSerilog()
    .ConfigureAppConfiguration((context, config) =>
    {
        config.SetBasePath(projectRoot);
    })
    .ConfigureServices((context, services) =>
    {
        Log.Information("Config: {Path} (exists={Exists})", settingsJson, File.Exists(settingsJson));
        Log.Information("Config: {Path} (exists={Exists})", settingsEnvJson, File.Exists(settingsEnvJson));
        Log.Information("Environment: {Env}", Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production");

        services.Configure<AppSettings>(context.Configuration);
        services.AddSingleton<IAudioConverter, AudioConverter>();
        services.AddSingleton<ITtsService, TtsService>();
        services.AddSingleton<IModelService, ModelService>();
        services.AddSingleton<IAudioPlayer, AudioPlayer>();
        services.AddSingleton<ISpeechQueue, SpeechQueue>();
        services.AddSingleton<IProxyService, ProxyService>();
        services.AddSingleton<ProviderFormatCache>();
        services.AddSingleton<AudioFormatConverter>();

        services.AddSingleton<IPersistentAudioPipeline>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PersistentAudioPipeline>>();
            var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
            var audioPlayer = sp.GetRequiredService<IAudioPlayer>();
            if (!audioPlayer.IsAvailable)
                return null!;

            var defaultProfile = settings.OutputProfiles.FirstOrDefault(p => p.Value.IsDefault).Value;
            var volume = defaultProfile?.Volume;
            var device = string.IsNullOrEmpty(defaultProfile?.Device) ? null : defaultProfile.Device;

            return new PersistentAudioPipeline(
                logger,
                settings,
                ffplayPath: "ffplay",
                volume: volume,
                device: device);
        });

        RegisterSttServices(services, context.Configuration);
        RegisterClipboardTtsServices(services, context.Configuration);

        services.AddHttpClient("OpenRouter", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddHttpClient("ProxyBackend", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddHttpClient("Groq", (sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.Groq.ApiKey);
        });

        services.AddSingleton<IPersonaAllocator, PersonaAllocator>();
        services.AddSingleton<IModifierInjector, ModifierInjector>();
        services.AddSingleton<ICharacterNormalizer, CharacterNormalizer>();
        services.AddSingleton<ParallelTtsPlayer>();

        services.AddSingleton<ITtsProvider>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
            return settings.TtsBackend switch
            {
                "kokoro" => ActivatorUtilities.CreateInstance<KokoroTtsProvider>(sp),
                "replicate" => ActivatorUtilities.CreateInstance<ReplicateTtsProvider>(sp),
                _ => ActivatorUtilities.CreateInstance<OpenRouterTtsProvider>(sp)
            };
        });

        services.AddHttpClient("ModifierInjector", (sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
            client.Timeout = TimeSpan.FromMilliseconds(settings.MultiCharacter.ModifierTimeoutMs);
        });

        services.AddHttpClient("CharacterNormalizer", (sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
            client.Timeout = TimeSpan.FromMilliseconds(settings.MultiCharacter.NormalizerTimeoutMs);
        });

        services.AddHttpClient("KokoroTts", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient("Replicate", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
        });
    })
    .Build();

if (cliArgs.Length == 0 || cliArgs.Contains("--help") || cliArgs.Contains("-h"))
{
    PrintUsage(host.Services);
    Log.Information("Application ended (help)");
    await Log.CloseAndFlushAsync();
    return;
}

string? text = null;
string? outputFileName = null;
string? voiceOverride = null;
string? modelOverride = null;
string? personaName = null;
string? savePersonaName = null;
var setAsDefault = false;
string? instructionsOverride = null;
double? temperatureOverride = null;
int? seedOverride = null;
var listModels = false;
var listVoices = false;
var playAudio = false;
var streamAudio = false;
string? outputProfileName = null;
string? saveOutputProfileName = null;
var setOutputDefault = false;
string? deviceOverride = null;
double? volumeOverride = null;
var listDevices = false;
var listOutputProfiles = false;
var daemonMode = false;
var noPlay = false;
var sttMode = false;
var sttSetup = false;
var noCleanup = false;
string? cleanupModelOverride = null;
string? extractVoicePath = null;
string? extractVoiceName = null;

for (var i = 0; i < cliArgs.Length; i++)
{
    if (cliArgs[i] == "--output" && i + 1 < cliArgs.Length)
        outputFileName = cliArgs[++i];
    else if (cliArgs[i] == "--text-file" && i + 1 < cliArgs.Length)
    {
        var filePath = cliArgs[++i];
        var resolvedPath = Path.GetFullPath(filePath, projectRoot);
        if (!File.Exists(resolvedPath))
        {
            Log.Error("Text file not found: '{FilePath}'. Project root: '{ProjectRoot}'.", filePath, projectRoot);
            await Log.CloseAndFlushAsync();
            return;
        }

        text = await File.ReadAllTextAsync(resolvedPath);
        Log.Information("Read text from file: {FilePath} ({Length} chars)", filePath, text.Length);
        if (string.IsNullOrWhiteSpace(text))
        {
            Log.Error("Text file '{FilePath}' is empty.", filePath);
            await Log.CloseAndFlushAsync();
            return;
        }
    }
    else if ((cliArgs[i] == "--persona" || cliArgs[i] == "--preset") && i + 1 < cliArgs.Length)
        personaName = cliArgs[++i];
    else if (cliArgs[i] == "--voice" && i + 1 < cliArgs.Length)
        voiceOverride = cliArgs[++i];
    else if (cliArgs[i] == "--model" && i + 1 < cliArgs.Length)
        modelOverride = cliArgs[++i];
    else if (cliArgs[i] == "--openai-instructions" && i + 1 < cliArgs.Length)
        instructionsOverride = cliArgs[++i];
    else if (cliArgs[i] == "--temperature" && i + 1 < cliArgs.Length)
    {
        if (double.TryParse(cliArgs[++i], out var t))
            temperatureOverride = t;
        else
        {
            Log.Error("Invalid temperature value: '{Value}'. Must be a number between 0 and 2.", cliArgs[i]);
            await Log.CloseAndFlushAsync();
            return;
        }
    }
    else if (cliArgs[i] == "--seed" && i + 1 < cliArgs.Length)
    {
        if (int.TryParse(cliArgs[++i], out var s))
            seedOverride = s;
        else
        {
            Log.Error("Invalid seed value: '{Value}'. Must be an integer.", cliArgs[i]);
            await Log.CloseAndFlushAsync();
            return;
        }
    }
    else if (cliArgs[i] == "--list-models")
        listModels = true;
    else if (cliArgs[i] == "--list-voices")
        listVoices = true;
    else if (cliArgs[i] == "--list-personas")
    {
        HandleListPersonas(host.Services);
        Log.Information("Application ended");
        await Log.CloseAndFlushAsync();
        return;
    }
    else if (cliArgs[i] == "--save-persona" && i + 1 < cliArgs.Length)
        savePersonaName = cliArgs[++i];
    else if (cliArgs[i] == "--set-default")
        setAsDefault = true;
    else if (cliArgs[i] == "--play")
        playAudio = true;
    else if (cliArgs[i] == "--stream")
        streamAudio = true;
    else if (cliArgs[i] == "--output-profile" && i + 1 < cliArgs.Length)
        outputProfileName = cliArgs[++i];
    else if (cliArgs[i] == "--save-output-profile" && i + 1 < cliArgs.Length)
        saveOutputProfileName = cliArgs[++i];
    else if (cliArgs[i] == "--set-output-default")
        setOutputDefault = true;
    else if (cliArgs[i] == "--device" && i + 1 < cliArgs.Length)
        deviceOverride = cliArgs[++i];
    else if (cliArgs[i] == "--volume" && i + 1 < cliArgs.Length)
    {
        if (double.TryParse(cliArgs[++i], out var v) && v >= 0 && v <= 100)
            volumeOverride = v;
        else
        {
            Log.Error("Invalid volume value: '{Value}'. Must be a number between 0 and 100.", cliArgs[i]);
            await Log.CloseAndFlushAsync();
            return;
        }
    }
    else if (cliArgs[i] == "--list-devices")
        listDevices = true;
    else if (cliArgs[i] == "--list-output-profiles")
        listOutputProfiles = true;
    else if (cliArgs[i] == "--no-play")
        noPlay = true;
    else if (cliArgs[i] == "--daemon")
        daemonMode = true;
    else if (cliArgs[i] == "--stt")
        sttMode = true;
    else if (cliArgs[i] == "--stt-setup")
        sttSetup = true;
    else if (cliArgs[i] == "--no-cleanup")
        noCleanup = true;
    else if (cliArgs[i] == "--cleanup-model" && i + 1 < cliArgs.Length)
        cleanupModelOverride = cliArgs[++i];
    else if (cliArgs[i] == "--extract-voice" && i + 1 < cliArgs.Length)
        extractVoicePath = cliArgs[++i];
    else if (cliArgs[i] == "--voice-name" && i + 1 < cliArgs.Length)
        extractVoiceName = cliArgs[++i];
    else if (!cliArgs[i].StartsWith("-"))
    {
        var resolvedPath = Path.GetFullPath(cliArgs[i], projectRoot);
        if (File.Exists(resolvedPath))
        {
            text = await File.ReadAllTextAsync(resolvedPath);
            Log.Information("Read text from file: {FilePath} ({Length} chars)", cliArgs[i], text.Length);
            if (string.IsNullOrWhiteSpace(text))
            {
                Log.Error("Text file '{FilePath}' is empty.", cliArgs[i]);
                await Log.CloseAndFlushAsync();
                return;
            }
        }
        else
        {
            text = cliArgs[i];
            Log.Information("Using direct text input ({Length} chars)", text.Length);
        }
    }
}

if (extractVoicePath != null)
{
    var repoRoot = Path.GetFullPath(Path.Combine(projectRoot, "..", ".."));
    var inputPath = Path.GetFullPath(extractVoicePath, projectRoot);
    if (!File.Exists(inputPath))
    {
        Log.Error("Video file not found: '{Path}'. Project root: '{ProjectRoot}'.", extractVoicePath, projectRoot);
        await Log.CloseAndFlushAsync();
        return;
    }

    var voiceName = extractVoiceName ?? Path.GetFileNameWithoutExtension(inputPath);
    var scriptPath = Path.Combine(repoRoot, "scripts", "extract-voice.py");
    var venvPython = Path.Combine(repoRoot, ".venv-tts", "bin", "python");

    if (!File.Exists(scriptPath))
    {
        Log.Error("Extract-voice script not found: {Path}", scriptPath);
        await Log.CloseAndFlushAsync();
        return;
    }

    Log.Information("Extracting voice sample from: {Input}", inputPath);
    Log.Information("Output name: {Name}", voiceName);

    var psi = new ProcessStartInfo
    {
        FileName = venvPython,
        Arguments = $"\"{scriptPath}\" --input \"{inputPath}\" --name \"{voiceName}\"",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    using var process = Process.Start(psi)!;
    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (!string.IsNullOrEmpty(stdout))
        Log.Information("{Output}", stdout.TrimEnd());
    if (!string.IsNullOrEmpty(stderr))
        Log.Information("{Output}", stderr.TrimEnd());

    if (process.ExitCode != 0)
    {
        Log.Error("Voice extraction failed with exit code {Code}", process.ExitCode);
        await Log.CloseAndFlushAsync();
        return;
    }

    var outputFile = Path.Combine(repoRoot, "voices", $"{voiceName}.wav");
    Log.Information("Voice sample saved: {Path}", outputFile);
    Log.Information("Application ended");
    await Log.CloseAndFlushAsync();
    return;
}

try
{
    if (noCleanup)
    {
        var liveSettings = host.Services.GetRequiredService<IOptions<AppSettings>>();
        liveSettings.Value.Stt.CleanupSkip = true;
    }

    if (cleanupModelOverride != null)
    {
        var liveSettings = host.Services.GetRequiredService<IOptions<AppSettings>>();
        liveSettings.Value.TranscriptCleanup.Model = cleanupModelOverride;
        Log.Information("Cleanup model override: {Model}", cleanupModelOverride);
    }

    if (sttSetup)
    {
        PipeWireRecorder.KillOrphanedProcesses();
        var exitCode = await SttSetup.RunAsync(projectRoot);
        Log.Information("Application ended (stt-setup, code={Code})", exitCode);
        await Log.CloseAndFlushAsync();
        return;
    }

    if (sttMode && daemonMode)
    {
        PipeWireRecorder.KillOrphanedProcesses();

        var proxyService = host.Services.GetRequiredService<IProxyService>();
        var speechQueue = host.Services.GetRequiredService<ISpeechQueue>();
        var sttRunner = host.Services.GetRequiredService<ISttRunner>();
        var clipboardTtsRunner = host.Services.GetRequiredService<IClipboardTtsRunner>();
        var pipeline = host.Services.GetRequiredService<IPersistentAudioPipeline>();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Log.Information("Starting STT + daemon mode...");
        if (pipeline != null)
            await pipeline.StartAsync(cts.Token);
        await speechQueue.StartAsync(cts.Token);

        var proxyTask = proxyService.RunAsync(cts.Token);
        var sttTask = sttRunner.RunAsync(cts.Token);
        var cbTtsTask = clipboardTtsRunner.RunAsync(cts.Token);

        await Task.WhenAny(proxyTask, sttTask, cbTtsTask);
        cts.Cancel();

        try { await Task.WhenAll(proxyTask, sttTask, cbTtsTask); } catch { }

        await speechQueue.StopAsync(CancellationToken.None);
        if (pipeline is IAsyncDisposable d)
            await d.DisposeAsync();
        Log.Information("STT + daemon stopped");
        await Log.CloseAndFlushAsync();
        return;
    }

    if (sttMode)
    {
        PipeWireRecorder.KillOrphanedProcesses();

        var sttRunner = host.Services.GetRequiredService<ISttRunner>();
        var clipboardTtsRunner = host.Services.GetRequiredService<IClipboardTtsRunner>();
        var pipeline = host.Services.GetRequiredService<IPersistentAudioPipeline>();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (pipeline != null)
            await pipeline.StartAsync(cts.Token);

        var sttTask = sttRunner.RunAsync(cts.Token);
        var cbTtsTask = clipboardTtsRunner.RunAsync(cts.Token);

        await Task.WhenAny(sttTask, cbTtsTask);
        cts.Cancel();

        try { await Task.WhenAll(sttTask, cbTtsTask); } catch { }

        if (pipeline is IAsyncDisposable d)
            await d.DisposeAsync();
        await Log.CloseAndFlushAsync();
        return;
    }

    if (daemonMode)
    {
        var mcSettings = host.Services.GetRequiredService<IOptions<AppSettings>>().Value.MultiCharacter;
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        ProxyService.CheckPendingRegressionTests(
            mcSettings.EnforceRegressionTests, logger, fixturesDir);

        var proxyService = host.Services.GetRequiredService<IProxyService>();
        var speechQueue = host.Services.GetRequiredService<ISpeechQueue>();
        var pipeline = host.Services.GetRequiredService<IPersistentAudioPipeline>();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Log.Information("Starting proxy daemon...");
        if (pipeline != null)
            await pipeline.StartAsync(cts.Token);
        await speechQueue.StartAsync(cts.Token);

        try
        {
            await proxyService.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Daemon shutting down...");
        }

        await speechQueue.StopAsync(CancellationToken.None);
        if (pipeline is IAsyncDisposable d)
            await d.DisposeAsync();
        Log.Information("Daemon stopped");
        await Log.CloseAndFlushAsync();
        return;
    }

    var settings = host.Services.GetRequiredService<IOptions<AppSettings>>().Value;
    var defaultPersona = settings.Personas.FirstOrDefault(p => p.Value.IsDefault);
    var effectivePersonaName = personaName ?? defaultPersona.Key ?? settings.Personas.Keys.FirstOrDefault() ?? "default";
    var persona = ResolvePersona(settings, effectivePersonaName);

    var effectiveModel = modelOverride ?? persona.Model;
    var effectiveVoice = voiceOverride ?? persona.Voice;
    var effectiveInstructions = instructionsOverride ?? persona.OpenAiInstructions;
    var effectiveTemperature = temperatureOverride ?? persona.Temperature;
    var effectiveSeed = seedOverride ?? persona.Seed;
    var isModelAll = effectiveModel == "all";

    if (streamAudio)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Log.Error("No text input provided for streaming. Pass text as an argument or use --text-file <path>.");
            await Log.CloseAndFlushAsync();
            return;
        }

        var audioPlayer = host.Services.GetRequiredService<IAudioPlayer>();
        if (!audioPlayer.IsAvailable)
        {
            Log.Error("ffplay is not available. Install ffmpeg (which includes ffplay) to enable streaming playback.");
            await Log.CloseAndFlushAsync();
            return;
        }

        var (effDevice, effVolume) = ResolveOutputSettings(settings, outputProfileName, deviceOverride, volumeOverride);
        await HandleStream(host.Services, text, effectiveModel, effectiveVoice, effectiveInstructions, effectiveTemperature, effectiveSeed, audioPlayer, effDevice, effVolume);
    }
    else
    {
        var shouldPlay = !noPlay && (playAudio || settings.Playback.EnabledByDefault);

        if (modelOverride != null && !isModelAll)
        {
            var liveSettings = host.Services.GetRequiredService<IOptions<AppSettings>>();
            liveSettings.Value.OpenRouter.TtsModel = effectiveModel;
            Log.Information("Model override: {Model}", effectiveModel);
        }

        Log.Information("Effective config: persona={Persona}, model={Model}, voice={Voice}, temp={Temp}, seed={Seed}, instructions={Instructions}",
            effectivePersonaName, effectiveModel, effectiveVoice,
            effectiveTemperature?.ToString() ?? "default", effectiveSeed?.ToString() ?? "default",
            effectiveInstructions ?? "(none)");

        if (!string.IsNullOrWhiteSpace(effectiveInstructions) && !isModelAll && !effectiveModel.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("--openai-instructions ignored for non-OpenAI model '{Model}'. Instructions only apply to openai/* models.", effectiveModel);
            effectiveInstructions = null;
        }

        if (!string.IsNullOrEmpty(savePersonaName))
        {
            SavePersona(projectRoot, savePersonaName, effectiveModel, effectiveVoice, effectiveInstructions, effectiveTemperature, effectiveSeed, setAsDefault);
        }
        else if (setAsDefault)
        {
            SavePersona(projectRoot, effectivePersonaName, effectiveModel, effectiveVoice, effectiveInstructions, effectiveTemperature, effectiveSeed, true);
        }

        if (listDevices)
        {
            HandleListDevices(host.Services);
        }
        else if (listOutputProfiles)
        {
            HandleListOutputProfiles(settings);
        }
        else if (listModels)
        {
            await HandleListModels(host.Services);
        }
        else if (listVoices)
        {
            if (isModelAll)
                await HandleListAllVoices(host.Services);
            else
                await HandleListVoices(host.Services, effectiveModel);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Log.Error("No text input provided. Pass text as an argument or use --text-file <path>.");
                await Log.CloseAndFlushAsync();
                return;
            }

            var ttsService = host.Services.GetRequiredService<ITtsService>();
            var isSingleVoice = !isModelAll && effectiveVoice != "all";

            if (outputFileName == null && isSingleVoice && shouldPlay)
            {
                Log.Information("No output file specified — streaming to speakers");
                var audioPlayer = host.Services.GetRequiredService<IAudioPlayer>();
                if (!audioPlayer.IsAvailable)
                {
                    Log.Error("ffplay is not available. Use --output <file> to save audio, or install ffmpeg to enable playback.");
                    await Log.CloseAndFlushAsync();
                    return;
                }
                var (effDevice, effVolume) = ResolveOutputSettings(settings, outputProfileName, deviceOverride, volumeOverride);
                await HandleStream(host.Services, text, effectiveModel, effectiveVoice, effectiveInstructions, effectiveTemperature, effectiveSeed, audioPlayer, effDevice, effVolume);
            }
            else
            {
                if (outputFileName != null)
                {
                    var (isFile, format) = ParseOutputPath(outputFileName);
                    if (isFile)
                    {
                        var liveSettings = host.Services.GetRequiredService<IOptions<AppSettings>>();
                        liveSettings.Value.Audio.OutputFormat = format;
                        Log.Information("Output format from filename: {Format}", format);
                    }
                    else
                    {
                        var liveSettings = host.Services.GetRequiredService<IOptions<AppSettings>>();
                        liveSettings.Value.Audio.OutputPath = outputFileName;
                        outputFileName = null;
                        Log.Information("Output directory: {Dir}", outputFileName ?? liveSettings.Value.Audio.OutputPath);
                    }
                }

                List<string> generatedFiles;

                if (isModelAll && effectiveVoice == "all")
                {
                    generatedFiles = await ttsService.SynthesizeAllProvidersAsync(text, outputFileName, effectiveInstructions, effectiveTemperature, effectiveSeed);
                    Log.Information("Generated {Count} files across all providers", generatedFiles.Count);
                }
                else if (isModelAll)
                {
                    generatedFiles = await ttsService.SynthesizeAllModelsAsync(text, outputFileName, effectiveVoice, effectiveInstructions, effectiveTemperature, effectiveSeed);
                    Log.Information("Generated {Count} model files", generatedFiles.Count);
                }
                else if (effectiveVoice == "all")
                {
                    generatedFiles = await ttsService.SynthesizeAllVoicesAsync(text, outputFileName, effectiveInstructions, effectiveTemperature, effectiveSeed);
                    Log.Information("Generated {Count} voice files", generatedFiles.Count);
                }
                else
                {
                    var outputPath = await ttsService.SynthesizeToFileAsync(text, outputFileName, effectiveVoice, effectiveInstructions, effectiveTemperature, effectiveSeed);
                    generatedFiles = [outputPath];
                    Log.Information("Saved: {Path}", outputPath);
                }

                foreach (var f in generatedFiles)
                    Log.Information("  {Path}", f);

                if (shouldPlay && generatedFiles.Count > 0)
                {
                    var audioPlayer = host.Services.GetRequiredService<IAudioPlayer>();
                    if (!audioPlayer.IsAvailable)
                    {
                        Log.Warning("ffplay is not available. Skipping playback. Install ffmpeg to enable audio playback.");
                    }
                    else
                    {
                        var (effDevice, effVolume) = ResolveOutputSettings(settings, outputProfileName, deviceOverride, volumeOverride);
                        foreach (var f in generatedFiles)
                        {
                            Log.Information("Playing: {File}", Path.GetFileName(f));
                            try
                            {
                                await audioPlayer.PlayAsync(f, effVolume, effDevice);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Playback failed for {File}: {Error}", Path.GetFileName(f), ex.Message);
                            }
                        }
                    }
                }
            }
        }
    }

    if (!string.IsNullOrEmpty(saveOutputProfileName))
    {
        var (effDevice, effVolume) = ResolveOutputSettings(settings, outputProfileName, deviceOverride, volumeOverride);
        SaveOutputProfile(projectRoot, saveOutputProfileName, effDevice, effVolume, setOutputDefault);
    }
    else if (setOutputDefault && !string.IsNullOrEmpty(outputProfileName))
    {
        var (effDevice, effVolume) = ResolveOutputSettings(settings, outputProfileName, deviceOverride, volumeOverride);
        SaveOutputProfile(projectRoot, outputProfileName, effDevice, effVolume, true);
    }
}
catch (Exception ex)
{
    Log.Error(ex, "Fatal error: {Message}", ex.Message);
}

Log.Information("Application ended");
await Log.CloseAndFlushAsync();

static VoicePersona ResolvePersona(AppSettings settings, string personaName)
{
    if (settings.Personas.TryGetValue(personaName, out var persona))
    {
        Log.Information("Using persona: {PersonaName}", personaName);
        return persona;
    }

    Log.Error("Persona '{PersonaName}' not found. Available personas: {Personas}", personaName, string.Join(", ", settings.Personas.Keys));
    throw new InvalidOperationException($"Persona '{personaName}' not found. Available: {string.Join(", ", settings.Personas.Keys)}");
}

static void HandleListPersonas(IServiceProvider services)
{
    var settings = services.GetRequiredService<IOptions<AppSettings>>().Value;
    Log.Information("Available personas:");
    foreach (var (name, persona) in settings.Personas)
    {
        var marker = persona.IsDefault ? " *" : "";
        Log.Information("  {Name}{Marker}: model={Model}, voice={Voice}, temp={Temp}, seed={Seed}, instructions={Instructions}",
            name, marker, persona.Model, persona.Voice,
            persona.Temperature?.ToString() ?? "default", persona.Seed?.ToString() ?? "default",
            persona.OpenAiInstructions ?? "(none)");
    }
}

static void SavePersona(
    string projectRoot,
    string name,
    string model,
    string voice,
    string? instructions,
    double? temperature,
    int? seed,
    bool isDefault)
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

    if (isDefault)
    {
        foreach (var kvp in appSettings.Personas)
            kvp.Value.IsDefault = false;
    }

    var isNew = !appSettings.Personas.ContainsKey(name);

    appSettings.Personas[name] = new VoicePersona
    {
        Model = model,
        Voice = voice,
        OpenAiInstructions = instructions,
        Temperature = temperature,
        Seed = seed,
        IsDefault = isDefault || (isNew && !appSettings.Personas.Any(p => p.Value.IsDefault))
    };

    var outputJson = JsonSerializer.Serialize(appSettings, jsonOptions);
    File.WriteAllText(configPath, outputJson);

    var action = isNew ? "Saved new" : "Updated";
    var defaultMarker = appSettings.Personas[name].IsDefault ? " (default)" : "";
    Log.Information("{Action} persona '{Name}'{Default}: model={Model}, voice={Voice}, temp={Temp}, seed={Seed}, instructions={Instructions}",
        action, name, defaultMarker, model, voice,
        temperature?.ToString() ?? "default", seed?.ToString() ?? "default",
        instructions ?? "(none)");
}

static async Task HandleListModels(IServiceProvider services)
{
    var modelService = services.GetRequiredService<IModelService>();
    var models = await modelService.GetTtsModelsAsync();

    Log.Information("Available TTS models:");
    foreach (var m in models)
    {
        var inputCost = m.PromptPricePerMillionChars > 0 ? $"${m.PromptPricePerMillionChars:F2}/1M input" : "free input";
        var outputCost = m.CompletionPricePerMillionChars > 0 ? $", ${m.CompletionPricePerMillionChars:F2}/1M output" : "";
        Log.Information("  {Id,-45} {Cost}{OutputCost}   {VoiceCount,3} voices", m.Id, inputCost, outputCost, m.VoiceCount);
    }
}

static async Task HandleListVoices(IServiceProvider services, string model)
{
    var modelService = services.GetRequiredService<IModelService>();
    var voices = await modelService.GetVoicesForModelAsync(model);

    Log.Information("Voices for {Model} ({Count} total):", model, voices.Count);

    const int cols = 2;
    for (var i = 0; i < voices.Count; i += cols)
    {
        var parts = new List<string>();
        for (var j = 0; j < cols && i + j < voices.Count; j++)
        {
            var v = voices[i + j];
            parts.Add(v.Description.Length > 0
                ? $"{v.Id} ({v.Description})"
                : v.Id);
        }
        Log.Information("  {Voices}", string.Join("  ", parts));
    }
}

static async Task HandleListAllVoices(IServiceProvider services)
{
    var modelService = services.GetRequiredService<IModelService>();
    var models = await modelService.GetTtsModelsAsync();

    Log.Information("Voices across all {Count} TTS models:", models.Count);
    var grandTotal = 0;

    foreach (var m in models)
    {
        var voices = await modelService.GetVoicesForModelAsync(m.Id);
        grandTotal += voices.Count;
        Log.Information("");
        Log.Information("  {Model} ({Count} voices):", m.Id, voices.Count);

        const int cols = 2;
        for (var i = 0; i < voices.Count; i += cols)
        {
            var parts = new List<string>();
            for (var j = 0; j < cols && i + j < voices.Count; j++)
            {
                var v = voices[i + j];
                parts.Add(v.Description.Length > 0
                    ? $"{v.Id} ({v.Description})"
                    : v.Id);
            }
            Log.Information("    {Voices}", string.Join("  ", parts));
        }
    }

    Log.Information("");
    Log.Information("Total: {Total} voices across {ModelCount} models", grandTotal, models.Count);
}

static async Task HandleStream(
    IServiceProvider services,
    string text,
    string model,
    string voice,
    string? instructions,
    double? temperature,
    int? seed,
    IAudioPlayer audioPlayer,
    string? device,
    double? volume)
{
    var ttsService = services.GetRequiredService<ITtsService>();

    var (audioStream, format) = await ttsService.SynthesizeToStreamAsync(text, voice, instructions, temperature, seed, model);

    Log.Information("Streaming audio to speakers (model={Model}, voice={Voice}, format={Format})", model, voice, format);

    using var ms = new MemoryStream();
    await audioStream.CopyToAsync(ms);
    ms.Position = 0;

    await audioPlayer.PlayStreamAsync(ms, format, volume, device);
}

static (string? device, double? volume) ResolveOutputSettings(
    AppSettings settings,
    string? outputProfileName,
    string? deviceOverride,
    double? volumeOverride)
{
    var defaultProfile = settings.OutputProfiles.FirstOrDefault(p => p.Value.IsDefault);
    var profileName = outputProfileName ?? defaultProfile.Key;
    OutputProfile? profile = null;
    if (profileName != null && settings.OutputProfiles.TryGetValue(profileName, out var p))
    {
        profile = p;
        Log.Information("Using output profile: {ProfileName}", profileName);
    }

    var device = deviceOverride ?? profile?.Device;
    var volume = volumeOverride ?? profile?.Volume;

    return (device, volume);
}

static void HandleListDevices(IServiceProvider services)
{
    var audioPlayer = services.GetRequiredService<IAudioPlayer>();
    var devices = audioPlayer.ListDevices();

    if (devices.Count == 0)
    {
        Log.Information("No audio output devices found (or aplay not available).");
        return;
    }

    Log.Information("Available audio output devices:");
    foreach (var d in devices)
        Log.Information("  {Id,-40} {Description}", d.Id, d.Description);
}

static void HandleListOutputProfiles(AppSettings settings)
{
    if (settings.OutputProfiles.Count == 0)
    {
        Log.Information("No output profiles configured.");
        return;
    }

    Log.Information("Output profiles:");
    foreach (var (name, profile) in settings.OutputProfiles)
    {
        var marker = profile.IsDefault ? " *" : "";
        var device = string.IsNullOrEmpty(profile.Device) ? "(system default)" : profile.Device;
        Log.Information("  {Name}{Marker}: device={Device}, volume={Volume}",
            name, marker, device, profile.Volume);
    }
}

static void SaveOutputProfile(
    string projectRoot,
    string name,
    string? device,
    double? volume,
    bool isDefault)
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

    if (isDefault)
    {
        foreach (var kvp in appSettings.OutputProfiles)
            kvp.Value.IsDefault = false;
    }

    var isNew = !appSettings.OutputProfiles.ContainsKey(name);

    appSettings.OutputProfiles[name] = new OutputProfile
    {
        Device = device ?? "",
        Volume = (int)(volume ?? 80),
        IsDefault = isDefault || (isNew && !appSettings.OutputProfiles.Any(p => p.Value.IsDefault))
    };

    var outputJson = JsonSerializer.Serialize(appSettings, jsonOptions);
    File.WriteAllText(configPath, outputJson);

    var action = isNew ? "Saved new" : "Updated";
    var defaultMarker = appSettings.OutputProfiles[name].IsDefault ? " (default)" : "";
    var deviceDisplay = string.IsNullOrEmpty(device) ? "(system default)" : device;
    Log.Information("{Action} output profile '{Name}'{Default}: device={Device}, volume={Volume}",
        action, name, defaultMarker, deviceDisplay, volume ?? 80);
}

static (bool isFile, string format) ParseOutputPath(string output)
{
    var audioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".pcm", ".ogg", ".flac", ".opus", ".m4a", ".aac", ".wma"
    };

    if (output.EndsWith('/') || output.EndsWith('\\'))
        return (false, "mp3");

    if (Directory.Exists(output))
        return (false, "mp3");

    var ext = Path.GetExtension(output);
    if (!string.IsNullOrEmpty(ext) && audioExtensions.Contains(ext))
        return (true, ext.TrimStart('.'));

    if (string.IsNullOrEmpty(ext))
        return (false, "mp3");

    return (true, ext.TrimStart('.'));
}

static string FindProjectRoot()
{
    var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    while (!string.IsNullOrEmpty(dir))
    {
        if (Directory.GetFiles(dir, "*.csproj").Length > 0)
            return dir;

        var parent = Directory.GetParent(dir)?.FullName;
        if (parent == null || parent == dir)
            break;
        dir = parent;
    }

    throw new InvalidOperationException("Cannot find project root directory containing a .csproj file.");
}

static void RegisterSttServices(IServiceCollection services, IConfiguration configuration)
{
    var sttSection = configuration.GetSection("Stt");
    var recorder = sttSection.GetSection("Recorder").Value ?? "pipewire";
    var transcriber = sttSection.GetSection("Transcriber").Value ?? "groq-whisper";
    var clipboard = sttSection.GetSection("Clipboard").Value ?? "wayland";
    var keyboard = sttSection.GetSection("Keyboard").Value ?? "ydotool";
    var transformer = sttSection.GetSection("Transformer").Value ?? "llm";
    var trigger = sttSection.GetSection("Trigger").Value ?? "console";

    Log.Information("[STT] Loading plugins — recorder={Recorder}, transcriber={Transcriber}, transformer={Transformer}, clipboard={Clipboard}, keyboard={Keyboard}, trigger={Trigger}",
        recorder, transcriber, transformer, clipboard, keyboard, trigger);

    switch (recorder)
    {
        case "pipewire":
            services.AddSingleton<IAudioRecorder, PipeWireRecorder>();
            break;
        default:
            throw new InvalidOperationException($"Unknown IAudioRecorder implementation: '{recorder}'");
    }

    switch (transcriber)
    {
        case "groq-whisper":
            services.AddSingleton<ITranscriptionService, GroqWhisperTranscriber>();
            break;
        default:
            throw new InvalidOperationException($"Unknown ITranscriptionService implementation: '{transcriber}'");
    }

    switch (transformer)
    {
        case "llm":
            services.AddSingleton<ITextTransformer, LlmTextTransformer>();
            break;
        case "none":
            services.AddSingleton<ITextTransformer>(_ => new NullTextTransformer());
            break;
        default:
            throw new InvalidOperationException($"Unknown ITextTransformer implementation: '{transformer}'");
    }

    switch (clipboard)
    {
        case "wayland":
            services.AddSingleton<IClipboardService, WaylandClipboardService>();
            break;
        default:
            throw new InvalidOperationException($"Unknown IClipboardService implementation: '{clipboard}'");
    }

    switch (keyboard)
    {
        case "ydotool":
            services.AddSingleton<IKeyboardSimulator, YdotoolKeyboardSimulator>();
            break;
        default:
            throw new InvalidOperationException($"Unknown IKeyboardSimulator implementation: '{keyboard}'");
    }

    switch (trigger)
    {
        case "console":
            services.AddSingleton<IRecordingTrigger, ConsoleRecordingTrigger>();
            break;
        case "evdev-media":
            services.AddSingleton<IRecordingTrigger, EvdevMediaKeyTrigger>();
            break;

        case "evdev-keyboard":
            services.AddSingleton<IRecordingTrigger, EvdevKeyboardTrigger>();
            break;
        default:
            throw new InvalidOperationException($"Unknown IRecordingTrigger implementation: '{trigger}'");
    }

    services.AddSingleton<ISttRunner, SttRunner>();
}

static void RegisterClipboardTtsServices(IServiceCollection services, IConfiguration configuration)
{
    var cbSection = configuration.GetSection("ClipboardTts");
    var triggerKey = cbSection.GetSection("TriggerKey").Value ?? "Ctrl+Comma";
    var debounceMs = cbSection.GetSection("TriggerDebounceMs").Value;
    var effectiveDebounce = string.IsNullOrEmpty(debounceMs) ? 300 : int.Parse(debounceMs);

    Log.Information("[ClipboardTts] Config — triggerKey={TriggerKey}, debounceMs={DebounceMs}", triggerKey, effectiveDebounce);

    services.AddSingleton(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<ClipboardTtsTrigger>>();
        return new ClipboardTtsTrigger(triggerKey, effectiveDebounce, logger);
    });

    services.AddSingleton<IClipboardTtsRunner, ClipboardTtsRunner>();
}

static void PrintUsage(IServiceProvider services)
{
    var settings = services.GetRequiredService<IOptions<AppSettings>>().Value;
    Log.Information("Usage: benow-conversation <text-or-file> [options]");
    Log.Information("");
    Log.Information("Arguments:");
    Log.Information("  <text-or-file>                  Text to synthesize, or path to a text file");
    Log.Information("");
    Log.Information("Options:");
    var defaultName = settings.Personas.FirstOrDefault(p => p.Value.IsDefault).Key ?? "(none)";
    Log.Information("  --persona <name>                Use a named persona from config (default: {Default})", defaultName);
    Log.Information("  --voice <id>                    Override voice ID, or \"all\" for all voices");
    Log.Information("  --model <model>                 Override TTS model, or \"all\" for all providers");
    Log.Information("  --openai-instructions <text>    Style instructions (warns and skips for non-OpenAI models)");
    Log.Information("  --temperature <0-2>             Sampling temperature (lower=consistent, higher=expressive)");
    Log.Information("  --seed <int>                    Seed for reproducible output");
    Log.Information("  --output <file|dir>             Save to file (format from extension) or directory (timestamped filename)");
    Log.Information("  --text-file <path>              Read text from file");
    Log.Information("  --play                          Play audio through speakers after generation (default behavior)");
    Log.Information("  --no-play                      Suppress playback (overrides default play behavior)");
    Log.Information("  --stream                        Stream and play audio in real-time (lowest latency)");
    Log.Information("  --output-profile <name>         Use a named output profile (device + volume)");
    Log.Information("  --device <device>               Override audio output device (ALSA device name)");
    Log.Information("  --volume <0-100>                Override playback volume");
    Log.Information("  --save-output-profile <name>    Save current output params as a named profile");
    Log.Information("  --set-output-default            Mark output profile as default");
    Log.Information("  --list-models                   List available TTS models");
    Log.Information("  --list-voices                   List voices for current/specified model");
    Log.Information("  --list-personas                 List named personas");
    Log.Information("  --list-output-profiles          List named output profiles");
    Log.Information("  --list-devices                  List available audio output devices");
    Log.Information("  --save-persona <name>           Save current params as a named persona");
    Log.Information("  --set-default                   Mark persona as default (use with --save-persona or --persona)");
    Log.Information("  --daemon                        Run as OpenAI-compatible proxy (TTS on responses)");
    Log.Information("  --stt                           Start STT mode (record → transcribe → paste)");
    Log.Information("  --stt-setup                     Interactive setup: detect trigger device and key codes");
    Log.Information("  --stt --daemon                  STT + daemon mode (both run concurrently)");
    Log.Information("  --no-cleanup                    Skip transcript cleanup step");
    Log.Information("  --cleanup-model <model>         Override cleanup LLM model");
    Log.Information("  --extract-voice <path>          Extract clean voice sample from video for cloning");
    Log.Information("  --voice-name <name>             Name for extracted voice (used with --extract-voice)");
    Log.Information("  --help                          Show this help message");
}
