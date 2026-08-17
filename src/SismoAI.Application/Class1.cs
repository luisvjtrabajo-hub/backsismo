using SismoAI.Domain;

namespace SismoAI.Application;

public sealed record ExternalEarthquakeDto(
    string Source,
    string SourceEventId,
    DateTimeOffset OriginTimeUtc,
    double Latitude,
    double Longitude,
    double DepthKm,
    double Magnitude,
    string MagnitudeType,
    string LocationDescription,
    string Quality,
    string Status,
    string RawPayload);

public sealed record NormalizedEarthquake(
    string Source,
    string SourceEventId,
    DateTimeOffset OriginTimeUtc,
    DateTimeOffset ReceivedAtUtc,
    double Latitude,
    double Longitude,
    double DepthKm,
    double Magnitude,
    string MagnitudeType,
    string LocationDescription,
    string Quality,
    string Status,
    string RawPayload);

public sealed record SourceStatusDto(
    string Name,
    bool IsAvailable,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastIngestedEventUtc,
    int ConsecutiveFailures,
    string LastError);

public sealed record ClusterDto(
    string Label,
    int EventCount,
    double AverageMagnitude,
    double AverageDepthKm,
    double Latitude,
    double Longitude);

public sealed record HistogramPointDto(string Label, int Count);

public sealed record TimelinePointDto(DateTimeOffset TimestampUtc, double Value);

public sealed record DashboardSnapshotDto(
    IReadOnlyList<EarthquakeEvent> CurrentEarthquakes,
    IReadOnlyList<TimelinePointDto> ActivitySeries,
    IReadOnlyList<TimelinePointDto> AnomalySeries,
    IReadOnlyList<HistogramPointDto> MagnitudeBands,
    IReadOnlyList<ClusterDto> Clusters,
    IReadOnlyList<ClusterDto> Swarms,
    IReadOnlyList<string> TopDrivers,
    IReadOnlyList<SourceStatusDto> Sources,
    IReadOnlyList<EarthquakeEvent> History,
    IReadOnlyList<TimelinePointDto> BacktestSeries,
    double CurrentAnomalyScore,
    string CurrentAnomalyLevel,
    string CurrentSummary,
    DateTimeOffset GeneratedAtUtc);

public sealed record AnalyticsResult(
    double AnomalyScore,
    string Level,
    string Summary,
    IReadOnlyList<string> Drivers);

public sealed class IngestionOptions
{
    public bool Enabled { get; set; } = true;
    public int PollingIntervalSeconds { get; set; } = 60;
    public int QueryLookbackHours { get; set; } = 24;
    public int RecentEventCount { get; set; } = 100;
}

public interface IEarthquakeDataSource
{
    string Name { get; }

    Task<IReadOnlyList<ExternalEarthquakeDto>> GetRecentEarthquakesAsync(
        DateTimeOffset? since,
        CancellationToken cancellationToken);
}

public interface IEarthquakeRepository
{
    Task<bool> ExistsAsync(string source, string sourceEventId, CancellationToken cancellationToken);
    Task AddAsync(EarthquakeEvent earthquakeEvent, CancellationToken cancellationToken);
    Task UpdateAnomalyScoreForRecentAsync(double anomalyScore, DateTimeOffset sinceUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<EarthquakeEvent>> GetRecentAsync(int count, CancellationToken cancellationToken);
    Task<IReadOnlyList<EarthquakeEvent>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<EarthquakeEvent>> GetBetweenAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
    Task<Dictionary<string, DateTimeOffset?>> GetLatestOriginBySourceAsync(CancellationToken cancellationToken);
}

public interface IMonitoringRepository
{
    Task<AnomalySnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AnomalySnapshot>> GetRecentSnapshotsAsync(int count, CancellationToken cancellationToken);
    Task SaveSnapshotAsync(AnomalySnapshot snapshot, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceSyncState>> GetSourceStatesAsync(CancellationToken cancellationToken);
    Task UpsertSourceStateAsync(SourceSyncState state, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IAnalyticsEngine
{
    AnalyticsResult Analyze(IReadOnlyList<EarthquakeEvent> recentEvents);
    IReadOnlyList<TimelinePointDto> BuildBacktest(IReadOnlyList<EarthquakeEvent> events);
}

public interface IDashboardService
{
    Task<DashboardSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken);
}

public interface IRealtimeNotifier
{
    Task PublishDashboardAsync(DashboardSnapshotDto snapshot, CancellationToken cancellationToken);
}
