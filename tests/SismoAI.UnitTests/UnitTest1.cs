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

    [Fact]
    public void Analyze_IncludesCountryInSummary_WhenFrequencyIsElevated()
    {
        var engine = new StatisticalAnalyticsEngine();
        var baseline = Enumerable.Range(1, 24)
            .Select(index => CreateEvent(DateTimeOffset.UtcNow.AddHours(-36 + index), 2.8, 70, "USGS", "California"))
            .ToList();
        var burst = Enumerable.Range(1, 18)
            .Select(index => CreateEvent(
                DateTimeOffset.UtcNow.AddMinutes(-(index * 15)),
                3.6,
                18,
                "IGP",
                "12 km al E de San Marcos, Huari - Áncash"))
            .ToList();

        var result = engine.Analyze(baseline.Concat(burst).ToList());

        Assert.Contains("Perú", result.Summary);
        Assert.Contains(result.Drivers, driver => driver.Contains("Perú", StringComparison.Ordinal));
    }

    private static EarthquakeEvent CreateEvent(
        DateTimeOffset time,
        double magnitude,
        double depthKm,
        string source = "test",
        string locationDescription = "Lima")
    {
        return new EarthquakeEvent
        {
            Source = source,
            SourceEventId = Guid.NewGuid().ToString("N"),
            OriginTimeUtc = time,
            ReceivedAtUtc = time,
            Latitude = -12,
            Longitude = -77,
            DepthKm = depthKm,
            Magnitude = magnitude,
            MagnitudeType = "Mw",
            LocationDescription = locationDescription,
            Quality = "test",
            Status = "reviewed",
            RawPayload = "{}"
        };
    }
}
