using System.Globalization;
using System.Text.Json;
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

public sealed class SismoDbContext(DbContextOptions<SismoDbContext> options) : DbContext(options)
{
    public DbSet<EarthquakeEvent> EarthquakeEvents => Set<EarthquakeEvent>();
    public DbSet<AnomalySnapshot> AnomalySnapshots => Set<AnomalySnapshot>();
    public DbSet<SourceSyncState> SourceSyncStates => Set<SourceSyncState>();

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
                options.UseNpgsql(postgres);
            }
            else
            {
                options.UseSqlite(sqlite);
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
            client.BaseAddress = new Uri("https://censis.igp.gob.pe/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<IrisDataSource>(client =>
        {
            client.BaseAddress = new Uri("https://service.earthscope.org/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<IEarthquakeDataSource, UsgsDataSource>();
        services.AddScoped<IEarthquakeDataSource, IgpDataSource>();
        services.AddScoped<IEarthquakeDataSource, IrisDataSource>();

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
        var events = await dbContext.EarthquakeEvents
            .Where(x => x.OriginTimeUtc >= sinceUtc)
            .ToListAsync(cancellationToken);

        foreach (var item in events)
        {
            item.AnomalyScore = anomalyScore;
            item.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public async Task<IReadOnlyList<EarthquakeEvent>> GetRecentAsync(int count, CancellationToken cancellationToken)
    {
        return await dbContext.EarthquakeEvents
            .OrderByDescending(x => x.OriginTimeUtc)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EarthquakeEvent>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        return await dbContext.EarthquakeEvents
            .Where(x => x.OriginTimeUtc >= sinceUtc)
            .OrderByDescending(x => x.OriginTimeUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EarthquakeEvent>> GetBetweenAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        return await dbContext.EarthquakeEvents
            .Where(x => x.OriginTimeUtc >= fromUtc && x.OriginTimeUtc <= toUtc)
            .OrderBy(x => x.OriginTimeUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
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

    public async Task<AnomalySnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        return await dbContext.AnomalySnapshots
            .OrderByDescending(x => x.CapturedAtUtc)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AnomalySnapshot>> GetRecentSnapshotsAsync(int count, CancellationToken cancellationToken)
    {
        return await dbContext.AnomalySnapshots
            .OrderByDescending(x => x.CapturedAtUtc)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
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
    IAnalyticsEngine analyticsEngine) : IDashboardService
{
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
            now);
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

        var latestBySource = await earthquakeRepository.GetLatestOriginBySourceAsync(cancellationToken);
        var lookbackSince = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, options.Value.QueryLookbackHours));

        foreach (var source in dataSources)
        {
            var lastKnown = latestBySource.TryGetValue(source.Name, out var value) ? value : null;
            var since = lastKnown?.AddMinutes(-5) ?? lookbackSince;
            var state = new SourceSyncState
            {
                SourceName = source.Name,
                LastAttemptUtc = DateTimeOffset.UtcNow
            };

            try
            {
                var items = await source.GetRecentEarthquakesAsync(since, cancellationToken);
                var inserted = 0;
                DateTimeOffset? newestOrigin = lastKnown;

                foreach (var item in items.OrderBy(x => x.OriginTimeUtc))
                {
                    if (await earthquakeRepository.ExistsAsync(item.Source, item.SourceEventId, cancellationToken))
                    {
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
                state.LastError = inserted == 0 ? "Sin eventos nuevos en el ciclo actual." : string.Empty;
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
            }

            await monitoringRepository.UpsertSourceStateAsync(state, cancellationToken);
            await monitoringRepository.SaveChangesAsync(cancellationToken);
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
}

public sealed class UsgsDataSource(HttpClient httpClient) : IEarthquakeDataSource
{
    public string Name => "USGS";

    public async Task<IReadOnlyList<ExternalEarthquakeDto>> GetRecentEarthquakesAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var startTime = (since ?? DateTimeOffset.UtcNow.AddDays(-1)).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var url = $"fdsnws/event/1/query?format=geojson&orderby=time&minmagnitude=1&starttime={Uri.EscapeDataString(startTime)}";
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

    public async Task<IReadOnlyList<ExternalEarthquakeDto>> GetRecentEarthquakesAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("api/news", cancellationToken);
        response.EnsureSuccessStatusCode();

        throw new InvalidOperationException(
            "IGP/CENSIS expone contenido web público, pero no se identificó una API sísmica pública estable y documentada para eventos recientes. El adapter quedó preparado y pendiente de una especificación oficial.");
    }
}

public sealed class IrisDataSource(HttpClient httpClient) : IEarthquakeDataSource
{
    public string Name => "IRIS";

    public async Task<IReadOnlyList<ExternalEarthquakeDto>> GetRecentEarthquakesAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("fdsnws/event/1/query?format=geojson&limit=1", cancellationToken);
        if ((int)response.StatusCode == 410)
        {
            throw new InvalidOperationException(
                "El endpoint EarthScope/IRIS consultado para eventos sísmicos responde 410 Gone. Se requiere una ruta oficial vigente para habilitar esta fuente.");
        }

        response.EnsureSuccessStatusCode();
        return [];
    }
}
