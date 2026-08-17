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
        var snapshot = await dashboardService.GetSnapshotAsync(cancellationToken);
        return Ok(snapshot);
    }
}
