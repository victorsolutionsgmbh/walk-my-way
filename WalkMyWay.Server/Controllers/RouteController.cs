using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WalkMyWay.Server;
using WalkMyWay.Server.Models;
using WalkMyWay.Server.Services;

namespace WalkMyWay.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class RouteController : ControllerBase
{
    private readonly RouteCalculationService _routeService;
    private readonly IMapProvider _mapProvider;
    private readonly ILogger<RouteController> _logger;

    public RouteController(RouteCalculationService routeService, IMapProvider mapProvider, ILogger<RouteController> logger)
    {
        _routeService = routeService;
        _mapProvider = mapProvider;
        _logger = logger;
    }

    [HttpGet("autocomplete")]
    public async Task<IActionResult> Autocomplete(
        [FromQuery] string input,
        [FromQuery] double? lat,
        [FromQuery] double? lng)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length < 2)
            return Ok(new { suggestions = Array.Empty<object>() });

        if (!lat.HasValue || !lng.HasValue)
            return BadRequest(new { error = "lat and lng are required for autocomplete." });

        try
        {
            var suggestions = await _mapProvider.GetPlaceAutocompleteSuggestionsAsync(input, lat, lng);
            return Ok(new { suggestions });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autocomplete failed for input '{Input}'", input);
            return StatusCode(500, new { error = "Autocomplete failed." });
        }
    }

    [HttpGet("address")]
    public async Task<IActionResult> GetAddress([FromQuery] double lat, [FromQuery] double lng)
    {
        try
        {
            var address = await _mapProvider.ReverseGeocodeAsync(lat, lng);
            return Ok(new { address });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reverse geocoding failed for ({Lat},{Lng})", lat, lng);
            return Ok(new { address = $"{lat:F6}, {lng:F6}" });
        }
    }

    [HttpGet("check-region")]
    public IActionResult CheckRegion()
    {
        // CF-IPCountry is set by Cloudflare. If absent (dev/localhost), allow access.
        var country = Request.Headers["CF-IPCountry"].FirstOrDefault();
        var allowed = string.IsNullOrEmpty(country) || country.Equals("AT", StringComparison.OrdinalIgnoreCase);
        return Ok(new { allowed, country = country ?? "unknown" });
    }

    [HttpPost]
    public async Task<IActionResult> FindRoute([FromBody] RouteRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (request.Preferences == null || !request.Preferences.Any())
            return BadRequest(new { error = "At least one stop must be specified." });

        if (request.Preferences.Any(p => p.Count < 1))
            return BadRequest(new { error = "Each stop must have a count of at least 1." });

        var totalStops = request.Preferences.Sum(p => p.Count);
        if (totalStops > AppConstants.MaxStops)
            return BadRequest(new { error = $"Total stops cannot exceed {AppConstants.MaxStops} (requested {totalStops})." });

        try
        {
            var result = await _routeService.GetWalkingRouteAsync(request);
            await Task.Delay(1640);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Route calculation failed for destination '{Destination}': {Message}",
                request.DestinationAddress, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calculating route for destination '{Destination}' from ({Lat},{Lng})",
                request.DestinationAddress, request.CurrentLatitude, request.CurrentLongitude);
            return StatusCode(500, new { error = "An unexpected error occurred while calculating the route." });
        }
    }
}
