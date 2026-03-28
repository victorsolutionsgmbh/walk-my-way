using WalkMyWay.Server.Models;

namespace WalkMyWay.Server.Services;

public interface IMapProvider
{
    Task<string> ReverseGeocodeAsync(double lat, double lng);
    Task<RouteData> GetWalkingRouteAsync(double originLat, double originLng, string destination);
    Task<List<PlaceCandidate>> SearchPlacesNearbyAsync(double lat, double lng, int radiusMeters, string type, bool openNow);
    Task<List<PlaceSuggestion>> GetPlaceAutocompleteSuggestionsAsync(string input, double? lat, double? lng);
    string BuildNavigationUrl(double originLat, double originLng, string destination, List<WaypointInfo> waypoints);
}

public record RouteData(
    List<(double Lat, double Lng)> Points,
    string EndAddress
);

public class PlaceSuggestion
{
    public string Description { get; set; } = string.Empty;
    public string PlaceId { get; set; } = string.Empty;
}

public record PlaceCandidate(
    string PlaceId,
    string Name,
    string Address,
    double Rating,
    int UserRatingsTotal,
    double Latitude,
    double Longitude,
    (double NeLat, double NeLng, double SwLat, double SwLng) Viewport
);
