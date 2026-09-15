using System.Diagnostics.Metrics;
using System.Linq;
using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Metrics.Tests;

/// <summary>
/// Verifies <see cref="PoolMetrics"/> publishes the expected observable gauges with the expected
/// values/tags, using the real <see cref="System.Diagnostics.Metrics.MeterListener"/> API (no
/// OpenTelemetry package needed to observe emitted measurements). Each test uses a unique meter
/// name (via the <c>meterName</c> constructor override) so concurrently-running tests never
/// observe each other's instruments.
/// </summary>
public class PoolMetricsTests
{
    [Fact]
    public void Constructor_Throws_WhenPoolNameEmpty()
    {
        Assert.Throws<ArgumentException>(() => new PoolMetrics(string.Empty, () => SampleStats()));
    }

    [Fact]
    public void Constructor_Throws_WhenStatsProviderNull()
    {
        Assert.Throws<ArgumentNullException>(() => new PoolMetrics("pool-a", null!));
    }

    [Fact]
    public void ObservableGauges_ReportCurrentStats_WhenCollected()
    {
        var meterName = UniqueMeterName();
        var stats = SampleStats(maxSize: 8, created: 3, idle: 2, leased: 1, unhealthy: 0, waiting: 4, createFailures: 5, operationalFailures: 6, leaks: 7);
        using var metrics = new PoolMetrics("pool-a", () => stats, meterName);

        var measurements = CollectMeasurements(meterName);

        Assert.Equal(8, GetValue(measurements, "dataversepool.pool.max_size"));
        Assert.Equal(3, GetValue(measurements, "dataversepool.pool.created"));
        Assert.Equal(2, GetValue(measurements, "dataversepool.pool.idle"));
        Assert.Equal(1, GetValue(measurements, "dataversepool.pool.leased"));
        Assert.Equal(0, GetValue(measurements, "dataversepool.pool.unhealthy_or_recycling"));
        Assert.Equal(4, GetValue(measurements, "dataversepool.pool.waiting"));
        Assert.Equal(5, GetValue(measurements, "dataversepool.pool.consecutive_create_failures"));
        Assert.Equal(6, GetValue(measurements, "dataversepool.pool.consecutive_operational_failures"));
        Assert.Equal(7, GetValue(measurements, "dataversepool.pool.detected_leak_count"));
    }

    [Fact]
    public void ObservableGauges_TagEachMeasurement_WithPoolName()
    {
        var meterName = UniqueMeterName();
        using var metrics = new PoolMetrics("my-pool", () => SampleStats(), meterName);

        var measurements = CollectMeasurements(meterName);

        Assert.NotEmpty(measurements);
        Assert.All(measurements, m => Assert.Contains(m.Tags, t => t.Key == "pool.name" && Equals(t.Value, "my-pool")));
    }

    [Fact]
    public void Dispose_UnregistersInstruments()
    {
        var meterName = UniqueMeterName();
        var metrics = new PoolMetrics("pool-a", () => SampleStats(), meterName);
        metrics.Dispose();

        var measurements = CollectMeasurements(meterName);

        Assert.Empty(measurements);
    }

    [Fact]
    public async Task AddMetrics_PublishesLiveResourcePoolStats()
    {
        var meterName = UniqueMeterName();
        await using var pool = new ResourcePool<FakeResource>(new FakePolicy(), new PoolOptions { MaxSize = 2 });
        using var metrics = pool.AddMetrics("live-pool", meterName);

        await using var lease = await pool.AcquireAsync();

        var measurements = CollectMeasurements(meterName);

        Assert.Equal(1, GetValue(measurements, "dataversepool.pool.created"));
        Assert.Equal(1, GetValue(measurements, "dataversepool.pool.leased"));
        Assert.Equal(0, GetValue(measurements, "dataversepool.pool.idle"));
    }

    private static string UniqueMeterName() => $"DataversePool.Tests.{Guid.NewGuid():N}";

    private static PoolStats SampleStats(
        int maxSize = 4,
        int created = 1,
        int idle = 1,
        int leased = 0,
        int unhealthy = 0,
        int waiting = 0,
        int createFailures = 0,
        int operationalFailures = 0,
        int leaks = 0) =>
        new(maxSize, created, idle, leased, unhealthy, waiting, createFailures, operationalFailures, leaks);

    private sealed record CapturedMeasurement(string Instrument, object Value, IReadOnlyList<KeyValuePair<string, object?>> Tags);

    private static List<CapturedMeasurement> CollectMeasurements(string meterName)
    {
        var captured = new List<CapturedMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == meterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, _) =>
            captured.Add(new CapturedMeasurement(instrument.Name, measurement, tags.ToArray())));
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            captured.Add(new CapturedMeasurement(instrument.Name, measurement, tags.ToArray())));
        listener.Start();
        listener.RecordObservableInstruments();
        return captured;
    }

    private static long GetValue(List<CapturedMeasurement> measurements, string name)
    {
        var measurement = measurements.Single(m => m.Instrument == name);
        return measurement.Value switch
        {
            int i => i,
            long l => l,
            _ => throw new InvalidOperationException($"Unexpected measurement value type: {measurement.Value.GetType()}"),
        };
    }
}
