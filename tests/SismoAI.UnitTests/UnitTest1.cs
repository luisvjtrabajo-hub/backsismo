using SismoAI.Analytics;
using SismoAI.Domain;

namespace SismoAI.UnitTests;

public sealed class StatisticalAnalyticsEngineTests
{
    [Fact]
    public void Analyze_ReturnsAnomaly_WhenRecentWindowShowsBurst()
    {
        var engine = new StatisticalAnalyticsEngine();
        var baseline = Enumerable.Range(1, 36)
            .Select(index => CreateEvent(DateTimeOffset.UtcNow.AddHours(-48 + index), 2.9, 85))
            .ToList();
        var burst = Enumerable.Range(1, 16)
            .Select(index => CreateEvent(DateTimeOffset.UtcNow.AddMinutes(-(index * 20)), 5.1, 18))
            .ToList();

        var result = engine.Analyze(baseline.Concat(burst).ToList());

        Assert.True(result.AnomalyScore >= 40);
        Assert.NotEmpty(result.Drivers);
    }

    private static EarthquakeEvent CreateEvent(DateTimeOffset time, double magnitude, double depthKm)
    {
        return new EarthquakeEvent
        {
            Source = "test",
            SourceEventId = Guid.NewGuid().ToString("N"),
            OriginTimeUtc = time,
            ReceivedAtUtc = time,
            Latitude = -12,
            Longitude = -77,
            DepthKm = depthKm,
            Magnitude = magnitude,
            MagnitudeType = "Mw",
            LocationDescription = "Lima",
            Quality = "test",
            Status = "reviewed",
            RawPayload = "{}"
        };
    }
}
