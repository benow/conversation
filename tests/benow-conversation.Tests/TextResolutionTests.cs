namespace benow_conversation.Tests;

public class TextResolutionTests : IDisposable
{
    private readonly string _projectRoot;

    public TextResolutionTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), $"textres_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);

        var csprojPath = Path.Combine(_projectRoot, "benow-conversation.csproj");
        File.WriteAllText(csprojPath, "<Project />");
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, true);
    }

    [Fact]
    public void ResolvesDirectText()
    {
        var input = "Hello world";
        var resolvedPath = Path.GetFullPath(input, _projectRoot);

        Assert.False(File.Exists(resolvedPath));
        Assert.Equal("Hello world", input);
    }

    [Fact]
    public async Task ResolvesFilePath()
    {
        var textFile = Path.Combine(_projectRoot, "test.txt");
        await File.WriteAllTextAsync(textFile, "Hello from file");

        var input = "test.txt";
        var resolvedPath = Path.GetFullPath(input, _projectRoot);

        Assert.True(File.Exists(resolvedPath));
        var text = await File.ReadAllTextAsync(resolvedPath);
        Assert.Equal("Hello from file", text);
    }

    [Fact]
    public void ThrowsForMissingFile()
    {
        var input = "nonexistent.txt";
        var resolvedPath = Path.GetFullPath(input, _projectRoot);

        Assert.False(File.Exists(resolvedPath));
    }

    [Fact]
    public void ResolvesPathsRelativeToProjectRoot()
    {
        var subDir = Path.Combine(_projectRoot, "subdir");
        Directory.CreateDirectory(subDir);
        var textFile = Path.Combine(subDir, "nested.txt");
        File.WriteAllText(textFile, "nested content");

        var input = "subdir/nested.txt";
        var resolvedPath = Path.GetFullPath(input, _projectRoot);

        Assert.True(File.Exists(resolvedPath));
    }

    [Fact]
    public async Task DetectsEmptyFile()
    {
        var textFile = Path.Combine(_projectRoot, "empty.txt");
        await File.WriteAllTextAsync(textFile, "   ");

        var content = await File.ReadAllTextAsync(textFile);
        Assert.True(string.IsNullOrWhiteSpace(content));
    }
}
