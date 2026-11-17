using UrlShortener.Core.Abstractions;

namespace UrlShortener.UnitTests.TestDoubles;

/// <summary>Returns a scripted sequence of codes so collision handling can be tested deterministically.</summary>
internal sealed class SequenceCodeGenerator(params string[] codes) : IShortCodeGenerator
{
    private readonly Queue<string> _codes = new(codes);

    public int Calls { get; private set; }

    public string Generate()
    {
        Calls++;
        return _codes.Count > 1 ? _codes.Dequeue() : _codes.Peek();
    }
}
