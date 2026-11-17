using UrlShortener.Core;
using UrlShortener.Core.Services;

namespace UrlShortener.UnitTests.Core;

public class RandomShortCodeGeneratorTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(12)]
    public void Generate_ReturnsCodeOfConfiguredLength(int length)
    {
        var generator = new RandomShortCodeGenerator(new ShortenerOptions { CodeLength = length });

        Assert.Equal(length, generator.Generate().Length);
    }

    [Fact]
    public void Generate_UsesOnlyBase62Characters()
    {
        var generator = new RandomShortCodeGenerator(new ShortenerOptions());

        for (var i = 0; i < 1_000; i++)
        {
            Assert.All(generator.Generate(), c => Assert.Contains(c, RandomShortCodeGenerator.Alphabet));
        }
    }

    [Fact]
    public void Generate_ProducesDistinctCodes()
    {
        var generator = new RandomShortCodeGenerator(new ShortenerOptions());

        var codes = Enumerable.Range(0, 10_000).Select(_ => generator.Generate()).ToHashSet(StringComparer.Ordinal);

        // 10k draws from 62^7 (~3.5e12): the chance of any collision is ~1.4e-5.
        Assert.Equal(10_000, codes.Count);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(33)]
    public void Constructor_RejectsUnsafeLengths(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RandomShortCodeGenerator(new ShortenerOptions { CodeLength = length }));
    }
}
