using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace ConnectionPool.Dataverse.Tests;

internal sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>Real BCL listener scoped to one meter name, so parallel tests cannot interfere.</summary>
internal sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    public ConcurrentQueue<Measurement> Measurements { get; } = new();

    public MetricCollector(string meterName, bool throwFromCallback = false)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>((i, v, tags, _) => Add(i, v, tags, throwFromCallback));
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags, throwFromCallback));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags, throwFromCallback));
        _listener.Start();
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, bool throwFromCallback)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }

        Measurements.Enqueue(new Measurement(instrument.Name, value, dict));
        if (throwFromCallback)
        {
            throw new InvalidOperationException("listener bug");
        }
    }

    public List<Measurement> Of(string instrument) => Measurements.Where(m => m.Instrument == instrument).ToList();

    public double Sum(string instrument) => Of(instrument).Sum(m => m.Value);

    /// <summary>Forces every subscribed observable gauge's callback to run once, capturing its
    /// current value into <see cref="Measurements"/>. Push-based instruments (counters,
    /// histograms) don't need this - they report as the operations that drive them happen.</summary>
    public void Collect() => _listener.RecordObservableInstruments();

    public void Dispose() => _listener.Dispose();
}

