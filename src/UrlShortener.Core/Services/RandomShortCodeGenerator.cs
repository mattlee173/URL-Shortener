using System.Security.Cryptography;

using UrlShortener.Core.Abstractions;

namespace UrlShortener.Core.Services;

/// <summary>
/// Generates Base62 codes from a cryptographically secure RNG.
/// Random (rather than sequential/hash-of-id) codes are not enumerable, so callers cannot
/// walk the keyspace to discover other people's links.
/// </summary>
public sealed class RandomShortCodeGenerator : IShortCodeGenerator
{
    internal const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private readonly int _length;

    public RandomShortCodeGenerator(ShortenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CodeLength, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.CodeLength, 32);
        _length = options.CodeLength;
    }

    public string Generate() => RandomNumberGenerator.GetString(Alphabet, _length);
}
