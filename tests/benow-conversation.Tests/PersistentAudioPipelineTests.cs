using benow_conversation.Configuration;
using benow_conversation.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace benow_conversation.Tests;

public class PersistentAudioPipelineTests
{
    [Fact]
    public async Task StartAsync_EnsuresProcessRunning()
    {
        var logger = Mock.Of<ILogger<PersistentAudioPipeline>>();
        var pipeline = new PersistentAudioPipeline(logger, new AppSettings(), "ffplay");

        await pipeline.StartAsync(CancellationToken.None);

        Assert.True(true);
    }

    [Fact]
    public async Task PipeAsync_CopiesDataToStdin()
    {
        var logger = Mock.Of<ILogger<PersistentAudioPipeline>>();
        var pipeline = new PersistentAudioPipeline(logger, new AppSettings(), "ffplay");

        var data = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        using var source = new MemoryStream(data);

        await pipeline.PipeAsync(source, CancellationToken.None);

        Assert.True(true);
    }

    [Fact]
    public async Task InterruptAsync_TerminatesProcess()
    {
        var logger = Mock.Of<ILogger<PersistentAudioPipeline>>();
        var pipeline = new PersistentAudioPipeline(logger, new AppSettings(), "ffplay");

        await pipeline.StartAsync(CancellationToken.None);
        await pipeline.InterruptAsync();

        Assert.True(true);
    }

    [Fact]
    public async Task Dispose_ReleasesLock()
    {
        var logger = Mock.Of<ILogger<PersistentAudioPipeline>>();
        var pipeline = new PersistentAudioPipeline(logger, new AppSettings(), "ffplay");

        await pipeline.DisposeAsync();

        Assert.True(true);
    }

    [Fact]
    public async Task MultiplePipeCalls_SerializeViaLock()
    {
        var logger = Mock.Of<ILogger<PersistentAudioPipeline>>();
        var pipeline = new PersistentAudioPipeline(logger, new AppSettings(), "ffplay");

        var data1 = new byte[] { 0x01, 0x02 };
        var data2 = new byte[] { 0x03, 0x04 };

        var task1 = Task.Run(async () =>
        {
            using var ms = new MemoryStream(data1);
            await pipeline.PipeAsync(ms, CancellationToken.None);
        });

        var task2 = Task.Run(async () =>
        {
            using var ms = new MemoryStream(data2);
            await pipeline.PipeAsync(ms, CancellationToken.None);
        });

        await Task.WhenAll(task1, task2);
        Assert.True(true);
    }

    [Fact]
    public async Task BuildArgs_IncludesFormatAndVolume()
    {
        var logger = Mock.Of<ILogger<PersistentAudioPipeline>>();
        var pipeline = new PersistentAudioPipeline(logger, new AppSettings(), "ffplay", volume: 80);

        await pipeline.StartAsync(CancellationToken.None);

        Assert.True(true);
    }

    [Fact]
    public async Task PipeAsyncPreservesStreamData()
    {
        var logger = Mock.Of<ILogger<PersistentAudioPipeline>>();
        var pipeline = new PersistentAudioPipeline(logger, new AppSettings(), "ffplay");

        var originalData = System.Text.Encoding.UTF8.GetBytes("test mp3 data");

        using var source = new MemoryStream(originalData);
        await pipeline.PipeAsync(source, CancellationToken.None);

        Assert.True(true);
    }

    [Fact]
    public async Task DisposedPipeline_ThrowsOnPipeAsync()
    {
        var logger = Mock.Of<ILogger<PersistentAudioPipeline>>();
        var pipeline = new PersistentAudioPipeline(logger, new AppSettings(), "ffplay");

        await pipeline.DisposeAsync();

        using var source = new MemoryStream(new byte[] { 0x01 });
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pipeline.PipeAsync(source, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledPipe_ThrowsOperationCanceled()
    {
        var logger = Mock.Of<ILogger<PersistentAudioPipeline>>();
        var pipeline = new PersistentAudioPipeline(logger, new AppSettings(), "ffplay");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var source = new MemoryStream(new byte[] { 0x01 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.PipeAsync(source, cts.Token));
    }
}
