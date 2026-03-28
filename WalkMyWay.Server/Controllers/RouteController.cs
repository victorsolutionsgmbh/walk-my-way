using Microsoft.AspNetCore.Mvc;
using WalkMyWay.Server;
using WalkMyWay.Server.Models;
using WalkMyWay.Server.Services;

namespace WalkMyWay.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RouteController : ControllerBase
{
    private readonly RouteCalculationService _routeService;
    private readonly IMapProvider _mapProvider;

    public RouteController(RouteCalculationService routeService, IMapProvider mapProvider)
    {
        _routeService = routeService;
        _mapProvider = mapProvider;
    }

    [HttpGet("autocomplete")]
    public async Task<IActionResult> Autocomplete(
        [FromQuery] string input,
        [FromQuery] double? lat,
        [FromQuery] double? lng)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length < 2)
            return Ok(new { suggestions = Array.Empty<object>() });

        var suggestions = await _mapProvider.GetPlaceAutocompleteSuggestionsAsync(input, lat, lng);
        return Ok(new { suggestions });
    }

    [HttpGet("address")]
    public async Task<IActionResult> GetAddress([FromQuery] double lat, [FromQuery] double lng)
    {
        var address = await _mapProvider.ReverseGeocodeAsync(lat, lng);
        return Ok(new { address });
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

        if (request.Preferences.Any(p => p.Count < 1))
            return BadRequest(new { error = "Each stop must have a count of at least 1." });

        var totalStops = request.Preferences.Sum(p => p.Count);
        if (totalStops > AppConstants.MaxStops)
            return BadRequest(new { error = $"Total stops cannot exceed {AppConstants.MaxStops} (requested {totalStops})." });

        try
        {
            var result = await _routeService.GetWalkingRouteAsync(request);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception)
        {
            return StatusCode(500, new { error = "An unexpected error occurred while calculating the route." });
        }
    }
}
