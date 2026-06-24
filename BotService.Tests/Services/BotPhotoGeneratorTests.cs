using BotService.Models;
using BotService.Services.Photo;
using Xunit;

namespace BotService.Tests.Services;

/// <summary>
/// T360+T361 — Unit tests for BotPhotoGenerator and PhotoUploader.
/// </summary>
public class BotPhotoGeneratorTests
{
    [Fact]
    public async Task GeneratePortrait_ReturnsNonEmptyBytes()
    {
        var generator = new PlaceholderPhotoGenerator();
        var persona = new BotPersona
        {
            Id = "bot_test",
            FirstName = "Test",
            LastName = "Bot",
            Gender = "female",
            City = "Stockholm"
        };

        var result = await generator.GeneratePortraitAsync(persona);

        Assert.NotNull(result);
        Assert.True(result.Length > 100, $"PNG too small: {result.Length} bytes");

        // Verify PNG signature
        Assert.Equal(0x89, result[0]);
        Assert.Equal((byte)'P', result[1]);
        Assert.Equal((byte)'N', result[2]);
        Assert.Equal((byte)'G', result[3]);
    }

    [Fact]
    public async Task GeneratePortrait_ProducesValidPng()
    {
        var generator = new PlaceholderPhotoGenerator();
        var persona = new BotPersona { Id = "x", FirstName = "A", LastName = "B" };

        var result = await generator.GeneratePortraitAsync(persona);

        // Must have IHDR, IDAT, IEND chunks
        var pngStr = System.Text.Encoding.ASCII.GetString(result);
        Assert.Contains("IHDR", pngStr);
        Assert.Contains("IDAT", pngStr);
        Assert.Contains("IEND", pngStr);
    }

    [Fact]
    public async Task GenerateLifestylePhotos_ReturnsTwoImages()
    {
        var generator = new PlaceholderPhotoGenerator();
        var persona = new BotPersona { Id = "x", FirstName = "A" };

        var results = await generator.GenerateLifestylePhotosAsync(persona);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.data.Length > 100);
            Assert.NotEmpty(r.description);
        });
    }

    [Fact]
    public async Task DifferentPersonas_GetDifferentColors()
    {
        var generator = new PlaceholderPhotoGenerator();
        var p1 = new BotPersona { Id = "a", FirstName = "Alice" };
        var p2 = new BotPersona { Id = "b", FirstName = "Bob" };

        var img1 = await generator.GeneratePortraitAsync(p1);
        var img2 = await generator.GeneratePortraitAsync(p2);

        // Different personas should produce different images
        Assert.False(img1.SequenceEqual(img2),
            "Different personas should get different placeholder colors");
    }

    [Fact]
    public void PlaceholderGenerator_IsNotRealPhotoGenerator()
    {
        var generator = new PlaceholderPhotoGenerator();
        Assert.False(generator.IsRealPhotoGenerator);
    }
}
