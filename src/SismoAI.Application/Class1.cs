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
    double CurrentPeruAnomalyScore,
    string CurrentPeruAnomalyLevel,
    string CurrentPeruSummary,
    IReadOnlyList<string> PeruTopDrivers,
    CountryBaselineClassificationDto PeruMachineLearning,
    IReadOnlyList<CountryBaselineClassificationDto> GlobalMachineLearning,
    DateTimeOffset GeneratedAtUtc);

public sealed record CountryDailyFeatureDto(
    string CountryCode,
    string CountryName,
    DateOnly Date,
    int EarthquakeCount,
    int SignificantEarthquakeCount,
    double MaxMagnitude,
    double MeanMagnitude,
    double MeanDepthKm,
    double TotalEnergyJoules,
    int EarthquakeCount1d,
    int SignificantEarthquakeCount1d,
    double MaxMagnitude1d,
    double TotalEnergyJoules1d,
    int EarthquakeCount7d,
    int EarthquakeCount30d,
    int SignificantEarthquakeCount7d,
    int SignificantEarthquakeCount30d,
    double MaxMagnitude7d,
    double MaxMagnitude30d,
    double MeanMagnitude7d,
    double MeanMagnitude30d,
    double TotalEnergyJoules7d,
    double TotalEnergyJoules30d,
    double BValue30d,
    double SignificantRate7d,
    double SignificantRate30d,
    double ActivityRatio7dTo30d,
    double SignificantActivityRatio7dTo30d,
    double EnergyRatio7dTo30d,
    double EtasRate1d,
    double OmoriPressure3d,
    double RecentEventDensity3d,
    double RecentSignificantDensity7d,
    double HoursSinceLastEvent,
    double HoursSinceLastSignificant,
    int DaysSinceLastSignificant,
    double? Temperature2mMean,
    double? Temperature2mMax,
    double? Temperature2mMin,
    double? PrecipitationSum,
    double? PressureMslMean,
    double? RelativeHumidity2mMean,
    double? WindSpeed10mMean,
    double? SoilMoisture0To10cmMean,
    double? ShortwaveRadiationSum,
    int GeomagneticSampleCount,
    double? GeomagneticRangeX,
    double? GeomagneticRangeY,
    double? GeomagneticRangeZ,
    double? GeomagneticRangeF,
    double? GeomagneticMeanAbsDeltaF,
    int NextDayEarthquakeCount,
    bool NextDayHadSignificantEarthquake);

public sealed record FeatureInfluenceDto(
    string Name,
    double Weight,
    string Direction);

public sealed record BaselineVariantDto(
    string Key,
    string Name,
    bool IsReady,
    string Summary,
    double Accuracy,
    double F1Score,
    double AreaUnderRocCurve,
    double AreaUnderPrecisionRecallCurve,
    double LatestProbability,
    bool LatestPrediction);

public sealed record CountryBaselineClassificationDto(
    string CountryCode,
    string CountryName,
    bool IsReady,
    string SelectedVariantKey,
    string ModelName,
    string Summary,
    int TotalSamples,
    int TrainingSamples,
    int TestSamples,
    double PositiveRate,
    double Accuracy,
    double F1Score,
    double AreaUnderRocCurve,
    double AreaUnderPrecisionRecallCurve,
    double LatestProbability,
    bool LatestPrediction,
    DateOnly? LatestFeatureDate,
    IReadOnlyList<BaselineVariantDto> Variants,
    IReadOnlyList<FeatureInfluenceDto> TopPositiveSignals,
    IReadOnlyList<FeatureInfluenceDto> TopNegativeSignals);

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
    public int HistoricalBackfillDays { get; set; } = 365;
    public int RecentEventCount { get; set; } = 100;
    public ClimateIngestionOptions Climate { get; set; } = new();
    public GeomagneticIngestionOptions Geomagnetic { get; set; } = new();
}

public sealed class ClimateIngestionOptions
{
    public bool Enabled { get; set; } = true;
    public int HistoryDays { get; set; } = 365;
    public int RefreshIntervalMinutes { get; set; } = 180;
    public string Latitude { get; set; } = "-9.19";
    public string Longitude { get; set; } = "-75.015";
    public string LocationLabel { get; set; } = "Perú";
    public string Models { get; set; } = "EC_Earth3P_HR,MRI_AGCM3_2_S";
    public List<ClimateLocationOption> Locations { get; set; } = [];
}

public sealed class ClimateLocationOption
{
    public string Label { get; set; } = string.Empty;
    public string Latitude { get; set; } = string.Empty;
    public string Longitude { get; set; } = string.Empty;
}

public sealed class GeomagneticIngestionOptions
{
    public bool Enabled { get; set; }
    public int HistoryDays { get; set; } = 30;
    public int SamplingPeriodSeconds { get; set; } = 60;
    public string DataType { get; set; } = "variation";
    public List<GeomagneticObservatoryOption> Observatories { get; set; } = [];
}

public sealed class GeomagneticObservatoryOption
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string CountryName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Elements { get; set; } = string.Empty;
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
    Task<Dictionary<string, DateTimeOffset?>> GetOldestOriginBySourceAsync(CancellationToken cancellationToken);
}

public interface IMonitoringRepository
{
    Task<AnomalySnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AnomalySnapshot>> GetRecentSnapshotsAsync(int count, CancellationToken cancellationToken);
    Task SaveSnapshotAsync(AnomalySnapshot snapshot, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceSyncState>> GetSourceStatesAsync(CancellationToken cancellationToken);
    Task RemoveMissingSourceStatesAsync(IReadOnlyCollection<string> activeSourceNames, CancellationToken cancellationToken);
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
    Task<IReadOnlyList<CountryDailyFeatureDto>> GetCountryDailyFeaturesAsync(string countryCode, int days, CancellationToken cancellationToken);
}

public interface IMachineLearningService
{
    Task<CountryBaselineClassificationDto> BuildCountryBaselineAsync(
        string countryCode,
        string countryName,
        IReadOnlyList<CountryDailyFeatureDto> features,
        CancellationToken cancellationToken);
}

public interface IRealtimeNotifier
{
    Task PublishDashboardAsync(DashboardSnapshotDto snapshot, CancellationToken cancellationToken);
}
