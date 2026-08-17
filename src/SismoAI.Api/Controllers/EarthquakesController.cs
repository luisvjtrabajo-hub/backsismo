using Microsoft.AspNetCore.Mvc;
using SismoAI.Application;

namespace SismoAI.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class EarthquakesController(
    IEarthquakeRepository earthquakeRepository,
    IMonitoringRepository monitoringRepository) : ControllerBase
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
}
