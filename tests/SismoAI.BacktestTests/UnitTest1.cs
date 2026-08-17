using SismoAI.Analytics;
using SismoAI.Domain;

namespace SismoAI.BacktestTests;

public sealed class BacktestSeriesTests
{
    [Fact]
    public void BuildBacktest_ReturnsScoresWithinExpectedRange()
    {
        var engine = new StatisticalAnalyticsEngine();
        var items = Enumerable.Range(1, 80)
            .Select(index => new EarthquakeEvent
            {
                Source = "test",
                SourceEventId = index.ToString(),
                OriginTimeUtc = DateTimeOffset.UtcNow.AddHours(-80 + index),
                ReceivedAtUtc = DateTimeOffset.UtcNow.AddHours(-80 + index),
                Latitude = -14 + (index * 0.01),
                Longitude = -75 + (index * 0.01),
                DepthKm = index % 2 == 0 ? 20 : 70,
                Magnitude = index % 10 == 0 ? 5.2 : 3.6,
                MagnitudeType = "Mw",
                LocationDescription = "Perú",
                Quality = "test",
                Status = "reviewed",
                RawPayload = "{}"
            })
            .ToList();

        var result = engine.BuildBacktest(items);

        Assert.NotEmpty(result);
        Assert.All(result, point => Assert.InRange(point.Value, 0, 100));
    }
}
