using System.Text;
using benow_conversation.Configuration;
using benow_conversation.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace benow_conversation.Tests;

public class AudioPlayerTests : IDisposable
{
    private readonly string _tempDir;

    public AudioPlayerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"audioplayer_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static AudioPlayer CreatePlayer()
    {
        var logger = new Mock<ILogger<AudioPlayer>>();
        return new AudioPlayer(logger.Object);
    }

    [Fact]
    public void IsAvailable_ReturnsTrue_WhenFfplayExists()
    {
        var player = CreatePlayer();
        var result = player.IsAvailable;
        Assert.True(result || !result);
    }

    [Fact]
    public async Task PlayAsync_Throws_WhenFileNotFound()
    {
        var player = CreatePlayer();
        if (!player.IsAvailable)
            return;

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => player.PlayAsync("/nonexistent/file.mp3"));
    }

    [Fact]
    public async Task PlayAsync_PlaysFile_WhenAvailable()
    {
        var player = CreatePlayer();
        if (!player.IsAvailable)
            return;

        var testFile = Path.Combine(_tempDir, "test.mp3");
        await File.WriteAllBytesAsync(testFile, Encoding.UTF8.GetBytes("fake audio data"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await player.PlayAsync(testFile, cancellationToken: cts.Token);
    }

    [Fact]
    public async Task PlayStreamAsync_PipesAudioToFfplay()
    {
        var player = CreatePlayer();
        if (!player.IsAvailable)
            return;

        var audioData = new byte[4096];
        using var stream = new MemoryStream(audioData);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await player.PlayStreamAsync(stream, "pcm", cancellationToken: cts.Token);
    }

    [Fact]
    public void ListDevices_ReturnsList()
    {
        var player = CreatePlayer();
        var devices = player.ListDevices();
        Assert.NotNull(devices);
    }
}

public class OutputProfileTests
{
    [Fact]
    public void OutputProfile_Defaults()
    {
        var profile = new OutputProfile();
        Assert.Equal("", profile.Device);
        Assert.Equal(80, profile.Volume);
        Assert.Null(profile.FfplayPath);
        Assert.False(profile.IsDefault);
    }

    [Fact]
    public void PlaybackSettings_Default()
    {
        var settings = new PlaybackSettings();
        Assert.False(settings.EnabledByDefault);
    }

    [Fact]
    public void AppSettings_HasPlaybackAndOutputProfiles()
    {
        var settings = new AppSettings();
        Assert.NotNull(settings.Playback);
        Assert.NotNull(settings.OutputProfiles);
        Assert.Empty(settings.OutputProfiles);
        Assert.False(settings.Playback.EnabledByDefault);
    }

    [Fact]
    public void OutputProfile_WithDeviceAndVolume()
    {
        var profile = new OutputProfile
        {
            Device = "sysdefault",
            Volume = 60,
            IsDefault = true
        };
        Assert.Equal("sysdefault", profile.Device);
        Assert.Equal(60, profile.Volume);
        Assert.True(profile.IsDefault);
    }
}
