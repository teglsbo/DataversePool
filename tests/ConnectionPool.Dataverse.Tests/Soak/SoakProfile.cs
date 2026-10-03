namespace ConnectionPool.Dataverse.Tests.Soak;

public enum SoakPhaseKind
{
    Warmup,
    Steady,
    Spike,
    Idle,
}

/// <summary>One stretch of the load profile. <see cref="Concurrency"/> workers run flat out (spike) or with think time (steady).</summary>
public sealed record SoakPhase(SoakPhaseKind Kind, TimeSpan Duration, int Concurrency, string Label);

/// <summary>
/// Deterministic (seeded) burn-in profile: spikes, steady load, short and long idle periods, spikes straight
/// after idle (cold connections, expired tokens) and back-to-back spikes. Always ends with an idle phase so
/// the final leak measurements are taken at rest.
/// </summary>
public static class SoakProfile
{
    /// <param name="total">Wall-clock length of the whole run.</param>
    /// <param name="scale">Multiplies every phase duration; below 1 compresses the profile for dry runs.</param>
    public static IReadOnlyList<SoakPhase> Generate(TimeSpan total, int seed, double scale = 1.0, int maxSpikeConcurrency = 64)
    {
        var rng = new Random(seed);
        var phases = new List<SoakPhase>();
        var elapsed = TimeSpan.Zero;

        TimeSpan Secs(int min, int max) => TimeSpan.FromSeconds(rng.Next(min, max + 1) * scale);
        int SpikeConcurrency() => rng.Next(Math.Max(2, maxSpikeConcurrency / 4), maxSpikeConcurrency + 1);

        void Add(SoakPhaseKind kind, TimeSpan duration, int concurrency, string label)
        {
            phases.Add(new SoakPhase(kind, duration, concurrency, label));
            elapsed += duration;
        }

        var finalIdle = TimeSpan.FromSeconds(60 * scale);
        Add(SoakPhaseKind.Warmup, TimeSpan.FromSeconds(60 * scale), 2, "warmup");

        while (elapsed + finalIdle < total)
        {
            var roll = rng.Next(100);
            var longIdleFits = total - elapsed > TimeSpan.FromMinutes(25 * scale);
            if (roll < 10 && longIdleFits)
            {
                // Long enough to outlive idle connection timeouts, token lifetimes and throttle state.
                Add(SoakPhaseKind.Idle, Secs(600, 900), 0, "long-idle");
                Add(SoakPhaseKind.Spike, Secs(10, 30), SpikeConcurrency(), "cold-spike");
            }
            else if (roll < 40)
            {
                Add(SoakPhaseKind.Spike, Secs(10, 45), SpikeConcurrency(), "spike");
                Add(SoakPhaseKind.Idle, Secs(60, 300), 0, "idle");
                Add(SoakPhaseKind.Spike, Secs(10, 45), SpikeConcurrency(), "cold-spike");
            }
            else if (roll < 65)
            {
                Add(SoakPhaseKind.Spike, Secs(10, 30), SpikeConcurrency(), "spike");
                Add(SoakPhaseKind.Idle, Secs(3, 8), 0, "blink");
                Add(SoakPhaseKind.Spike, Secs(10, 30), SpikeConcurrency(), "spike");
            }
            else
            {
                Add(SoakPhaseKind.Spike, Secs(10, 45), SpikeConcurrency(), "spike");
                Add(SoakPhaseKind.Steady, Secs(30, 90), 4, "steady");
                Add(SoakPhaseKind.Idle, Secs(30, 180), 0, "idle");
            }
        }

        Add(SoakPhaseKind.Idle, finalIdle, 0, "final-idle");

        // Trim earlier phases so the run lasts about `total`, keeping the final idle intact.
        var excess = elapsed - total;
        var floor = TimeSpan.FromSeconds(1 * scale);
        for (var i = phases.Count - 2; i >= 0 && excess > TimeSpan.Zero; i--)
        {
            var room = phases[i].Duration - floor;
            var cut = excess < room ? excess : room;
            if (cut > TimeSpan.Zero)
            {
                phases[i] = phases[i] with { Duration = phases[i].Duration - cut };
                excess -= cut;
            }
        }

        return phases;
    }
}
