using Microsoft.AspNetCore.Mvc;
using SismoAI.Application;

namespace SismoAI.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class DashboardController(IDashboardService dashboardService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DashboardSnapshotDto>> Get(CancellationToken cancellationToken)
    {
        var snapshot = await dashboardService.GetSnapshotAsync(includeMachineLearning: false, cancellationToken);
        return Ok(snapshot);
    }

    [HttpGet("ml")]
    public async Task<ActionResult<DashboardMachineLearningSnapshotDto>> GetMachineLearning(CancellationToken cancellationToken)
    {
        var snapshot = await dashboardService.GetMachineLearningSnapshotAsync(cancellationToken);
        return Ok(snapshot);
    }

    [HttpGet("features/country-daily")]
    public async Task<ActionResult<IReadOnlyList<CountryDailyFeatureDto>>> GetCountryDailyFeatures(
        [FromQuery] string countryCode = "PE",
        [FromQuery] int days = 365,
        CancellationToken cancellationToken = default)
    {
        var items = await dashboardService.GetCountryDailyFeaturesAsync(countryCode, days, cancellationToken);
        return Ok(items);
    }
}
