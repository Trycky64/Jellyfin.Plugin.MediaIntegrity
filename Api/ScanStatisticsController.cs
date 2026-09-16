using Jellyfin.Plugin.MediaIntegrity.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaIntegrity.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MediaIntegrity")]
public sealed class ScanStatisticsController : ControllerBase
{
    private readonly ScanStatisticsService _statistics;

    public ScanStatisticsController(ScanStatisticsService statistics)
    {
        _statistics = statistics;
    }

    [HttpGet("LastScan")]
    public async Task<IActionResult> GetLastScan(CancellationToken cancellationToken)
    {
        var stats = await _statistics.LoadAsync(cancellationToken);
        return stats is null ? NoContent() : Ok(stats);
    }
}
