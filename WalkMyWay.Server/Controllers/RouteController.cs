using Microsoft.AspNetCore.Mvc;
using WalkMyWay.Server.Models;
using WalkMyWay.Server.Services;

namespace WalkMyWay.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RouteController : ControllerBase
{
    private readonly GoogleMapsService _mapsService;

    public RouteController(GoogleMapsService mapsService)
    {
        _mapsService = mapsService;
    }

    [HttpGet("autocomplete")]
    public async Task<IActionResult> Autocomplete(
        [FromQuery] string input,
        [FromQuery] double? lat,
        [FromQuery] double? lng)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length < 2)
            return Ok(new { suggestions = Array.Empty<object>() });

        var suggestions = await _mapsService.GetPlaceAutocompleteSuggestionsAsync(input, lat, lng);
        return Ok(new { suggestions });
    }

    [HttpGet("address")]
    public async Task<IActionResult> GetAddress([FromQuery] double lat, [FromQuery] double lng)
    {
        var address = await _mapsService.ReverseGeocodeAsync(lat, lng);
        return Ok(new { address });
    }

    [HttpPost]
    public async Task<IActionResult> FindRoute([FromBody] RouteRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var result = await _mapsService.GetWalkingRouteAsync(request);
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
