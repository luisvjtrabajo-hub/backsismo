using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SismoAI.Application;
using SismoAI.Domain;

namespace SismoAI.Infrastructure;

internal sealed record CountryDefinition(
    string Code,
    string Name,
    double MinLatitude,
    double MaxLatitude,
    double MinLongitude,
    double MaxLongitude,
    double ExtendedMinLatitude,
    double ExtendedMaxLatitude,
    double ExtendedMinLongitude,
    double ExtendedMaxLongitude,
    IReadOnlyList<string> CountryKeywords,
    IReadOnlyList<string> RegionKeywords,
    IReadOnlyList<string> ForeignKeywords,
    string? PreferredSource = null,
    string? ClimateLabel = null);

public sealed class SismoDbContext(DbContextOptions<SismoDbContext> options) : DbContext(options)
{
    public DbSet<EarthquakeEvent> EarthquakeEvents => Set<EarthquakeEvent>();
    public DbSet<AnomalySnapshot> AnomalySnapshots => Set<AnomalySnapshot>();
    public DbSet<SourceSyncState> SourceSyncStates => Set<SourceSyncState>();
    public DbSet<ClimateDailyObservation> ClimateDailyObservations => Set<ClimateDailyObservation>();
    public DbSet<GeomagneticObservation> GeomagneticObservations => Set<GeomagneticObservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EarthquakeEvent>(entity =>
        {
            entity.HasIndex(x => x.OriginTimeUtc);
            entity.HasIndex(x => x.Latitude);
            entity.HasIndex(x => x.Longitude);
            entity.HasIndex(x => x.DepthKm);
            entity.HasIndex(x => x.Magnitude);
            entity.HasIndex(x => x.Source);
            entity.HasIndex(x => x.SourceEventId);
            entity.HasIndex(x => new { x.Source, x.SourceEventId }).IsUnique();
            entity.Property(x => x.RawPayload).HasColumnType("text");
        });

        modelBuilder.Entity<AnomalySnapshot>(entity =>
        {
            entity.HasIndex(x => x.CapturedAtUtc);
            entity.Property(x => x.DriversJson).HasColumnType("text");
            entity.Property(x => x.Summary).HasColumnType("text");
        });

        modelBuilder.Entity<SourceSyncState>(entity =>
        {
            entity.HasIndex(x => x.SourceName).IsUnique();
            entity.Property(x => x.LastError).HasColumnType("text");
        });

        modelBuilder.Entity<ClimateDailyObservation>(entity =>
        {
            entity.HasIndex(x => x.ObservationDate);
            entity.HasIndex(x => x.Model);
            entity.HasIndex(x => new { x.Latitude, x.Longitude, x.ObservationDate });
            entity.HasIndex(x => new { x.Dataset, x.Model, x.Latitude, x.Longitude, x.ObservationDate }).IsUnique();
            entity.Property(x => x.RawPayload).HasColumnType("text");
        });

        modelBuilder.Entity<GeomagneticObservation>(entity =>
        {
            entity.HasIndex(x => x.ObservedAtUtc);
            entity.HasIndex(x => x.ObservatoryCode);
            entity.HasIndex(x => x.CountryCode);
            entity.HasIndex(x => new { x.CountryCode, x.ObservedAtUtc });
            entity.HasIndex(x => new { x.ObservatoryCode, x.ObservedAtUtc });
            entity.HasIndex(x => new { x.ObservatoryCode, x.ObservedAtUtc, x.SamplingPeriodSeconds, x.DataType }).IsUnique();
            entity.Property(x => x.RawPayload).HasColumnType("text");
        });
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddSismoInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IngestionOptions>(configuration.GetSection("Ingestion"));

        var postgres = ResolvePostgresConnectionString(configuration);
        var sqlite = ResolveSqliteConnectionString(configuration);

        services.AddDbContext<SismoDbContext>(options =>
        {
            if (!string.IsNullOrWhiteSpace(postgres))
            {
                options.UseNpgsql(postgres, npgsqlOptions =>
                {
                    npgsqlOptions.CommandTimeout(180);
                    npgsqlOptions.MaxBatchSize(50);
                });
            }
            else
            {
                options.UseSqlite(sqlite, sqliteOptions =>
                {
                    sqliteOptions.CommandTimeout(180);
                    sqliteOptions.MaxBatchSize(50);
                });
            }
        });

        services.AddScoped<IEarthquakeRepository, MonitoringRepository>();
        services.AddScoped<IMonitoringRepository, MonitoringRepository>();
        services.AddScoped<IDashboardService, DashboardService>();

        services.AddHttpClient<UsgsDataSource>(client =>
        {
            client.BaseAddress = new Uri("https://earthquake.usgs.gov/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<IgpDataSource>(client =>
        {
            client.BaseAddress = new Uri("https://ide.igp.gob.pe/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<IscDataSource>(client =>
        {
            client.BaseAddress = new Uri("https://www.isc.ac.uk/");
            client.Timeout = TimeSpan.FromSeconds(45);
        });

        services.AddHttpClient<OpenMeteoClimateDataSource>(client =>
        {
            client.BaseAddress = new Uri("https://climate-api.open-meteo.com/");
            client.Timeout = TimeSpan.FromSeconds(45);
        });

        services.AddHttpClient<UsgsGeomagnetismDataSource>(client =>
        {
            client.BaseAddress = new Uri("https://geomag.usgs.gov/ws/");
            client.Timeout = TimeSpan.FromSeconds(90);
        });

        services.AddScoped<IEarthquakeDataSource, UsgsDataSource>();
        services.AddScoped<IEarthquakeDataSource, IgpDataSource>();
        services.AddScoped<IEarthquakeDataSource, IscDataSource>();

        if (configuration.GetValue("Ingestion:Enabled", true))
        {
            services.AddHostedService<EarthquakeIngestionWorker>();
        }

        return services;
    }

    private static string? ResolvePostgresConnectionString(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("PostgreSql");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var renderDatabaseUrl = configuration["DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(renderDatabaseUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(renderDatabaseUrl, UriKind.Absolute, out var uri))
        {
            return renderDatabaseUrl;
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        var username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : string.Empty;
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var database = uri.AbsolutePath.Trim('/');
        var sslMode = GetQueryValue(uri.Query, "sslmode") ?? "Require";

        return $"Host={uri.Host};Port={uri.Port};Database={database};Username={username};Password={password};SSL Mode={sslMode};Trust Server Certificate=true";
    }

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var items = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var item in items)
        {
            var pair = item.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private static string ResolveSqliteConnectionString(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("Sqlite");
        var builder = string.IsNullOrWhiteSpace(configured)
            ? new SqliteConnectionStringBuilder()
            : new SqliteConnectionStringBuilder(configured);

        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            dataSource = Path.Combine("data", "sismoai.db");
        }

        var resolvedPath = Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.GetFullPath(dataSource, AppContext.BaseDirectory);

        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        builder.DataSource = resolvedPath;
        builder.Mode = SqliteOpenMode.ReadWriteCreate;

        return builder.ToString();
    }
}

public sealed class MonitoringRepository(SismoDbContext dbContext) : IEarthquakeRepository, IMonitoringRepository
{
    public async Task<bool> ExistsAsync(string source, string sourceEventId, CancellationToken cancellationToken)
    {
        return await dbContext.EarthquakeEvents.AnyAsync(
            x => x.Source == source && x.SourceEventId == sourceEventId,
            cancellationToken);
    }

    public async Task AddAsync(EarthquakeEvent earthquakeEvent, CancellationToken cancellationToken)
    {
        await dbContext.EarthquakeEvents.AddAsync(earthquakeEvent, cancellationToken);
    }

    public async Task UpdateAnomalyScoreForRecentAsync(double anomalyScore, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var events = await dbContext.EarthquakeEvents.ToListAsync(cancellationToken);
        events = events
            .Where(x => x.OriginTimeUtc >= sinceUtc)
            .ToList();

        foreach (var item in events)
        {
            item.AnomalyScore = anomalyScore;
            item.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public async Task<IReadOnlyList<EarthquakeEvent>> GetRecentAsync(int count, CancellationToken cancellationToken)
    {
        var items = await dbContext.EarthquakeEvents
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return items
            .OrderByDescending(x => x.OriginTimeUtc)
            .Take(count)
            .ToList();
    }

    public async Task<IReadOnlyList<EarthquakeEvent>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var items = await dbContext.EarthquakeEvents
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return items
            .Where(x => x.OriginTimeUtc >= sinceUtc)
            .OrderByDescending(x => x.OriginTimeUtc)
            .ToList();
    }

    public async Task<IReadOnlyList<EarthquakeEvent>> GetBetweenAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var items = await dbContext.EarthquakeEvents
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return items
            .Where(x => x.OriginTimeUtc >= fromUtc && x.OriginTimeUtc <= toUtc)
            .OrderBy(x => x.OriginTimeUtc)
            .ToList();
    }

    public async Task<Dictionary<string, DateTimeOffset?>> GetLatestOriginBySourceAsync(CancellationToken cancellationToken)
    {
        var items = await dbContext.EarthquakeEvents
            .AsNoTracking()
            .Select(x => new { x.Source, x.OriginTimeUtc })
            .ToListAsync(cancellationToken);

        return items
            .GroupBy(x => x.Source)
            .ToDictionary(
                group => group.Key,
                group => (DateTimeOffset?)group.Max(x => x.OriginTimeUtc));
    }

    public async Task<Dictionary<string, DateTimeOffset?>> GetOldestOriginBySourceAsync(CancellationToken cancellationToken)
    {
        var items = await dbContext.EarthquakeEvents
            .AsNoTracking()
            .Select(x => new { x.Source, x.OriginTimeUtc })
            .ToListAsync(cancellationToken);

        return items
            .GroupBy(x => x.Source)
            .ToDictionary(
                group => group.Key,
                group => (DateTimeOffset?)group.Min(x => x.OriginTimeUtc));
    }

    public async Task<AnomalySnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        var items = await dbContext.AnomalySnapshots
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return items
            .OrderByDescending(x => x.CapturedAtUtc)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<AnomalySnapshot>> GetRecentSnapshotsAsync(int count, CancellationToken cancellationToken)
    {
        var items = await dbContext.AnomalySnapshots
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return items
            .OrderByDescending(x => x.CapturedAtUtc)
            .Take(count)
            .ToList();
    }

    public async Task SaveSnapshotAsync(AnomalySnapshot snapshot, CancellationToken cancellationToken)
    {
        await dbContext.AnomalySnapshots.AddAsync(snapshot, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceSyncState>> GetSourceStatesAsync(CancellationToken cancellationToken)
    {
        return await dbContext.SourceSyncStates
            .OrderBy(x => x.SourceName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task RemoveMissingSourceStatesAsync(IReadOnlyCollection<string> activeSourceNames, CancellationToken cancellationToken)
    {
        var active = new HashSet<string>(activeSourceNames, StringComparer.OrdinalIgnoreCase);
        var staleStates = await dbContext.SourceSyncStates
            .Where(x => !active.Contains(x.SourceName))
            .ToListAsync(cancellationToken);

        if (staleStates.Count == 0)
        {
            return;
        }

        dbContext.SourceSyncStates.RemoveRange(staleStates);
    }

    public async Task UpsertSourceStateAsync(SourceSyncState state, CancellationToken cancellationToken)
    {
        var existing = await dbContext.SourceSyncStates
            .FirstOrDefaultAsync(x => x.SourceName == state.SourceName, cancellationToken);

        if (existing is null)
        {
            await dbContext.SourceSyncStates.AddAsync(state, cancellationToken);
            return;
        }

        existing.IsAvailable = state.IsAvailable;
        existing.LastAttemptUtc = state.LastAttemptUtc;
        existing.LastSuccessUtc = state.LastSuccessUtc;
        existing.LastIngestedEventUtc = state.LastIngestedEventUtc;
        existing.ConsecutiveFailures = state.ConsecutiveFailures;
        existing.LastError = state.LastError;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}

public sealed class DashboardService(
    IEarthquakeRepository earthquakeRepository,
    IMonitoringRepository monitoringRepository,
    IAnalyticsEngine analyticsEngine,
    IMachineLearningService machineLearningService,
    SismoDbContext dbContext) : IDashboardService
{
    private static readonly CountryDefinition PeruDefinition = new(
        "PE",
        "Perú",
        -18.6, 0.8, -81.6, -68.2,
        -21, 2, -84, -66,
        ["perú", "peru"],
        ["lima", "ica", "arequipa", "ancash", "áncash", "huari", "cañete", "nasca", "piura", "trujillo", "cusco", "cuzco", "tacna", "moquegua", "puno", "ayacucho", "apurímac", "apurimac", "junín", "junin", "amazonas", "san martín", "san martin", "ucayali", "madre de dios", "huánuco", "huanuco", "pasco", "chiclayo", "chimbote", "callao", "cajamarca", "huancayo", "pucallpa", "tarapoto"],
        ["chile", "ecuador", "colombia", "bolivia", "brasil", "brazil", "argentina", "venezuela", "jamaica", "costa rica", "mexico", "méxico", "guatemala", "nicaragua", "panama", "panamá", "el salvador", "puerto rico", "united states", "usa", "canada", "canadá"],
        "IGP",
        "Perú");
    private static readonly CountryDefinition UnitedStatesDefinition = new(
        "US",
        "Estados Unidos",
        18.5, 71.5, -179.9, -66.5,
        15, 72, -180, -60,
        ["united states", "usa", "alaska", "california", "hawaii", "nevada", "puerto rico"],
        ["alaska", "california", "hawaii", "nevada", "utah", "washington", "montana", "idaho", "wyoming", "puerto rico"],
        ["mexico", "méxico", "canada", "canadá", "japan", "japon", "chile", "peru", "perú"],
        null,
        "Estados Unidos");
    private static readonly CountryDefinition JapanDefinition = new(
        "JP",
        "Japón",
        24, 46.5, 122, 146.5,
        20, 48, 120, 150,
        ["japan", "japón", "honshu", "hokkaido", "kyushu", "tokyo"],
        ["honshu", "hokkaido", "kyushu", "tokyo", "osaka", "sendai", "fukushima", "okinawa"],
        ["russia", "china", "taiwan", "philippines", "corea", "korea", "alaska"],
        null,
        "Japón");
    private static readonly CountryDefinition ChileDefinition = new(
        "CL",
        "Chile",
        -56, -17, -76, -66,
        -57, -16, -78, -64,
        ["chile", "santiago", "antofagasta", "valparaiso", "concepcion"],
        ["santiago", "antofagasta", "valparaiso", "concepcion", "atacama", "iquique", "coquimbo"],
        ["peru", "perú", "bolivia", "argentina", "ecuador"],
        null,
        "Chile");
    private static readonly CountryDefinition[] MachineLearningCountries =
    [
        PeruDefinition,
        UnitedStatesDefinition,
        JapanDefinition,
        ChileDefinition
    ];

    public async Task<DashboardSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var recent = await earthquakeRepository.GetSinceAsync(now.AddDays(-7), cancellationToken);
        var latestEvents = recent.Take(50).ToList();
        var latestSnapshot = await monitoringRepository.GetLatestSnapshotAsync(cancellationToken);
        var sources = (await monitoringRepository.GetSourceStatesAsync(cancellationToken))
            .Select(x => new SourceStatusDto(
                x.SourceName,
                x.IsAvailable,
                x.LastAttemptUtc,
                x.LastSuccessUtc,
                x.LastIngestedEventUtc,
                x.ConsecutiveFailures,
                x.LastError))
            .ToList();

        var anomalySeries = (await monitoringRepository.GetRecentSnapshotsAsync(48, cancellationToken))
            .OrderBy(x => x.CapturedAtUtc)
            .Select(x => new TimelinePointDto(x.CapturedAtUtc, x.Score))
            .ToList();

        var activitySeries = recent
            .Where(x => x.OriginTimeUtc >= now.AddHours(-24))
            .GroupBy(x => new DateTimeOffset(x.OriginTimeUtc.UtcDateTime.Year, x.OriginTimeUtc.UtcDateTime.Month, x.OriginTimeUtc.UtcDateTime.Day, x.OriginTimeUtc.UtcDateTime.Hour, 0, 0, TimeSpan.Zero))
            .OrderBy(x => x.Key)
            .Select(x => new TimelinePointDto(x.Key, x.Count()))
            .ToList();

        var magnitudeBands = new[]
        {
            new HistogramPointDto("M<3", recent.Count(x => x.Magnitude < 3)),
            new HistogramPointDto("3-4", recent.Count(x => x.Magnitude >= 3 && x.Magnitude < 4)),
            new HistogramPointDto("4-5", recent.Count(x => x.Magnitude >= 4 && x.Magnitude < 5)),
            new HistogramPointDto("5-6", recent.Count(x => x.Magnitude >= 5 && x.Magnitude < 6)),
            new HistogramPointDto("6+", recent.Count(x => x.Magnitude >= 6))
        };

        var clusters = BuildGeoClusters(recent, 0.75, "Cluster");
        var swarms = BuildGeoClusters(
            recent.Where(x => x.DepthKm <= 70 && x.OriginTimeUtc >= now.AddDays(-3)).ToList(),
            0.45,
            "Enjambre");

        var backtestSeries = analyticsEngine.BuildBacktest(recent.OrderBy(x => x.OriginTimeUtc).ToList());
        var analytics = latestSnapshot is null
            ? analyticsEngine.Analyze(latestEvents)
            : new AnalyticsResult(
                latestSnapshot.Score,
                latestSnapshot.Level,
                latestSnapshot.Summary,
                JsonSerializer.Deserialize<List<string>>(latestSnapshot.DriversJson) ?? []);
        var peruRecent = recent.Where(x => IsCountryEvent(x, PeruDefinition)).ToList();
        var peruAnalytics = analyticsEngine.Analyze(peruRecent.Take(50).ToList());
            var peruDailyFeatures = await GetCountryDailyFeaturesAsync(PeruDefinition.Code, 3650, cancellationToken);
        var peruMachineLearning = await machineLearningService.BuildCountryBaselineAsync(
            PeruDefinition.Code,
            PeruDefinition.Name,
            peruDailyFeatures,
            cancellationToken);
        var globalMachineLearning = new List<CountryBaselineClassificationDto>(MachineLearningCountries.Length);
        foreach (var country in MachineLearningCountries)
        {
            var features = country.Code == PeruDefinition.Code
                ? peruDailyFeatures
                : await GetCountryDailyFeaturesAsync(country.Code, 3650, cancellationToken);
            var baseline = country.Code == PeruDefinition.Code
                ? peruMachineLearning
                : await machineLearningService.BuildCountryBaselineAsync(country.Code, country.Name, features, cancellationToken);
            globalMachineLearning.Add(baseline);
        }

        return new DashboardSnapshotDto(
            latestEvents,
            activitySeries,
            anomalySeries,
            magnitudeBands,
            clusters,
            swarms,
            analytics.Drivers,
            sources,
            recent.Take(200).ToList(),
            backtestSeries,
            analytics.AnomalyScore,
            analytics.Level,
            analytics.Summary,
            peruAnalytics.AnomalyScore,
            peruAnalytics.Level,
            peruAnalytics.Summary,
            peruAnalytics.Drivers,
            peruMachineLearning,
            globalMachineLearning,
            now);
    }

    public async Task<IReadOnlyList<CountryDailyFeatureDto>> GetCountryDailyFeaturesAsync(string countryCode, int days, CancellationToken cancellationToken)
    {
        var country = MachineLearningCountries.FirstOrDefault(x => string.Equals(x.Code, countryCode, StringComparison.OrdinalIgnoreCase))
            ?? PeruDefinition;
            var normalizedDays = Math.Clamp(days, 30, 3650);
        var sinceUtc = DateTimeOffset.UtcNow.AddDays(-normalizedDays);
        var earthquakes = (await earthquakeRepository.GetSinceAsync(sinceUtc, cancellationToken))
            .Where(x => IsCountryEvent(x, country))
            .ToList();
        var climate = await dbContext.ClimateDailyObservations
            .AsNoTracking()
            .Where(x => x.LocationLabel == (country.ClimateLabel ?? country.Name)
                && x.ObservationDate >= DateOnly.FromDateTime(sinceUtc.UtcDateTime.Date))
            .OrderBy(x => x.ObservationDate)
            .ToListAsync(cancellationToken);
        var geomagnetic = await dbContext.GeomagneticObservations
            .AsNoTracking()
            .Where(x => x.CountryCode == country.Code
                && x.ObservedAtUtc >= sinceUtc)
            .OrderBy(x => x.ObservedAtUtc)
            .ToListAsync(cancellationToken);

        var climateByDate = climate
            .GroupBy(x => x.ObservationDate)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(x => x.Model == "EC_Earth3P_HR")
                    .ThenBy(x => x.Model)
                    .First());

        var earthquakeByDate = earthquakes
            .GroupBy(x => DateOnly.FromDateTime(x.OriginTimeUtc.UtcDateTime.Date))
            .ToDictionary(group => group.Key, group => group.ToList());
        var geomagneticByDate = geomagnetic
            .GroupBy(x => DateOnly.FromDateTime(x.ObservedAtUtc.UtcDateTime.Date))
            .ToDictionary(group => group.Key, group => group.ToList());

        var availableDates = climateByDate.Keys
            .Concat(earthquakeByDate.Keys)
            .Concat(geomagneticByDate.Keys)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        if (availableDates.Count == 0)
        {
            return [];
        }

        var startDate = availableDates[0];
        var endDate = availableDates[^1];
        var allDates = new List<DateOnly>();
        for (var cursor = startDate; cursor <= endDate; cursor = cursor.AddDays(1))
        {
            allDates.Add(cursor);
        }

        var result = new List<CountryDailyFeatureDto>(allDates.Count);
        foreach (var date in allDates)
        {
            earthquakeByDate.TryGetValue(date, out var dayEarthquakes);
            climateByDate.TryGetValue(date, out var dayClimate);
            geomagneticByDate.TryGetValue(date, out var dayGeomagnetic);
            var events = dayEarthquakes ?? [];
            var geomagneticItems = dayGeomagnetic ?? [];
            var window1Events = CollectWindowEvents(earthquakeByDate, date, 1);
            var window7Events = CollectWindowEvents(earthquakeByDate, date, 7);
            var window30Events = CollectWindowEvents(earthquakeByDate, date, 30);
            var window3Events = CollectWindowEvents(earthquakeByDate, date, 3);
            var significantCount1d = window1Events.Count(x => x.Magnitude >= 4.5);
            var significantCount7d = window7Events.Count(x => x.Magnitude >= 4.5);
            var significantCount30d = window30Events.Count(x => x.Magnitude >= 4.5);
            var totalEnergy1d = Math.Round(window1Events.Sum(x => x.ApproximateEnergyJoules), 3);
            var totalEnergy7d = Math.Round(window7Events.Sum(x => x.ApproximateEnergyJoules), 3);
            var totalEnergy30d = Math.Round(window30Events.Sum(x => x.ApproximateEnergyJoules), 3);
            var activityRatio7dTo30d = ComputeWindowRatio(window7Events.Count / 7d, window30Events.Count / 30d);
            var significantActivityRatio7dTo30d = ComputeWindowRatio(significantCount7d / 7d, significantCount30d / 30d);
            var energyRatio7dTo30d = ComputeWindowRatio(totalEnergy7d / 7d, totalEnergy30d / 30d);
            var etasRate1d = ComputeEtasLiteRate(window1Events, date);
            var omoriPressure3d = ComputeOmoriPressure(window3Events, date);
            var recentEventDensity3d = Math.Round(window3Events.Count / 3d, 4);
            var recentSignificantDensity7d = Math.Round(significantCount7d / 7d, 4);
            var hoursSinceLastEvent = ComputeHoursSinceLastEvent(earthquakeByDate, date);
            var hoursSinceLastSignificant = ComputeHoursSinceLastSignificant(earthquakeByDate, date);
            var nextDate = date.AddDays(1);
            earthquakeByDate.TryGetValue(nextDate, out var nextDayEarthquakes);
            var nextEvents = nextDayEarthquakes ?? [];

            result.Add(new CountryDailyFeatureDto(
                country.Code,
                country.Name,
                date,
                events.Count,
                events.Count(x => x.Magnitude >= 4.5),
                events.Count == 0 ? 0 : events.Max(x => x.Magnitude),
                events.Count == 0 ? 0 : Math.Round(events.Average(x => x.Magnitude), 3),
                events.Count == 0 ? 0 : Math.Round(events.Average(x => x.DepthKm), 3),
                Math.Round(events.Sum(x => x.ApproximateEnergyJoules), 3),
                window1Events.Count,
                significantCount1d,
                window1Events.Count == 0 ? 0 : window1Events.Max(x => x.Magnitude),
                totalEnergy1d,
                window7Events.Count,
                window30Events.Count,
                significantCount7d,
                significantCount30d,
                window7Events.Count == 0 ? 0 : window7Events.Max(x => x.Magnitude),
                window30Events.Count == 0 ? 0 : window30Events.Max(x => x.Magnitude),
                window7Events.Count == 0 ? 0 : Math.Round(window7Events.Average(x => x.Magnitude), 3),
                window30Events.Count == 0 ? 0 : Math.Round(window30Events.Average(x => x.Magnitude), 3),
                totalEnergy7d,
                totalEnergy30d,
                ComputeBValue(window30Events),
                Math.Round(significantCount7d / 7d, 4),
                Math.Round(significantCount30d / 30d, 4),
                activityRatio7dTo30d,
                significantActivityRatio7dTo30d,
                energyRatio7dTo30d,
                etasRate1d,
                omoriPressure3d,
                recentEventDensity3d,
                recentSignificantDensity7d,
                hoursSinceLastEvent,
                hoursSinceLastSignificant,
                ComputeDaysSinceLastSignificant(earthquakeByDate, date),
                dayClimate?.Temperature2mMean,
                dayClimate?.Temperature2mMax,
                dayClimate?.Temperature2mMin,
                dayClimate?.PrecipitationSum,
                dayClimate?.PressureMslMean,
                dayClimate?.RelativeHumidity2mMean,
                dayClimate?.WindSpeed10mMean,
                dayClimate?.SoilMoisture0To10cmMean,
                dayClimate?.ShortwaveRadiationSum,
                geomagneticItems.Count,
                ComputeRange(geomagneticItems.Select(x => x.X)),
                ComputeRange(geomagneticItems.Select(x => x.Y)),
                ComputeRange(geomagneticItems.Select(x => x.Z)),
                ComputeRange(geomagneticItems.Select(x => x.F)),
                ComputeMeanAbsoluteDelta(geomagneticItems.Select(x => x.F)),
                nextEvents.Count,
                nextEvents.Count > 0,
                nextEvents.Any(x => x.Magnitude >= 4.0),
                nextEvents.Any(x => x.Magnitude >= 4.5)));
        }

        return result
            .OrderByDescending(x => x.Date)
            .ToList();
    }

    private static double ComputeEtasLiteRate(IReadOnlyList<EarthquakeEvent> events, DateOnly date)
    {
        if (events.Count == 0)
        {
            return 0;
        }

        var endOfDayUtc = date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
        const double productivityBase = 0.8;
        const double magnitudeScale = 1.1;
        const double timeOffsetDays = 0.05;
        const double decay = 1.1;

        var score = events.Sum(x =>
        {
            var elapsedDays = Math.Max(timeOffsetDays, (endOfDayUtc - x.OriginTimeUtc.UtcDateTime).TotalDays);
            var productivity = Math.Exp(magnitudeScale * Math.Max(0, x.Magnitude - 3d));
            return productivityBase * productivity / Math.Pow(elapsedDays + timeOffsetDays, decay);
        });

        return Math.Round(Math.Log10(score + 1), 6);
    }

    private static double ComputeOmoriPressure(IReadOnlyList<EarthquakeEvent> events, DateOnly date)
    {
        if (events.Count == 0)
        {
            return 0;
        }

        var endOfDayUtc = date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
        const double timeOffsetDays = 0.03;
        const double decay = 1.0;

        var score = events.Sum(x =>
        {
            var elapsedDays = Math.Max(timeOffsetDays, (endOfDayUtc - x.OriginTimeUtc.UtcDateTime).TotalDays);
            return Math.Max(0, x.Magnitude - 2.5d) / Math.Pow(elapsedDays + timeOffsetDays, decay);
        });

        return Math.Round(Math.Log10(score + 1), 6);
    }

    private static List<EarthquakeEvent> CollectWindowEvents(
        IReadOnlyDictionary<DateOnly, List<EarthquakeEvent>> earthquakeByDate,
        DateOnly date,
        int windowDays)
    {
        var collected = new List<EarthquakeEvent>();
        for (var offset = windowDays - 1; offset >= 0; offset--)
        {
            if (earthquakeByDate.TryGetValue(date.AddDays(-offset), out var items))
            {
                collected.AddRange(items);
            }
        }

        return collected;
    }

    private static double ComputeHoursSinceLastEvent(
        IReadOnlyDictionary<DateOnly, List<EarthquakeEvent>> earthquakeByDate,
        DateOnly date)
    {
        var endOfDayUtc = date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        for (var offset = 0; offset <= 3650; offset++)
        {
            var candidateDate = date.AddDays(-offset);
            if (earthquakeByDate.TryGetValue(candidateDate, out var items) && items.Count > 0)
            {
                var latest = items.Max(x => x.OriginTimeUtc.UtcDateTime);
                return Math.Round(Math.Max(0, (endOfDayUtc - latest).TotalHours), 4);
            }
        }

        return 3651 * 24;
    }

    private static double ComputeHoursSinceLastSignificant(
        IReadOnlyDictionary<DateOnly, List<EarthquakeEvent>> earthquakeByDate,
        DateOnly date)
    {
        var endOfDayUtc = date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        for (var offset = 0; offset <= 3650; offset++)
        {
            var candidateDate = date.AddDays(-offset);
            if (earthquakeByDate.TryGetValue(candidateDate, out var items))
            {
                var latest = items
                    .Where(x => x.Magnitude >= 4.5)
                    .Select(x => x.OriginTimeUtc.UtcDateTime)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();

                if (latest != DateTime.MinValue)
                {
                    return Math.Round(Math.Max(0, (endOfDayUtc - latest).TotalHours), 4);
                }
            }
        }

        return 3651 * 24;
    }

    private static int ComputeDaysSinceLastSignificant(
        IReadOnlyDictionary<DateOnly, List<EarthquakeEvent>> earthquakeByDate,
        DateOnly date)
    {
        for (var offset = 0; offset <= 3650; offset++)
        {
            var candidateDate = date.AddDays(-offset);
            if (earthquakeByDate.TryGetValue(candidateDate, out var items)
                && items.Any(x => x.Magnitude >= 4.5))
            {
                return offset;
            }
        }

        return 3651;
    }

    private static double ComputeWindowRatio(double shortWindowRate, double longWindowRate)
    {
        if (longWindowRate <= 0)
        {
            return shortWindowRate <= 0 ? 1 : Math.Round(shortWindowRate + 1, 4);
        }

        return Math.Round(shortWindowRate / longWindowRate, 4);
    }

    private static double ComputeBValue(IReadOnlyList<EarthquakeEvent> events)
    {
        if (events.Count < 25)
        {
            return 0;
        }

        var magnitudes = events
            .Select(x => x.Magnitude)
            .Where(x => x > 0)
            .OrderBy(x => x)
            .ToList();

        if (magnitudes.Count < 25)
        {
            return 0;
        }

        const double magnitudeBin = 0.1;
        var completenessMagnitude = magnitudes.Min();
        var denominator = magnitudes.Average() - (completenessMagnitude - (magnitudeBin / 2d));
        if (denominator <= 0)
        {
            return 0;
        }

        return Math.Round(Math.Log10(Math.E) / denominator, 4);
    }

    private static double? ComputeRange(IEnumerable<double?> values)
    {
        var materialized = values
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();

        if (materialized.Count < 2)
        {
            return null;
        }

        return Math.Round(materialized.Max() - materialized.Min(), 6);
    }

    private static double? ComputeMeanAbsoluteDelta(IEnumerable<double?> values)
    {
        var materialized = values
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();

        if (materialized.Count < 2)
        {
            return null;
        }

        var deltas = new List<double>(materialized.Count - 1);
        for (var index = 1; index < materialized.Count; index++)
        {
            deltas.Add(Math.Abs(materialized[index] - materialized[index - 1]));
        }

        return deltas.Count == 0 ? null : Math.Round(deltas.Average(), 6);
    }

    private static IReadOnlyList<ClusterDto> BuildGeoClusters(IReadOnlyList<EarthquakeEvent> events, double bucketSize, string prefix)
    {
        return events
            .GroupBy(x => (
                Lat: Math.Round(x.Latitude / bucketSize) * bucketSize,
                Lon: Math.Round(x.Longitude / bucketSize) * bucketSize))
            .Where(x => x.Count() >= 3)
            .OrderByDescending(x => x.Count())
            .Take(12)
            .Select((group, index) => new ClusterDto(
                $"{prefix} {index + 1}",
                group.Count(),
                Math.Round(group.Average(x => x.Magnitude), 2),
                Math.Round(group.Average(x => x.DepthKm), 2),
                Math.Round(group.Average(x => x.Latitude), 4),
                Math.Round(group.Average(x => x.Longitude), 4)))
            .ToList();
    }

    private static bool IsCountryEvent(EarthquakeEvent earthquakeEvent, CountryDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.PreferredSource)
            && string.Equals(earthquakeEvent.Source, definition.PreferredSource, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (earthquakeEvent.Latitude >= definition.MinLatitude
            && earthquakeEvent.Latitude <= definition.MaxLatitude
            && earthquakeEvent.Longitude >= definition.MinLongitude
            && earthquakeEvent.Longitude <= definition.MaxLongitude)
        {
            return true;
        }

        var location = NormalizeForKeywordMatch(earthquakeEvent.LocationDescription);
        if (HasKeywordPhrase(location, definition.CountryKeywords))
        {
            return true;
        }

        if (HasKeywordPhrase(location, definition.ForeignKeywords))
        {
            return false;
        }

        var isNearCountryBounds = earthquakeEvent.Latitude >= definition.ExtendedMinLatitude
            && earthquakeEvent.Latitude <= definition.ExtendedMaxLatitude
            && earthquakeEvent.Longitude >= definition.ExtendedMinLongitude
            && earthquakeEvent.Longitude <= definition.ExtendedMaxLongitude;

        return isNearCountryBounds && HasKeywordPhrase(location, definition.RegionKeywords);
    }

    private static string NormalizeForKeywordMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return " ";
        }

        var normalized = value
            .Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .ToArray();

        var text = new string(normalized)
            .ToLowerInvariant();
        var cleaned = new string(text.Select(ch => char.IsLetter(ch) ? ch : ' ').ToArray());
        var compact = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return string.IsNullOrWhiteSpace(compact) ? " " : $" {compact} ";
    }

    private static bool HasKeywordPhrase(string normalizedText, IReadOnlyList<string> phrases)
    {
        foreach (var phrase in phrases)
        {
            var normalizedPhrase = NormalizeForKeywordMatch(phrase).Trim();
            if (!string.IsNullOrWhiteSpace(normalizedPhrase) && normalizedText.Contains($" {normalizedPhrase} ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class EarthquakeIngestionWorker(
    IServiceScopeFactory scopeFactory,
    IAnalyticsEngine analyticsEngine,
    IRealtimeNotifier realtimeNotifier,
    IOptions<IngestionOptions> options,
    ILogger<EarthquakeIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Falló el ciclo de ingestión sísmica.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, options.Value.PollingIntervalSeconds)), stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var earthquakeRepository = scope.ServiceProvider.GetRequiredService<IEarthquakeRepository>();
        var monitoringRepository = scope.ServiceProvider.GetRequiredService<IMonitoringRepository>();
        var dashboardService = scope.ServiceProvider.GetRequiredService<IDashboardService>();
        var dataSources = scope.ServiceProvider.GetRequiredService<IEnumerable<IEarthquakeDataSource>>();
            var climateDataSource = scope.ServiceProvider.GetRequiredService<OpenMeteoClimateDataSource>();
            var geomagnetismDataSource = scope.ServiceProvider.GetRequiredService<UsgsGeomagnetismDataSource>();
        var activeSourceNames = dataSources.Select(x => x.Name).ToArray();

        var latestBySource = await earthquakeRepository.GetLatestOriginBySourceAsync(cancellationToken);
        var oldestBySource = await earthquakeRepository.GetOldestOriginBySourceAsync(cancellationToken);
            var historicalBackfillDays = Math.Max(1, options.Value.HistoricalBackfillDays);
            var lookbackSince = DateTimeOffset.UtcNow.AddDays(-historicalBackfillDays);
            var incrementalSince = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, options.Value.QueryLookbackHours));
        await monitoringRepository.RemoveMissingSourceStatesAsync(activeSourceNames, cancellationToken);
        await monitoringRepository.SaveChangesAsync(cancellationToken);

        foreach (var source in dataSources)
        {
            var lastKnown = latestBySource.TryGetValue(source.Name, out var value) ? value : null;
            var oldestKnown = oldestBySource.TryGetValue(source.Name, out var oldestValue) ? oldestValue : null;
            var targetOldest = DateTimeOffset.UtcNow.AddDays(-historicalBackfillDays);
            var requiresHistoricalCompletion = oldestKnown is null || oldestKnown > targetOldest;
            var since = lastKnown?.AddMinutes(-5) ?? incrementalSince;

            if (requiresHistoricalCompletion)
            {
                since = oldestKnown?.AddMinutes(-5) ?? lookbackSince;
                if (since > targetOldest)
                {
                    since = targetOldest;
                }
            }

            var state = new SourceSyncState
            {
                SourceName = source.Name,
                LastAttemptUtc = DateTimeOffset.UtcNow
            };

            try
            {
                var items = await source.GetRecentEarthquakesAsync(since, cancellationToken);
                var inserted = 0;
                var skippedDuplicatesInBatch = 0;
                var skippedAlreadyPersisted = 0;
                DateTimeOffset? newestOrigin = lastKnown;
                var pendingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in items.OrderBy(x => x.OriginTimeUtc))
                {
                    var dedupeKey = $"{item.Source}::{item.SourceEventId}";
                    if (!pendingKeys.Add(dedupeKey))
                    {
                        skippedDuplicatesInBatch++;
                        continue;
                    }

                    if (await earthquakeRepository.ExistsAsync(item.Source, item.SourceEventId, cancellationToken))
                    {
                        skippedAlreadyPersisted++;
                        continue;
                    }

                    var entity = new EarthquakeEvent
                    {
                        Source = item.Source,
                        SourceEventId = item.SourceEventId,
                        OriginTimeUtc = item.OriginTimeUtc,
                        ReceivedAtUtc = DateTimeOffset.UtcNow,
                        Latitude = item.Latitude,
                        Longitude = item.Longitude,
                        DepthKm = item.DepthKm,
                        Magnitude = item.Magnitude,
                        MagnitudeType = item.MagnitudeType,
                        LocationDescription = item.LocationDescription,
                        Quality = item.Quality,
                        Status = item.Status,
                        RawPayload = item.RawPayload,
                        ApproximateEnergyJoules = ApproximateEnergy(item.Magnitude)
                    };

                    await earthquakeRepository.AddAsync(entity, cancellationToken);
                    inserted++;
                    newestOrigin = newestOrigin is null || item.OriginTimeUtc > newestOrigin ? item.OriginTimeUtc : newestOrigin;
                }

                state.IsAvailable = true;
                state.LastSuccessUtc = DateTimeOffset.UtcNow;
                state.LastIngestedEventUtc = newestOrigin;
                state.ConsecutiveFailures = 0;
                state.LastError = BuildIngestionStatusMessage(inserted, skippedAlreadyPersisted, skippedDuplicatesInBatch);
                await monitoringRepository.UpsertSourceStateAsync(state, cancellationToken);
                await monitoringRepository.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Fuente {SourceName}: {Inserted} insertados, {SkippedPersisted} ya existentes, {SkippedBatch} duplicados en lote.",
                    source.Name,
                    inserted,
                    skippedAlreadyPersisted,
                    skippedDuplicatesInBatch);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "La fuente {SourceName} no pudo ser ingerida.", source.Name);
                var previous = (await monitoringRepository.GetSourceStatesAsync(cancellationToken))
                    .FirstOrDefault(x => x.SourceName == source.Name);

                state.IsAvailable = false;
                state.LastSuccessUtc = previous?.LastSuccessUtc;
                state.LastIngestedEventUtc = previous?.LastIngestedEventUtc;
                state.ConsecutiveFailures = (previous?.ConsecutiveFailures ?? 0) + 1;
                state.LastError = exception.Message;
                await monitoringRepository.UpsertSourceStateAsync(state, cancellationToken);
                await monitoringRepository.SaveChangesAsync(cancellationToken);
            }
        }

        if (options.Value.Climate.Enabled)
        {
            await IngestClimateAsync(scope.ServiceProvider.GetRequiredService<SismoDbContext>(), climateDataSource, cancellationToken);
        }

            if (options.Value.Geomagnetic.Enabled)
            {
                await IngestGeomagneticAsync(scope.ServiceProvider.GetRequiredService<SismoDbContext>(), geomagnetismDataSource, cancellationToken);
            }

        var recent = await earthquakeRepository.GetRecentAsync(options.Value.RecentEventCount, cancellationToken);
        var analytics = analyticsEngine.Analyze(recent);

        await monitoringRepository.SaveSnapshotAsync(new AnomalySnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Score = analytics.AnomalyScore,
            Level = analytics.Level,
            Summary = analytics.Summary,
            DriversJson = JsonSerializer.Serialize(analytics.Drivers)
        }, cancellationToken);

        await earthquakeRepository.UpdateAnomalyScoreForRecentAsync(analytics.AnomalyScore, DateTimeOffset.UtcNow.AddHours(-24), cancellationToken);
        await monitoringRepository.SaveChangesAsync(cancellationToken);

        var snapshot = await dashboardService.GetSnapshotAsync(cancellationToken);
        await realtimeNotifier.PublishDashboardAsync(snapshot, cancellationToken);
    }

    private static double ApproximateEnergy(double magnitude)
    {
        return Math.Pow(10, 1.5 * magnitude + 4.8);
    }

    private static string BuildIngestionStatusMessage(int inserted, int skippedAlreadyPersisted, int skippedDuplicatesInBatch)
    {
        if (inserted == 0 && skippedAlreadyPersisted == 0 && skippedDuplicatesInBatch == 0)
        {
            return "Sin eventos nuevos en el ciclo actual.";
        }

        var details = new List<string>();
        if (inserted > 0)
        {
            details.Add($"{inserted} nuevos");
        }

        if (skippedAlreadyPersisted > 0)
        {
            details.Add($"{skippedAlreadyPersisted} ya existentes");
        }

        if (skippedDuplicatesInBatch > 0)
        {
            details.Add($"{skippedDuplicatesInBatch} duplicados en lote");
        }

        return string.Join(" | ", details);
    }

    private async Task IngestClimateAsync(SismoDbContext dbContext, OpenMeteoClimateDataSource climateDataSource, CancellationToken cancellationToken)
    {
        var refreshIntervalMinutes = Math.Max(15, options.Value.Climate.RefreshIntervalMinutes);
        var refreshThresholdUtc = DateTimeOffset.UtcNow.AddMinutes(-refreshIntervalMinutes);
        var latestClimateSyncUtc = await dbContext.ClimateDailyObservations
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => (DateTimeOffset?)x.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestClimateSyncUtc is not null && latestClimateSyncUtc >= refreshThresholdUtc)
        {
            logger.LogInformation(
                "Fuente OpenMeteoClimate: se omite consulta porque la última sincronización fue hace menos de {RefreshIntervalMinutes} minutos.",
                refreshIntervalMinutes);
            return;
        }

        var climateLocations = options.Value.Climate.Locations.Count > 0
            ? options.Value.Climate.Locations
            : [new ClimateLocationOption
                {
                    Label = options.Value.Climate.LocationLabel,
                    Latitude = options.Value.Climate.Latitude,
                    Longitude = options.Value.Climate.Longitude
                }];
        var items = new List<ClimateDailyObservation>();
        foreach (var location in climateLocations)
        {
            var latestStoredDate = await dbContext.ClimateDailyObservations
                .AsNoTracking()
                .Where(x => x.LocationLabel == location.Label)
                .MaxAsync(x => (DateOnly?)x.ObservationDate, cancellationToken);
            var queryStartDate = latestStoredDate is null
                ? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-Math.Max(7, options.Value.Climate.HistoryDays)))
                : latestStoredDate.Value;
            var queryEndDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);

            var locationOptions = new ClimateIngestionOptions
            {
                Enabled = options.Value.Climate.Enabled,
                HistoryDays = options.Value.Climate.HistoryDays,
                RefreshIntervalMinutes = options.Value.Climate.RefreshIntervalMinutes,
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                LocationLabel = location.Label,
                Models = options.Value.Climate.Models,
                Locations = []
            };

            try
            {
                items.AddRange(await climateDataSource.GetDailyObservationsAsync(
                    locationOptions,
                    queryStartDate,
                    queryEndDate,
                    cancellationToken));
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning(
                    exception,
                    "Fuente OpenMeteoClimate: límite de consultas alcanzado para {LocationLabel}. Se reintentará en un ciclo posterior.",
                    location.Label);
            }
        }

        if (items.Count == 0)
        {
            logger.LogInformation("Fuente OpenMeteoClimate: sin observaciones nuevas en el ciclo actual.");
            return;
        }

        var incomingKeys = items
            .Select(x => new { x.Dataset, x.Model, x.Latitude, x.Longitude, x.ObservationDate })
            .ToList();
        var minDate = incomingKeys.Min(x => x.ObservationDate);
        var maxDate = incomingKeys.Max(x => x.ObservationDate);
        var models = incomingKeys.Select(x => x.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var existing = await dbContext.ClimateDailyObservations
            .Where(x => x.Dataset == "ClimateAPI"
                && models.Contains(x.Model)
                && x.ObservationDate >= minDate
                && x.ObservationDate <= maxDate)
            .ToListAsync(cancellationToken);

        var existingLookup = existing.ToDictionary(
            x => $"{x.Dataset}|{x.Model}|{x.Latitude:F5}|{x.Longitude:F5}|{x.ObservationDate:yyyy-MM-dd}",
            x => x,
            StringComparer.OrdinalIgnoreCase);

        var inserted = 0;
        var updated = 0;

        foreach (var item in items)
        {
            var key = $"{item.Dataset}|{item.Model}|{item.Latitude:F5}|{item.Longitude:F5}|{item.ObservationDate:yyyy-MM-dd}";
            if (!existingLookup.TryGetValue(key, out var existingItem))
            {
                await dbContext.ClimateDailyObservations.AddAsync(item, cancellationToken);
                inserted++;
                continue;
            }

            existingItem.LocationLabel = item.LocationLabel;
            existingItem.Temperature2mMean = item.Temperature2mMean;
            existingItem.Temperature2mMax = item.Temperature2mMax;
            existingItem.Temperature2mMin = item.Temperature2mMin;
            existingItem.PrecipitationSum = item.PrecipitationSum;
            existingItem.RainSum = item.RainSum;
            existingItem.SnowfallSum = item.SnowfallSum;
            existingItem.RelativeHumidity2mMean = item.RelativeHumidity2mMean;
            existingItem.RelativeHumidity2mMax = item.RelativeHumidity2mMax;
            existingItem.RelativeHumidity2mMin = item.RelativeHumidity2mMin;
            existingItem.WindSpeed10mMean = item.WindSpeed10mMean;
            existingItem.WindSpeed10mMax = item.WindSpeed10mMax;
            existingItem.CloudCoverMean = item.CloudCoverMean;
            existingItem.PressureMslMean = item.PressureMslMean;
            existingItem.SoilMoisture0To10cmMean = item.SoilMoisture0To10cmMean;
            existingItem.ShortwaveRadiationSum = item.ShortwaveRadiationSum;
            existingItem.RawPayload = item.RawPayload;
            existingItem.UpdatedAtUtc = DateTimeOffset.UtcNow;
            updated++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Fuente OpenMeteoClimate: {Inserted} insertados, {Updated} actualizados.",
            inserted,
            updated);
    }

    private async Task IngestGeomagneticAsync(
        SismoDbContext dbContext,
        UsgsGeomagnetismDataSource geomagnetismDataSource,
        CancellationToken cancellationToken)
    {
        var observatories = options.Value.Geomagnetic.Observatories;
        if (observatories.Count == 0)
        {
            logger.LogInformation("Fuente UsgsGeomagnetism: sin observatorios configurados.");
            return;
        }

        var items = new List<GeomagneticObservation>();
        foreach (var observatory in observatories)
        {
            var latestObservedAtUtc = await dbContext.GeomagneticObservations
                .AsNoTracking()
                .Where(x => x.ObservatoryCode == observatory.Code)
                .MaxAsync(x => (DateTimeOffset?)x.ObservedAtUtc, cancellationToken);
            var queryStartUtc = latestObservedAtUtc?.AddSeconds(-Math.Max(1, options.Value.Geomagnetic.SamplingPeriodSeconds))
                ?? DateTimeOffset.UtcNow.AddDays(-Math.Max(1, options.Value.Geomagnetic.HistoryDays));
            var queryEndUtc = DateTimeOffset.UtcNow;

            items.AddRange(await geomagnetismDataSource.GetObservationsAsync(
                observatory,
                options.Value.Geomagnetic,
                queryStartUtc,
                queryEndUtc,
                cancellationToken));
        }

        if (items.Count == 0)
        {
            logger.LogInformation("Fuente UsgsGeomagnetism: sin observaciones nuevas en el ciclo actual.");
            return;
        }

        var minObservedAt = items.Min(x => x.ObservedAtUtc);
        var maxObservedAt = items.Max(x => x.ObservedAtUtc);
        var observatoryCodes = items.Select(x => x.ObservatoryCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var existing = await dbContext.GeomagneticObservations
            .Where(x => x.ObservedAtUtc >= minObservedAt
                && x.ObservedAtUtc <= maxObservedAt
                && observatoryCodes.Contains(x.ObservatoryCode))
            .ToListAsync(cancellationToken);

        var existingLookup = existing.ToDictionary(
            x => $"{x.ObservatoryCode}|{x.ObservedAtUtc:O}|{x.SamplingPeriodSeconds}|{x.DataType}",
            StringComparer.OrdinalIgnoreCase);

        var inserted = 0;
        var updated = 0;
        foreach (var item in items)
        {
            var key = $"{item.ObservatoryCode}|{item.ObservedAtUtc:O}|{item.SamplingPeriodSeconds}|{item.DataType}";
            if (!existingLookup.TryGetValue(key, out var current))
            {
                await dbContext.GeomagneticObservations.AddAsync(item, cancellationToken);
                existingLookup[key] = item;
                inserted++;
                continue;
            }

            current.Provider = item.Provider;
            current.ObservatoryName = item.ObservatoryName;
            current.CountryCode = item.CountryCode;
            current.CountryName = item.CountryName;
            current.Latitude = item.Latitude;
            current.Longitude = item.Longitude;
            current.SourceFormat = item.SourceFormat;
            current.X = item.X;
            current.Y = item.Y;
            current.Z = item.Z;
            current.F = item.F;
            current.H = item.H;
            current.D = item.D;
            current.G = item.G;
            current.Dst = item.Dst;
            current.Dist = item.Dist;
            current.Sq = item.Sq;
            current.Sv = item.Sv;
            current.RawPayload = item.RawPayload;
            current.UpdatedAtUtc = DateTimeOffset.UtcNow;
            updated++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Fuente UsgsGeomagnetism: {Inserted} insertados, {Updated} actualizados.",
            inserted,
            updated);
    }
}

public sealed class OpenMeteoClimateDataSource(HttpClient httpClient)
{
    private const string DailyVariables =
        "temperature_2m_mean,temperature_2m_max,temperature_2m_min,precipitation_sum,rain_sum,snowfall_sum,relative_humidity_2m_mean,relative_humidity_2m_max,relative_humidity_2m_min,wind_speed_10m_mean,wind_speed_10m_max,cloud_cover_mean,pressure_msl_mean,soil_moisture_0_to_10cm_mean,shortwave_radiation_sum";

    public async Task<IReadOnlyList<ClimateDailyObservation>> GetDailyObservationsAsync(
        ClimateIngestionOptions options,
        DateOnly? startDateOverride,
        DateOnly? endDateOverride,
        CancellationToken cancellationToken)
    {
        var latitude = double.Parse(options.Latitude, CultureInfo.InvariantCulture);
        var longitude = double.Parse(options.Longitude, CultureInfo.InvariantCulture);
        var startDate = startDateOverride ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-Math.Max(7, options.HistoryDays)));
        var endDate = endDateOverride ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
        if (startDate > endDate)
        {
            return [];
        }

        var url = $"v1/climate?latitude={options.Latitude}&longitude={options.Longitude}&start_date={startDate:yyyy-MM-dd}&end_date={endDate:yyyy-MM-dd}&models={Uri.EscapeDataString(options.Models)}&daily={Uri.EscapeDataString(DailyVariables)}&timezone=GMT";
        using var response = await httpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        var document = JsonSerializer.Deserialize<OpenMeteoClimateResponse>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new OpenMeteoClimateResponse();
        var models = string.IsNullOrWhiteSpace(options.Models)
            ? ["EC_Earth3P_HR"]
            : options.Models.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<ClimateDailyObservation>();

        foreach (var model in models)
        {
            var daily = document.Daily;
            if (daily?.Time is null || daily.Time.Count == 0)
            {
                continue;
            }

            for (var index = 0; index < daily.Time.Count; index++)
            {
                if (!DateOnly.TryParse(daily.Time[index], CultureInfo.InvariantCulture, DateTimeStyles.None, out var observationDate))
                {
                    continue;
                }

                result.Add(new ClimateDailyObservation
                {
                    Provider = "OpenMeteo",
                    Dataset = "ClimateAPI",
                    Model = model,
                    Latitude = latitude,
                    Longitude = longitude,
                    LocationLabel = options.LocationLabel,
                    ObservationDate = observationDate,
                    Temperature2mMean = daily.ReadDouble($"temperature_2m_mean_{model}", index),
                    Temperature2mMax = daily.ReadDouble($"temperature_2m_max_{model}", index),
                    Temperature2mMin = daily.ReadDouble($"temperature_2m_min_{model}", index),
                    PrecipitationSum = daily.ReadDouble($"precipitation_sum_{model}", index),
                    RainSum = daily.ReadDouble($"rain_sum_{model}", index),
                    SnowfallSum = daily.ReadDouble($"snowfall_sum_{model}", index),
                    RelativeHumidity2mMean = daily.ReadDouble($"relative_humidity_2m_mean_{model}", index),
                    RelativeHumidity2mMax = daily.ReadDouble($"relative_humidity_2m_max_{model}", index),
                    RelativeHumidity2mMin = daily.ReadDouble($"relative_humidity_2m_min_{model}", index),
                    WindSpeed10mMean = daily.ReadDouble($"wind_speed_10m_mean_{model}", index),
                    WindSpeed10mMax = daily.ReadDouble($"wind_speed_10m_max_{model}", index),
                    CloudCoverMean = daily.ReadDouble($"cloud_cover_mean_{model}", index),
                    PressureMslMean = daily.ReadDouble($"pressure_msl_mean_{model}", index),
                    SoilMoisture0To10cmMean = daily.ReadDouble($"soil_moisture_0_to_10cm_mean_{model}", index),
                    ShortwaveRadiationSum = daily.ReadDouble($"shortwave_radiation_sum_{model}", index),
                    RawPayload = BuildClimateRowPayload(
                        model,
                        options.LocationLabel,
                        observationDate,
                        latitude,
                        longitude,
                        daily.ReadDouble($"temperature_2m_mean_{model}", index),
                        daily.ReadDouble($"precipitation_sum_{model}", index),
                        daily.ReadDouble($"pressure_msl_mean_{model}", index))
                });
            }
        }

        return result;
    }

    private static string BuildClimateRowPayload(
        string model,
        string locationLabel,
        DateOnly observationDate,
        double latitude,
        double longitude,
        double? temperature2mMean,
        double? precipitationSum,
        double? pressureMslMean)
    {
        return JsonSerializer.Serialize(new
        {
            model,
            locationLabel,
            observationDate,
            latitude,
            longitude,
            temperature2mMean,
            precipitationSum,
            pressureMslMean
        });
    }

    private sealed class OpenMeteoClimateResponse
    {
        public OpenMeteoClimateDaily? Daily { get; set; }
    }

    private sealed class OpenMeteoClimateDaily
    {
        public List<string> Time { get; set; } = [];
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Series { get; set; } = [];

        public double? ReadDouble(string key, int index)
        {
            if (!Series.TryGetValue(key, out var element)
                || element.ValueKind != JsonValueKind.Array
                || index < 0
                || index >= element.GetArrayLength())
            {
                return null;
            }

            var item = element[index];
            if (item.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return item.ValueKind == JsonValueKind.Number
                ? item.GetDouble()
                : double.TryParse(item.GetString(), CultureInfo.InvariantCulture, out var value)
                    ? value
                    : null;
        }
    }
}

public sealed class UsgsGeomagnetismDataSource(HttpClient httpClient)
{
    public async Task<IReadOnlyList<GeomagneticObservation>> GetObservationsAsync(
        GeomagneticObservatoryOption observatory,
        GeomagneticIngestionOptions options,
        DateTimeOffset startTimeUtc,
        DateTimeOffset endTimeUtc,
        CancellationToken cancellationToken)
    {
        if (startTimeUtc >= endTimeUtc)
        {
            return [];
        }

        var query = $"data/?id={Uri.EscapeDataString(observatory.Code)}" +
                    $"&format=json" +
                    $"&sampling_period={options.SamplingPeriodSeconds}" +
                    $"&type={Uri.EscapeDataString(options.DataType)}" +
                    $"&starttime={Uri.EscapeDataString(startTimeUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}" +
                    $"&endtime={Uri.EscapeDataString(endTimeUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}";

        if (!string.IsNullOrWhiteSpace(observatory.Elements))
        {
            query += $"&elements={Uri.EscapeDataString(observatory.Elements)}";
        }

        using var response = await httpClient.GetAsync(query, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        var document = JsonSerializer.Deserialize<UsgsGeomagnetismResponse>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new UsgsGeomagnetismResponse();
        if (document.Times.Count == 0 || document.Values.Count == 0)
        {
            return [];
        }

        var result = new List<GeomagneticObservation>(document.Times.Count);
        for (var index = 0; index < document.Times.Count; index++)
        {
            if (!DateTimeOffset.TryParse(document.Times[index], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var observedAtUtc))
            {
                continue;
            }

            var x = ReadGeomagneticValue(document.Values, "X", index);
            var y = ReadGeomagneticValue(document.Values, "Y", index);
            var z = ReadGeomagneticValue(document.Values, "Z", index);
            var f = ReadGeomagneticValue(document.Values, "F", index);
            var h = ReadGeomagneticValue(document.Values, "H", index);
            var d = ReadGeomagneticValue(document.Values, "D", index);
            var g = ReadGeomagneticValue(document.Values, "G", index);
            var dst = ReadGeomagneticValue(document.Values, "DST", index);
            var dist = ReadGeomagneticValue(document.Values, "DIST", index);
            var sq = ReadGeomagneticValue(document.Values, "SQ", index);
            var sv = ReadGeomagneticValue(document.Values, "SV", index);

            if (x is null && y is null && z is null && f is null && h is null && d is null && g is null && dst is null && dist is null && sq is null && sv is null)
            {
                continue;
            }

            result.Add(new GeomagneticObservation
            {
                Provider = "USGS",
                ObservatoryCode = observatory.Code,
                ObservatoryName = string.IsNullOrWhiteSpace(observatory.Name) ? observatory.Code : observatory.Name,
                CountryCode = observatory.CountryCode,
                CountryName = observatory.CountryName,
                Latitude = observatory.Latitude,
                Longitude = observatory.Longitude,
                ObservedAtUtc = observedAtUtc,
                SamplingPeriodSeconds = options.SamplingPeriodSeconds,
                DataType = options.DataType,
                SourceFormat = "json",
                X = x,
                Y = y,
                Z = z,
                F = f,
                H = h,
                D = d,
                G = g,
                Dst = dst,
                Dist = dist,
                Sq = sq,
                Sv = sv,
                RawPayload = JsonSerializer.Serialize(new
                {
                    observatory = observatory.Code,
                    observedAtUtc,
                    x,
                    y,
                    z,
                    f,
                    h,
                    d,
                    g,
                    dst,
                    dist,
                    sq,
                    sv
                })
            });
        }

        return result;
    }

    private static double? ReadGeomagneticValue(IReadOnlyList<UsgsGeomagnetismSeries> values, string id, int index)
    {
        var series = values.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (series is null || index < 0 || index >= series.NumericValues.Count)
        {
            return null;
        }

        return series.NumericValues[index];
    }
}

public sealed class UsgsGeomagnetismResponse
{
    [JsonPropertyName("times")]
    public List<string> Times { get; set; } = [];

    [JsonPropertyName("values")]
    public List<UsgsGeomagnetismSeries> Values { get; set; } = [];
}

public sealed class UsgsGeomagnetismSeries
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("values")]
    public List<double?> NumericValues { get; set; } = [];
}

public sealed class UsgsDataSource(HttpClient httpClient) : IEarthquakeDataSource
{
    public string Name => "USGS";
    private const string BaseUrl = "https://earthquake.usgs.gov/";

    public async Task<IReadOnlyList<ExternalEarthquakeDto>> GetRecentEarthquakesAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var startTime = (since ?? DateTimeOffset.UtcNow.AddDays(-1)).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var url = $"{BaseUrl}fdsnws/event/1/query?format=geojson&orderby=time&minmagnitude=1&starttime={Uri.EscapeDataString(startTime)}";
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var feed = JsonSerializer.Deserialize<UsgsFeed>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new UsgsFeed();

        return feed.Features
            .Where(x => x.Properties is not null && x.Geometry?.Coordinates?.Length >= 3)
            .Select(x => new ExternalEarthquakeDto(
                Name,
                x.Id ?? Guid.NewGuid().ToString("N"),
                DateTimeOffset.FromUnixTimeMilliseconds(x.Properties!.Time),
                x.Geometry!.Coordinates![1],
                x.Geometry.Coordinates[0],
                x.Geometry.Coordinates[2],
                x.Properties.Mag ?? 0,
                x.Properties.MagType ?? "unknown",
                x.Properties.Place ?? "Sin descripción",
                x.Properties.Detail ?? string.Empty,
                x.Properties.Status ?? "unknown",
                JsonSerializer.Serialize(x)))
            .ToList();
    }

    private sealed class UsgsFeed
    {
        public List<UsgsFeature> Features { get; set; } = [];
    }

    private sealed class UsgsFeature
    {
        public string? Id { get; set; }
        public UsgsProperties? Properties { get; set; }
        public UsgsGeometry? Geometry { get; set; }
    }

    private sealed class UsgsProperties
    {
        public double? Mag { get; set; }
        public string? Place { get; set; }
        public long Time { get; set; }
        public string? Status { get; set; }
        public string? MagType { get; set; }
        public string? Detail { get; set; }
    }

    private sealed class UsgsGeometry
    {
        public double[]? Coordinates { get; set; }
    }
}

public sealed class IgpDataSource(HttpClient httpClient) : IEarthquakeDataSource
{
    public string Name => "IGP";
    private const string BaseUrl = "https://ide.igp.gob.pe/";

    public async Task<IReadOnlyList<ExternalEarthquakeDto>> GetRecentEarthquakesAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}arcgis/rest/services/monitoreocensis/SismosReportados/MapServer/0/query?where={Uri.EscapeDataString("fechaevento IS NOT NULL")}&outFields=*&returnGeometry=true&orderByFields=fechaevento DESC&resultRecordCount=200&f=pjson";
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var feed = JsonSerializer.Deserialize<IgpArcGisQueryResponse>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new IgpArcGisQueryResponse();
        var threshold = since ?? DateTimeOffset.UtcNow.AddDays(-1);

        return feed.Features
            .Where(x => x.Attributes is not null && x.Geometry is not null)
            .Select(MapFeature)
            .Where(x => x is not null && x.OriginTimeUtc >= threshold)
            .Cast<ExternalEarthquakeDto>()
            .ToList();
    }

    private ExternalEarthquakeDto? MapFeature(IgpArcGisFeature feature)
    {
        var attributes = feature.Attributes;
        var geometry = feature.Geometry;
        if (attributes is null || geometry is null || attributes.FechaEvento is null)
        {
            return null;
        }

        var originTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(attributes.FechaEvento.Value);
        var depthKm = attributes.Prof ?? 0;
        var location = string.IsNullOrWhiteSpace(attributes.Ref)
            ? attributes.Department ?? "Sin descripción"
            : attributes.Ref;
        var sourceEventId = !string.IsNullOrWhiteSpace(attributes.Code)
            ? attributes.Code
            : $"igp-{attributes.ObjectId}";
        var qualityParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(attributes.Intensity))
        {
            qualityParts.Add(attributes.Intensity.Trim());
        }

        if (!string.IsNullOrWhiteSpace(attributes.DepthCategory))
        {
            qualityParts.Add(attributes.DepthCategory.Trim());
        }

        if (!string.IsNullOrWhiteSpace(attributes.Department))
        {
            qualityParts.Add(attributes.Department.Trim());
        }

        return new ExternalEarthquakeDto(
            Name,
            sourceEventId,
            originTimeUtc,
            geometry.Y,
            geometry.X,
            depthKm,
            attributes.Magnitude ?? 0,
            string.IsNullOrWhiteSpace(attributes.MagText) ? "M" : attributes.MagText,
            location,
            string.Join(" | ", qualityParts),
            attributes.ReportNumber is > 0 ? "reported" : "unknown",
            JsonSerializer.Serialize(feature));
    }

    private sealed class IgpArcGisQueryResponse
    {
        public List<IgpArcGisFeature> Features { get; set; } = [];
    }

    private sealed class IgpArcGisFeature
    {
        public IgpArcGisAttributes? Attributes { get; set; }
        public IgpArcGisGeometry? Geometry { get; set; }
    }

    private sealed class IgpArcGisAttributes
    {
        [JsonPropertyName("objectid")]
        public int ObjectId { get; set; }
        [JsonPropertyName("fechaevento")]
        public long? FechaEvento { get; set; }
        [JsonPropertyName("prof")]
        public int? Prof { get; set; }
        [JsonPropertyName("ref")]
        public string? Ref { get; set; }
        [JsonPropertyName("int_")]
        public string? Intensity { get; set; }
        [JsonPropertyName("profundidad")]
        public string? DepthCategory { get; set; }
        [JsonPropertyName("magnitud")]
        public double? Magnitude { get; set; }
        [JsonPropertyName("departamento")]
        public string? Department { get; set; }
        [JsonPropertyName("reporte")]
        public int? ReportNumber { get; set; }
        [JsonPropertyName("mag")]
        public string? MagText { get; set; }
        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }

    private sealed class IgpArcGisGeometry
    {
        [JsonPropertyName("x")]
        public double X { get; set; }
        [JsonPropertyName("y")]
        public double Y { get; set; }
    }
}

public sealed class IscDataSource(HttpClient httpClient) : IEarthquakeDataSource
{
    public string Name => "ISC";
    private const string BaseUrl = "https://www.isc.ac.uk/";
    private static readonly XNamespace QuakeMlNamespace = "http://quakeml.org/xmlns/bed/1.2";

    public async Task<IReadOnlyList<ExternalEarthquakeDto>> GetRecentEarthquakesAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var startTime = (since ?? DateTimeOffset.UtcNow.AddDays(-1)).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var url = $"{BaseUrl}fdsnws/event/1/query?format=xml&orderby=time&minmagnitude=1&starttime={Uri.EscapeDataString(startTime)}";
        using var response = await httpClient.GetAsync(url, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        var document = XDocument.Parse(payload);

        return document
            .Descendants(QuakeMlNamespace + "event")
            .Select(ParseEvent)
            .Where(x => x is not null)
            .Cast<ExternalEarthquakeDto>()
            .ToList();
    }

    private ExternalEarthquakeDto? ParseEvent(XElement eventElement)
    {
        var preferredOriginId = eventElement.Element(QuakeMlNamespace + "preferredOriginID")?.Value;
        var preferredMagnitudeId = eventElement.Element(QuakeMlNamespace + "preferredMagnitudeID")?.Value;

        var origin = eventElement
            .Elements(QuakeMlNamespace + "origin")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("publicID"), preferredOriginId, StringComparison.Ordinal))
            ?? eventElement.Elements(QuakeMlNamespace + "origin").FirstOrDefault();

        if (origin is null)
        {
            return null;
        }

        var originId = (string?)origin.Attribute("publicID");
        var magnitude = eventElement
            .Elements(QuakeMlNamespace + "magnitude")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("publicID"), preferredMagnitudeId, StringComparison.Ordinal))
            ?? eventElement.Elements(QuakeMlNamespace + "magnitude")
                .FirstOrDefault(x => string.Equals(x.Element(QuakeMlNamespace + "originID")?.Value, originId, StringComparison.Ordinal))
            ?? eventElement.Elements(QuakeMlNamespace + "magnitude").FirstOrDefault();

        var timeValue = origin.Element(QuakeMlNamespace + "time")?.Element(QuakeMlNamespace + "value")?.Value;
        var latitudeValue = origin.Element(QuakeMlNamespace + "latitude")?.Element(QuakeMlNamespace + "value")?.Value;
        var longitudeValue = origin.Element(QuakeMlNamespace + "longitude")?.Element(QuakeMlNamespace + "value")?.Value;
        var depthValue = origin.Element(QuakeMlNamespace + "depth")?.Element(QuakeMlNamespace + "value")?.Value;
        var magnitudeValue = magnitude?.Element(QuakeMlNamespace + "mag")?.Element(QuakeMlNamespace + "value")?.Value;

        if (!DateTimeOffset.TryParse(timeValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var originTimeUtc)
            || !double.TryParse(latitudeValue, CultureInfo.InvariantCulture, out var latitude)
            || !double.TryParse(longitudeValue, CultureInfo.InvariantCulture, out var longitude))
        {
            return null;
        }

        _ = double.TryParse(depthValue, CultureInfo.InvariantCulture, out var depthMeters);
        _ = double.TryParse(magnitudeValue, CultureInfo.InvariantCulture, out var magnitudeNumber);

        var location = eventElement
            .Elements(QuakeMlNamespace + "description")
            .Select(x => x.Element(QuakeMlNamespace + "text")?.Value)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            ?? "Sin descripción";

        var magnitudeType = magnitude?.Element(QuakeMlNamespace + "type")?.Value ?? "unknown";
        var sourceEventId = ExtractIscEventId((string?)eventElement.Attribute("publicID"));
        var quality = BuildQuality(origin, magnitude);
        var status = BuildStatus(eventElement);

        return new ExternalEarthquakeDto(
            Name,
            sourceEventId,
            originTimeUtc,
            latitude,
            longitude,
            depthMeters / 1000d,
            magnitudeNumber,
            magnitudeType,
            location,
            quality,
            status,
            eventElement.ToString(SaveOptions.DisableFormatting));
    }

    private static string ExtractIscEventId(string? publicId)
    {
        if (string.IsNullOrWhiteSpace(publicId))
        {
            return Guid.NewGuid().ToString("N");
        }

        const string marker = "evid=";
        var markerIndex = publicId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            return publicId[(markerIndex + marker.Length)..];
        }

        return publicId;
    }

    private static string BuildQuality(XElement origin, XElement? magnitude)
    {
        var agency = origin.Element(QuakeMlNamespace + "creationInfo")?.Element(QuakeMlNamespace + "agencyID")?.Value
            ?? magnitude?.Element(QuakeMlNamespace + "creationInfo")?.Element(QuakeMlNamespace + "author")?.Value
            ?? "ISC";
        var standardError = origin.Element(QuakeMlNamespace + "quality")?.Element(QuakeMlNamespace + "standardError")?.Value;
        var stationCount = origin.Element(QuakeMlNamespace + "quality")?.Element(QuakeMlNamespace + "associatedStationCount")?.Value;

        var details = new List<string> { agency };
        if (!string.IsNullOrWhiteSpace(stationCount))
        {
            details.Add($"stations:{stationCount}");
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            details.Add($"stderr:{standardError}");
        }

        return string.Join(" | ", details);
    }

    private static string BuildStatus(XElement eventElement)
    {
        var comment = eventElement
            .Elements(QuakeMlNamespace + "comment")
            .Select(x => x.Element(QuakeMlNamespace + "text")?.Value)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        if (!string.IsNullOrWhiteSpace(comment)
            && comment.Contains("not reviewed", StringComparison.OrdinalIgnoreCase))
        {
            return "preliminary";
        }

        return "reviewed";
    }
}
