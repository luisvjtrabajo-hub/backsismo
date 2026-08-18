using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SismoAI.Application;
using SismoAI.Infrastructure;

namespace SismoAI.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class EarthquakesController(
    IEarthquakeRepository earthquakeRepository,
    IMonitoringRepository monitoringRepository,
    SismoDbContext dbContext,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("recent")]
    public async Task<IActionResult> GetRecent([FromQuery] int count = 50, CancellationToken cancellationToken = default)
    {
        var items = await earthquakeRepository.GetRecentAsync(Math.Clamp(count, 1, 200), cancellationToken);
        return Ok(items);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int days = 7, CancellationToken cancellationToken = default)
    {
        var items = await earthquakeRepository.GetSinceAsync(DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 30)), cancellationToken);
        return Ok(items);
    }

    [HttpGet("sources")]
    public async Task<IActionResult> GetSources(CancellationToken cancellationToken)
    {
        var items = await monitoringRepository.GetSourceStatesAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("storage")]
    public async Task<IActionResult> GetStorage(CancellationToken cancellationToken)
    {
        var provider = dbContext.Database.ProviderName ?? "desconocido";
        var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);

        return Ok(new
        {
            provider,
            canConnect,
            ingestionEnabled = configuration.GetValue("Ingestion:Enabled", true),
            earthquakes = await dbContext.EarthquakeEvents.CountAsync(cancellationToken),
            climateDailyObservations = await dbContext.ClimateDailyObservations.CountAsync(cancellationToken),
            geomagneticObservations = await dbContext.GeomagneticObservations.CountAsync(cancellationToken),
            snapshots = await dbContext.AnomalySnapshots.CountAsync(cancellationToken),
            sources = await dbContext.SourceSyncStates.CountAsync(cancellationToken)
        });
    }
}
