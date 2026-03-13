using BotService.Services.Content;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotService.Tests.Services;

public class MessageContentProviderTests : IDisposable
{
    private readonly MessageContentProvider _provider;
    private readonly string _tempDir;

    public MessageContentProviderTests()
    {
        _provider = new MessageContentProvider(Mock.Of<ILogger<MessageContentProvider>>());
        _tempDir = Path.Combine(Path.GetTempPath(), $"bot-messages-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void GetMessage_WithDefaults_ReturnsNonEmpty()
    {
        _provider.LoadMessages(_tempDir); // No file, uses defaults
        
        var msg = _provider.GetMessage("opener");
        Assert.False(string.IsNullOrEmpty(msg));
    }

    [Fact]
    public void GetMessageForDepth_ReturnsAppropriateStage()
    {
        _provider.LoadMessages(_tempDir);

        // First message should be opener
        var opener = _provider.GetMessageForDepth(0);
        Assert.False(string.IsNullOrEmpty(opener));

        // Depth 1-2 should be followup
        var followup = _provider.GetMessageForDepth(1);
        Assert.False(string.IsNullOrEmpty(followup));

        // Depth 3-4 should be deepening
        var deepening = _provider.GetMessageForDepth(3);
        Assert.False(string.IsNullOrEmpty(deepening));

        // Depth 5+ should be continuing
        var continuing = _provider.GetMessageForDepth(10);
        Assert.False(string.IsNullOrEmpty(continuing));
    }

    [Fact]
    public void GetMessage_UnknownStage_FallsBackToOpener()
    {
        _provider.LoadMessages(_tempDir);

        var msg = _provider.GetMessage("nonexistent_stage");
        Assert.False(string.IsNullOrEmpty(msg));
    }

    [Fact]
    public void LoadMessages_FromCustomFile_LoadsCorrectly()
    {
        File.WriteAllText(Path.Combine(_tempDir, "messages.json"), """
        {
          "opener": ["Custom opener 1", "Custom opener 2"],
          "followup": ["Custom followup"]
        }
        """);

        _provider.LoadMessages(_tempDir);

        var msg = _provider.GetMessage("opener");
        Assert.StartsWith("Custom opener", msg);
    }

    [Fact]
    public void GetMessage_DefaultOpeners_AreSwedish()
    {
        _provider.LoadMessages(_tempDir);

        // Run 20 times, at least one should contain Swedish characters/words
        var messages = Enumerable.Range(0, 20).Select(_ => _provider.GetMessage("opener")).ToList();
        Assert.True(messages.Any(m => m.Contains("Hej") || m.Contains("Tjena") || m.Contains("Hallå") || m.Contains("God")),
            $"Expected Swedish openers, got: {string.Join(", ", messages.Distinct())}");
    }

    [Fact]
    public void GetMessage_AllStages_HaveContent()
    {
        _provider.LoadMessages(_tempDir);

        var stages = new[] { "opener", "followup", "deepening", "continuing", "fika_invite" };
        foreach (var stage in stages)
        {
            var msg = _provider.GetMessage(stage);
            Assert.False(string.IsNullOrEmpty(msg), $"Stage '{stage}' returned empty message");
        }
    }
}
