# Stage 1: Project Setup + Text to Audio File

## Goal

Set up the .NET solution structure and make the first successful TTS API call. Given text (direct or from file), submit to OpenRouter Voxtral Mini TTS and save the resulting MP3 to disk.

## Prerequisites

- .NET 8 SDK installed
- OpenRouter API key (user provides, goes in `appsettings.Development.json`)

## Tasks

### 1. Solution & Project Structure

Create the repository skeleton:

```
conversation/
├── src/
│   └── benow-conversation/
│       ├── benow-conversation.csproj              (.NET 8 console)
│       ├── Program.cs
│       ├── appsettings.json
│       ├── appsettings.Development.json           (gitignored)
│       ├── Services/
│       │   ├── ITtsService.cs
│       │   └── TtsService.cs
│       ├── Models/
│       │   └── TtsRequest.cs
│       └── Configuration/
│           └── AppSettings.cs
├── tests/
│   └── benow-conversation.Tests/
│       └── benow-conversation.Tests.csproj        (xUnit test project)
├── benow-conversation.sln
├── .gitignore
└── docs/
    └── plans/
```

**Steps:**
- `dotnet new sln -n benow-conversation`
- `dotnet new console -n benow-conversation -o src/benow-conversation`
- `dotnet new xunit -n benow-conversation.Tests -o tests/benow-conversation.Tests`
- `dotnet sln add src/benow-conversation/benow-conversation.csproj`
- `dotnet sln add tests/benow-conversation.Tests/benow-conversation.Tests.csproj`
- Add test project reference to app project
- Create `.gitignore` (standard .NET + `appsettings.Development.json` + `output/`)
- Create empty `Services/`, `Models/`, `Configuration/` directories

### 2. Configuration (AppSettings)

**`Configuration/AppSettings.cs`** — strongly-typed config classes:

```csharp
public class AppSettings
{
    public OpenRouterSettings OpenRouter { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
}

public class OpenRouterSettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string TtsModel { get; set; } = "mistralai/voxtral-mini-tts-2603";
}

public class AudioSettings
{
    public string OutputFormat { get; set; } = "mp3";
    public string OutputPath { get; set; } = "output";
}
```

**`appsettings.json`** — defaults with empty API key:
```json
{
  "OpenRouter": {
    "ApiKey": "",
    "BaseUrl": "https://openrouter.ai/api/v1",
    "TtsModel": "mistralai/voxtral-mini-tts-2603"
  },
  "Audio": {
    "OutputFormat": "mp3",
    "OutputPath": "output"
  }
}
```

**`appsettings.Development.json`** — user fills in API key (gitignored):
```json
{
  "OpenRouter": {
    "ApiKey": "sk-or-..."
  }
}
```

### 3. Models

**`Models/TtsRequest.cs`** — request/response models for the OpenRouter TTS API. Uses `[JsonPropertyName]` annotations for explicit control rather than relying solely on `JsonNamingPolicy.CamelCase`, since `ResponseFormat` -> `response_format` has a non-trivial mapping:

```csharp
public class TtsRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("input")]
    public string Input { get; set; } = "";

    [JsonPropertyName("voice")]
    public string Voice { get; set; } = "alloy";

    [JsonPropertyName("response_format")]
    public string ResponseFormat { get; set; } = "mp3";
}
```

> **Note**: The `voice: "alloy"` default is an OpenAI-standard voice name. Verify it works with Voxtral on OpenRouter at implementation time. If Voxtral doesn't support built-in voices, a different default or explicit voice selection may be needed.

### 4. TTS Service

**`Services/ITtsService.cs`**:
```csharp
public interface ITtsService
{
    Task<string> SynthesizeToFileAsync(string text, string? outputFileName = null);
}
```

**`Services/TtsService.cs`**:
- Injected via DI: `IOptions<AppSettings>`, `IHttpClientFactory`
- JSON serialization uses `System.Text.Json` with `[JsonPropertyName]` attributes on the model (see Task 3). Also configure `JsonSerializerOptions` with `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` as a fallback for any un-annotated properties.
- HttpClient timeout: configure via `IHttpClientFactory` registration (`HttpClient.Timeout = TimeSpan.FromSeconds(30)` default; make configurable via `OpenRouter:RequestTimeoutSeconds` in settings if needed, but 30s is sufficient for Stage 1 text lengths under 300 words)
- `SynthesizeToFileAsync`:
  1. POST to `{BaseUrl}/audio/speech` with JSON body (`model`, `input`, `voice`, `response_format`)
  2. Set `Authorization: Bearer {ApiKey}` header
  3. Validate response: check HTTP status code, check `Content-Type` is `audio/mpeg`
  4. On error response (non-2xx, or JSON body instead of audio): throw with API error message
  5. Resolve `Audio.OutputPath` relative to the **project root** (the directory containing the `.csproj`), not the runtime `bin/` directory
  6. Ensure output directory exists
  7. Generate filename: `outputFileName` if provided, otherwise `yyyyMMdd-HHmmss.mp3`
  8. Write bytes to `{resolved output path}/{filename}`
  9. Return the full output path

**Error handling** — all errors must produce clear, actionable messages:
- Missing API key: `"OpenRouter API key is not configured. Set 'OpenRouter:ApiKey' in appsettings.Development.json."`
- API auth failure (401/403): `"OpenRouter API key is invalid or unauthorized. Check your API key in appsettings.Development.json."`
- API rate limit (429): `"OpenRouter rate limit exceeded. Wait a moment and try again."`
- API error (other non-2xx): `"TTS API request failed (HTTP {status}): {response body}". Include the response body for diagnosis.`
- Network failure: `"Unable to connect to OpenRouter at {BaseUrl}. Check your network connection."`
- Disk write failure: `"Failed to write audio file to '{path}': {inner exception message}."`
- Empty API response: `"TTS API returned an empty response. The service may be experiencing issues."`
- Every error message includes: what went wrong + how to fix it

### 5. Program.cs (Host Setup + CLI)

Use `HostBuilder` pattern:
- Register `AppSettings` from config
- Register `ITtsService` / `TtsService` as singleton
- Register `HttpClient` via `IHttpClientFactory`
- Required NuGet packages (not included in `dotnet new console` template):
  - `Microsoft.Extensions.Hosting` (brings in DI, Options, Configuration)
  - `Microsoft.Extensions.Http` (for `IHttpClientFactory`)

**Path resolution** — all relative paths resolve against the **project root** (directory containing the `.csproj` file), not the runtime working directory (`bin/Debug/net8.0/`):
- `Audio.OutputPath` (e.g. `output/` resolves to `src/benow-conversation/output/`)
- Input file paths for `--text-file` and implicit file detection
- Use `AppContext.BaseDirectory` traversal or store project root at startup

CLI argument handling:
- Manual index-based argument parsing (sufficient for Stage 1's small flag set: `--output`, `--text-file`, `--help`). Avoid `System.CommandLine` dependency at this stage; refactor to a proper parser library in a later stage if the CLI grows.
- First positional arg is treated as text input
- If the arg resolves to an existing file (relative to project root), read the file contents as text
- Otherwise, use the arg directly as text
- `--output <filename>` optional: override output filename
- `--text-file <path>` explicit flag: force reading from a file path
- `--help` / no args: print usage

**CLI error handling** — all errors produce clear, actionable messages:
- No arguments: print usage/help
- `--text-file <path>` where path doesn't exist: `"Text file not found: '{path}'. Check that the file exists relative to the project root at '{projectRoot}'."`
- Empty text file: `"Text file '{path}' is empty. Provide a file containing the text to synthesize."`
- No input after resolution: `"No text input provided. Pass text as an argument or use --text-file <path>."`

```csharp
// Pseudocode for argument resolution
var projectRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
// Navigate up from bin/Debug/net8.0 to project root
while (!File.Exists(Path.Combine(projectRoot, "benow-conversation.csproj")))
    projectRoot = Directory.GetParent(projectRoot)?.FullName ?? throw new InvalidOperationException($"Cannot find project root.");

var input = args[0];

var resolvedPath = Path.GetFullPath(input, projectRoot);
if (File.Exists(resolvedPath))
    text = await File.ReadAllTextAsync(resolvedPath);
else
    text = input;

var outputPath = await ttsService.SynthesizeToFileAsync(text, outputFileName);
Console.WriteLine($"Saved: {outputPath}");
```

### 6. Tests

**`tests/benow-conversation.Tests/`** — xUnit test project:

Tests to write:
- `TtsServiceTests`:
  - `SynthesizeToFileAsync_WithValidText_ReturnsFilePath` — integration test (requires API key, marked with `[Fact(Skip = "Requires API key")]` or uses a mock)
  - `SynthesizeToFileAsync_CreatesOutputDirectory_IfMissing` — unit test with mocked `HttpMessageHandler` (inject custom `DelegatingHandler` via `IHttpClientFactory`)
  - `SynthesizeToFileAsync_GeneratesTimestampedFilename` — verify filename format
  - `SynthesizeToFileAsync_ThrowsOnNon2xx_WithActionableMessage` — verify error message includes status and body
  - `SynthesizeToFileAsync_ThrowsOnMissingApiKey_WithConfigGuidance` — verify error tells user where to set the key
- `AppSettingsTests`:
  - `AppSettings_LoadsFromJson` — verify config binding works
- `TextResolutionTests`:
  - `ResolvesDirectText` — when arg is not a file path, use as-is
  - `ResolvesFilePath` — when arg is an existing file, read contents
  - `ThrowsForMissingFile` — when explicit `--text-file` path doesn't exist, error includes project root path
  - `ResolvesPathsRelativeToProjectRoot` — file lookup uses project root, not working directory

### 7. .gitignore

```
# .NET
bin/
obj/
*.user
*.suo

# Config with secrets
appsettings.Development.json

# Output
output/

# IDE
.vscode/
.idea/
```

## Acceptance Criteria

- [ ] `dotnet build` succeeds with no warnings
- [ ] `dotnet test` passes (unit tests, mocked HttpClient)
- [ ] `dotnet run --project src/benow-conversation -- "Hello, this is a test of text to speech"` produces an MP3 file in `output/`
- [ ] `dotnet run --project src/benow-conversation -- input.txt` reads text from file and produces MP3
- [ ] Output file is a valid, playable MP3
- [ ] `appsettings.Development.json` is gitignored
- [ ] API key is not logged or committed

## CLI Usage

```bash
# Direct text
dotnet run --project src/benow-conversation -- "Hello world"

# Text from file
dotnet run --project src/benow-conversation -- ./my-text.txt

# Explicit text file flag
dotnet run --project src/benow-conversation -- --text-file ./my-text.txt

# Custom output filename
dotnet run --project src/benow-conversation -- "Hello world" --output greeting.mp3
```

## API Reference (OpenRouter TTS)

```
POST https://openrouter.ai/api/v1/audio/speech
Authorization: Bearer sk-or-...
Content-Type: application/json

{
  "model": "mistralai/voxtral-mini-tts-2603",
  "input": "Hello world",
  "voice": "alloy",
  "response_format": "mp3"
}

Response: raw audio bytes (Content-Type: audio/mpeg)
```

## Out of Scope for Stage 1

- Voice cloning (`ref_audio` / `voice_id`)
- Audio playback through speakers
- Video file input
- Stdin pipe support
- Streaming
- Any UI

## Design Notes

### JSON Serialization Strategy
Use `[JsonPropertyName]` on `TtsRequest` properties for explicit, unambiguous mapping to the API's snake_case/camelCase fields. Additionally configure `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }` as the default serializer policy so any future models serialize correctly without per-property annotations. This dual approach is verbose but defensive.

### HttpClient Mocking in Tests
`HttpClient` cannot be mocked directly (it's not an interface). The standard approach is to create a `MockHttpMessageHandler : DelegatingHandler` that returns a canned `HttpResponseMessage`. Register it with `IHttpClientFactory` in the test host, or construct `HttpClient` directly with the mock handler. For `TtsService` tests:
- Success case: return `new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audioBytes) }` with `Content-Type: audio/mpeg`
- Error cases: return appropriate status codes with JSON error bodies

### Project Root Detection
The app runs from `bin/Debug/net8.0/` but all file I/O must be relative to the project root (where `.csproj` lives). Two viable approaches:
1. **Walk up from `Assembly.GetExecutingAssembly().Location`** looking for the `.csproj` file (simple, works for `dotnet run` and `dotnet build`)
2. **Set `ContentRootPath` in `HostBuilder`** to `Directory.GetCurrentDirectory()` which is the project root when using `dotnet run --project`

Prefer option 2 — `HostBuilder` already sets `ContentRootPath` correctly when invoked via `dotnet run --project src/benow-conversation`. Access via `IHostEnvironment.ContentRootPath`. Only fall back to option 1 if `ContentRootPath` doesn't point to a directory containing the `.csproj`.

### Config Validation at Startup
Validate `OpenRouter:ApiKey` is non-empty during host startup (before any TTS call). Use `IValidateOptions<AppSettings>` or a simple post-binding check in `Program.cs`. Fail fast with a clear message rather than waiting for the first API call to fail.

### Error Response Contract
OpenRouter errors return JSON bodies like:
```json
{
  "error": {
    "message": "...",
    "type": "...",
    "code": "..."
  }
}
```
Attempt to deserialize error responses as this structure to extract the `message` field for display. Fall back to raw body text if deserialization fails.
