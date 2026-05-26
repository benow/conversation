# Tests

**Source:** `tests/benow-conversation.Tests/`

## Overview

The test suite uses **xUnit** with **Moq** for mocking. Tests cover configuration parsing, service logic, proxy behavior, and audio system integration.

## Test Framework & Dependencies

| Package | Version | Purpose |
|---|---|---|
| `xunit` | 2.9.3 | Test framework |
| `xunit.runner.visualstudio` | 3.1.4 | VS/Test runner integration |
| `Moq` | 4.20.72 | Mocking library |
| `Microsoft.Extensions.Hosting` | 10.0.8 | Host/DI for integration tests |
| `coverlet.collector` | 6.0.4 | Code coverage |

## Test Files

| File | Coverage Area |
|---|---|
| `AppSettingsTests.cs` | Configuration model binding and defaults |
| `AudioPlayerTests.cs` | Audio playback, device enumeration, availability detection |
| `ProxyServiceTests.cs` | Proxy request handling, header forwarding, model injection |
| `ProxyIntegrationTests.cs` | End-to-end proxy behavior with mocked backends |
| `TtsServiceTests.cs` | TTS synthesis, format fallback, error handling |
| `TextResolutionTests.cs` | CLI text input resolution (direct text vs file vs file detection) |
| `VoiceDescriptionTests.cs` | Voice ID to description inference logic |

## Running Tests

```sh
dotnet test tests/benow-conversation.Tests
```
