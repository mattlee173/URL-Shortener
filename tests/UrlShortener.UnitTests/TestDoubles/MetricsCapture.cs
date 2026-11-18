using System.Diagnostics.Metrics;

using UrlShortener.Infrastructure.Observability;

namespace UrlShortener.UnitTests.TestDoubles;

/// <summary>
/// Creates a <see cref="ShortenerMetrics"/> on a private meter and records what it publishes.
/// Listening is scoped to that one meter instance, so parallel tests can't see each other's values.
/// </summary>
internal sealed class MetricsCapture : IDisposable
{
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly Lock _gate = new();
    private readonly List<(string Instrument, long Value, string? Result)> _measurements = [];

    public MetricsCapture()
    {
        Metrics = new ShortenerMetrics(_factory);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (_factory.Created.Contains(instrument.Meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<int>((i, v, t, s) => OnMeasurement(i, v, t, s));
        _listener.Start();
    }

    public ShortenerMetrics Metrics { get; }

    /// <summary>Sum of all values recorded by a counter, optionally filtered by its "result" tag.</summary>
    public long Sum(string instrument, string? result = null)
    {
        lock (_gate)
        {
            return _measurements.Where(m => m.Instrument == instrument && (result is null || m.Result == result)).Sum(m => m.Value);
        }
    }

    /// <summary>Current value of an observable gauge.</summary>
    public long Observe(string instrument)
    {
        lock (_gate)
        {
            _measurements.RemoveAll(m => m.Instrument == instrument);
        }

        _listener.RecordObservableInstruments();
        return Sum(instrument);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _factory.Dispose();
    }

    private void OnMeasurement(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        string? result = null;
        foreach (var tag in tags)
        {
            if (tag.Key == "result")
            {
                result = tag.Value as string;
            }
        }

        lock (_gate)
        {
            _measurements.Add((instrument.Name, value, result));
        }
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        public List<Meter> Created { get; } = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            Created.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in Created)
            {
                meter.Dispose();
            }
        }
    }
}
